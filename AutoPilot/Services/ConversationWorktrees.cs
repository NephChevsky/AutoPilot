using System.Diagnostics;

namespace AutoPilot.Services
{
    /// <summary>The dedicated git branch and worktree folder of one conversation.</summary>
    public sealed record ConversationWorkspace(string SessionId, string Directory, string Branch);

    /// <summary>
    /// Gives every conversation its own branch (<c>&lt;branchPrefix&gt;&lt;sessionId&gt;</c>) in its own git worktree
    /// (<c>&lt;worktreesDirectory&gt;\&lt;sessionId&gt;</c>), so conversations never touch the main working directory or each other.
    /// </summary>
    public sealed class ConversationWorktrees(string repositoryDirectory, string worktreesDirectory, string branchPrefix, ILogger<ConversationWorktrees>? logger = null)
    {
        private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
        private const int MaxDiffChars = 500_000;
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Branch name of a conversation.</summary>
        public string GetBranchName(string sessionId) => branchPrefix + ValidateId(sessionId);

        /// <summary>Worktree folder of a conversation.</summary>
        public string GetWorktreePath(string sessionId) => Path.GetFullPath(Path.Combine(worktreesDirectory, ValidateId(sessionId)));

        /// <summary>True when the conversation has a dedicated branch (false for legacy conversations and for ids that cannot have one).</summary>
        public async Task<bool> BranchExistsAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (!IsValidId(sessionId))
            {
                return false;
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await BranchExistsCoreAsync(GetBranchName(sessionId), cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Idempotently makes sure the branch and worktree of the conversation exist: creates both from the repository HEAD when
        /// neither exists, re-creates the worktree from an existing branch, or reuses a valid worktree.
        /// </summary>
        public async Task<ConversationWorkspace> EnsureAsync(string sessionId, CancellationToken cancellationToken)
        {
            var branch = GetBranchName(sessionId);
            var path = GetWorktreePath(sessionId);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await GitAsync(["rev-parse", "--verify", "HEAD"], cancellationToken, "The working directory is not a git repository with at least one commit");

                var registered = await IsRegisteredWorktreeAsync(path, cancellationToken);
                if (registered && Directory.Exists(path))
                {
                    return new ConversationWorkspace(sessionId, path, branch);
                }

                if (registered)
                {
                    await GitAsync(["worktree", "prune"], cancellationToken);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (await BranchExistsCoreAsync(branch, cancellationToken))
                {
                    await GitAsync(["worktree", "add", path, branch], cancellationToken);
                }
                else
                {
                    await GitAsync(["worktree", "add", "-b", branch, path, "HEAD"], cancellationToken);
                }

                logger?.LogInformation("Prepared worktree {Path} on branch {Branch}.", path, branch);
                return new ConversationWorkspace(sessionId, path, branch);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Diff of everything the conversation changed (committed, uncommitted and untracked non-ignored files of its worktree)
        /// relative to the merge-base of the repository HEAD and the conversation branch. Returns null when the id is invalid
        /// or the branch does not exist. Neither the main repository nor the worktree's index is modified.
        /// </summary>
        /// <exception cref="InvalidOperationException">A git command failed.</exception>
        public async Task<ConversationDiff?> GetDiffAsync(string sessionId, CancellationToken cancellationToken)
        {
            if (!IsValidId(sessionId))
            {
                return null;
            }

            var branch = GetBranchName(sessionId);
            var worktree = GetWorktreePath(sessionId);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!await BranchExistsCoreAsync(branch, cancellationToken))
                {
                    return null;
                }

                var baseCommit = (await GitAsync(["merge-base", "HEAD", $"refs/heads/{branch}"], cancellationToken)).Trim();
                var target = Directory.Exists(worktree)
                    ? await SnapshotWorktreeAsync(worktree, cancellationToken)
                    : $"refs/heads/{branch}";

                var raw = await GitAsync(["-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "-M", baseCommit, target], cancellationToken);
                var truncated = false;
                if (raw.Length > MaxDiffChars)
                {
                    var cut = raw.LastIndexOf('\n', MaxDiffChars - 1);
                    raw = cut > 0 ? raw[..cut] : raw[..MaxDiffChars];
                    truncated = true;
                }

                return new ConversationDiff(branch, baseCommit, UnifiedDiffParser.Parse(raw), truncated);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Writes the worktree's full content to a tree object using a temporary copy of its index.</summary>
        private async Task<string> SnapshotWorktreeAsync(string worktree, CancellationToken cancellationToken)
        {
            var tempIndex = Path.Combine(Path.GetTempPath(), $"autopilot-{Guid.NewGuid():N}.index");
            try
            {
                var realIndex = (await GitAsync(["rev-parse", "--path-format=absolute", "--git-path", "index"], cancellationToken, workingDirectory: worktree)).Trim();
                if (File.Exists(realIndex))
                {
                    File.Copy(realIndex, tempIndex);
                }

                await GitAsync(["add", "--all"], cancellationToken, workingDirectory: worktree, indexFile: tempIndex);
                return (await GitAsync(["write-tree"], cancellationToken, workingDirectory: worktree, indexFile: tempIndex)).Trim();
            }
            finally
            {
                File.Delete(tempIndex);
            }
        }

        private async Task<bool> BranchExistsCoreAsync(string branch, CancellationToken cancellationToken) =>
            (await RunGitAsync(["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken)).ExitCode == 0;

        private async Task<bool> IsRegisteredWorktreeAsync(string path, CancellationToken cancellationToken)
        {
            var output = await GitAsync(["worktree", "list", "--porcelain"], cancellationToken);
            return output.Split('\n')
                .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
                .Select(line => NormalizePath(line["worktree ".Length..].Trim()))
                .Contains(NormalizePath(path), StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool IsValidId(string? sessionId) =>
            !string.IsNullOrEmpty(sessionId)
            && !sessionId.Contains("..", StringComparison.Ordinal)
            && sessionId.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-');

        private static string ValidateId(string sessionId) =>
            IsValidId(sessionId)
                ? sessionId
                : throw new ArgumentException("The session id must be non-empty and contain only letters, digits, '.', '_' or '-' (no '..').", nameof(sessionId));

        private async Task<string> GitAsync(string[] arguments, CancellationToken cancellationToken, string? failureHint = null, string? workingDirectory = null, string? indexFile = null)
        {
            var result = await RunGitAsync(arguments, cancellationToken, workingDirectory, indexFile);
            if (result.TimedOut || result.ExitCode != 0)
            {
                var detail = $"git {string.Join(' ', arguments)} failed ({(result.TimedOut ? "timeout" : result.ExitCode)}): {result.StdErr.Trim()}";
                throw new InvalidOperationException(failureHint is null ? detail : $"{failureHint}. {detail}");
            }

            return result.StdOut;
        }

        private Task<WorkspaceTools.ProcessResult> RunGitAsync(string[] arguments, CancellationToken cancellationToken, string? workingDirectory = null, string? indexFile = null)
        {
            var startInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory ?? repositoryDirectory };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (indexFile is not null)
            {
                startInfo.Environment["GIT_INDEX_FILE"] = indexFile;
            }

            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            return WorkspaceTools.RunProcessAsync(startInfo, GitTimeout, cancellationToken);
        }
    }
}
