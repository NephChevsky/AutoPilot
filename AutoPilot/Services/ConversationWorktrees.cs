using System.Diagnostics;

namespace AutoPilot.Services
{
    /// <summary>Outcome of <see cref="ConversationWorktrees.MergeAsync"/>.</summary>
    public enum ConversationMergeStatus { Merged, CleanupFailed, PushFailed, Conflict, Rejected }

    /// <summary>Result of merging a conversation branch; <paramref name="TargetBranch"/> and <paramref name="MergeCommit"/> are set once merged locally.</summary>
    public sealed record ConversationMergeResult(ConversationMergeStatus Status, string Message, string? TargetBranch = null, string? MergeCommit = null);

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

        /// <summary>
        /// Merges the conversation branch (<c>--no-ff</c>) into the branch checked out in the repository directory, pushes that
        /// branch to <paramref name="remote"/> (never forced) and deletes the conversation's worktree and local branch.
        /// Git failures and invalid input are reported through the result; only cancellation throws.
        /// </summary>
        public async Task<ConversationMergeResult> MergeAsync(string sessionId, string remote, CancellationToken cancellationToken)
        {
            if (!IsValidId(sessionId))
            {
                return Rejected("The conversation id is invalid; nothing was merged.");
            }

            if (string.IsNullOrEmpty(remote) || remote[0] == '-' || remote.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return Rejected("The configured git remote name is invalid (it must be non-empty, must not start with '-' and must not contain whitespace or control characters); nothing was merged.");
            }

            var branch = GetBranchName(sessionId);
            var worktree = GetWorktreePath(sessionId);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!await BranchExistsCoreAsync(branch, cancellationToken))
                {
                    return Rejected($"Branch '{branch}' does not exist; nothing was merged.");
                }

                var remoteUrl = await RunGitAsync(["remote", "get-url", remote], cancellationToken);
                if (remoteUrl.TimedOut || remoteUrl.ExitCode != 0)
                {
                    return Rejected($"Git remote '{remote}' is not configured; nothing was merged. {remoteUrl.StdErr.Trim()}");
                }

                var head = await RunGitAsync(["symbolic-ref", "--short", "HEAD"], cancellationToken);
                var target = head.StdOut.Trim();
                if (head.TimedOut || head.ExitCode != 0 || target.Length == 0)
                {
                    return Rejected("The main working directory is on a detached HEAD; check out a branch first. Nothing was merged.");
                }

                if (string.Equals(target, branch, StringComparison.Ordinal))
                {
                    return Rejected($"The main working directory has the conversation branch '{branch}' checked out; nothing was merged.");
                }

                var mergeHead = await RunGitAsync(["rev-parse", "-q", "--verify", "MERGE_HEAD"], cancellationToken);
                if (!mergeHead.TimedOut && mergeHead.ExitCode == 0)
                {
                    return Rejected("A merge is already in progress in the main working directory; finish or abort it first. Nothing was merged.");
                }

                var mainStatus = await RunGitAsync(["status", "--porcelain", "--untracked-files=no"], cancellationToken);
                if (mainStatus.TimedOut || mainStatus.ExitCode != 0)
                {
                    return Rejected($"Could not read the status of the main working directory: {mainStatus.StdErr.Trim()}");
                }

                if (mainStatus.StdOut.Trim().Length > 0)
                {
                    return Rejected($"The main working directory has uncommitted changes to tracked files; commit or stash them first. Nothing was merged.");
                }

                if (Directory.Exists(worktree))
                {
                    var worktreeStatus = await RunGitAsync(["status", "--porcelain"], cancellationToken, worktree);
                    if (worktreeStatus.TimedOut || worktreeStatus.ExitCode != 0)
                    {
                        return Rejected($"Could not read the status of the conversation worktree: {worktreeStatus.StdErr.Trim()}");
                    }

                    if (worktreeStatus.StdOut.Trim().Length > 0)
                    {
                        return Rejected("The conversation worktree has uncommitted or untracked changes; commit the changes first. Nothing was merged.");
                    }
                }

                // Cancellation is honoured only up to here; from now on the repository is mutated and every git
                // command must run to completion (each still has GitTimeout) so it is never left mid-merge.
                cancellationToken.ThrowIfCancellationRequested();
                var none = CancellationToken.None;

                var merge = await RunGitAsync(["merge", "--no-ff", "--no-edit", branch], none);
                if (merge.TimedOut || merge.ExitCode != 0)
                {
                    var conflicts = await RunGitAsync(["diff", "--name-only", "--diff-filter=U"], none);
                    var files = conflicts.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var abort = await RunGitAsync(["merge", "--abort"], none);
                    var stillMerging = false;
                    if (abort.TimedOut || abort.ExitCode != 0)
                    {
                        // Aborting fails harmlessly when git refused to start the merge at all; only a lingering MERGE_HEAD is a problem.
                        var lingering = await RunGitAsync(["rev-parse", "-q", "--verify", "MERGE_HEAD"], none);
                        stillMerging = lingering.TimedOut || lingering.ExitCode == 0;
                    }

                    logger?.LogWarning("Merging {Branch} into {Target} failed ({Conflicts} conflicting files, abort failed: {StillMerging}).", branch, target, files.Length, stillMerging);
                    var status = files.Length > 0 ? ConversationMergeStatus.Conflict : ConversationMergeStatus.Rejected;
                    if (stillMerging)
                    {
                        return new ConversationMergeResult(status,
                            $"Merging '{branch}' into '{target}' failed and 'git merge --abort' did not succeed ({(abort.TimedOut ? "timed out" : abort.StdErr.Trim())}); the main working directory may still be mid-merge. Run 'git merge --abort' manually in it. {(files.Length > 0 ? "Conflicting files: " + string.Join(", ", files) : string.Empty)}".TrimEnd(), target);
                    }

                    if (files.Length > 0)
                    {
                        return new ConversationMergeResult(ConversationMergeStatus.Conflict,
                            $"Merging '{branch}' into '{target}' hit conflicts and was aborted; nothing changed. Conflicting files: {string.Join(", ", files)}", target);
                    }

                    var detail = (merge.StdErr.Trim() + " " + merge.StdOut.Trim()).Trim();
                    return Rejected($"Merging '{branch}' into '{target}' failed; nothing changed. {(merge.TimedOut ? "Timed out." : detail)}", target);
                }

                var commit = (await RunGitAsync(["rev-parse", "HEAD"], none)).StdOut.Trim();

                var push = await RunGitAsync(["push", remote, $"HEAD:refs/heads/{target}"], none);
                if (push.TimedOut || push.ExitCode != 0)
                {
                    logger?.LogWarning("Pushing {Target} to {Remote} failed after merging {Branch}.", target, remote, branch);
                    return new ConversationMergeResult(ConversationMergeStatus.PushFailed,
                        $"Merged '{branch}' into '{target}' (commit {commit}) but pushing to '{remote}' failed; the merge, branch and worktree were kept and you can retry. {(push.TimedOut ? "Timed out." : push.StdErr.Trim())}",
                        target, commit);
                }

                try
                {
                    if (await IsRegisteredWorktreeAsync(worktree, none))
                    {
                        await GitAsync(["worktree", "remove", "--force", worktree], none);
                    }

                    await GitAsync(["worktree", "prune"], none);
                    await GitAsync(["branch", "-d", branch], none);
                    if (Directory.Exists(worktree))
                    {
                        throw new IOException($"The worktree folder '{worktree}' still exists.");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning(ex, "Cleanup of {Branch} failed after a successful merge and push.", branch);
                    return new ConversationMergeResult(ConversationMergeStatus.CleanupFailed,
                        $"Merged '{branch}' into '{target}' (commit {commit}) and pushed to '{remote}', but cleanup failed: {ex.Message}", target, commit);
                }

                logger?.LogInformation("Merged {Branch} into {Target} ({Commit}), pushed to {Remote} and cleaned up.", branch, target, commit, remote);
                return new ConversationMergeResult(ConversationMergeStatus.Merged,
                    $"Merged '{branch}' into '{target}' (commit {commit}), pushed to '{remote}' and deleted the worktree and local branch.", target, commit);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                logger?.LogError(ex, "Merging {Branch} failed.", branch);
                return Rejected($"Merge failed: {ex.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }

        private static ConversationMergeResult Rejected(string message, string? target = null) =>
            new(ConversationMergeStatus.Rejected, message, target);

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
