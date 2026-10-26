using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AutoPilot.Services
{
    /// <summary>
    /// Host-implemented tools referenced by the agent definitions: <c>get_diff</c> and <c>commit_and_push</c>.
    /// One instance per conversation, so the diff baseline is the state of the workspace when the run started.
    /// </summary>
    public sealed class WorkspaceTools(string workingDirectory, string? branch = null, string remote = "origin")
    {
        private const int MaxDiffChars = 120_000;
        private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

        private string? _baselineTree;
        private string? _baselineHead;
        private string? _baselineError;

        public async Task CaptureBaselineAsync(CancellationToken cancellationToken)
        {
            try
            {
                _baselineTree = await SnapshotTreeAsync(cancellationToken);
                // Recorded so preExisting stays correct after commits move HEAD.
                _baselineHead = (await TryGitAsync(["rev-parse", "--verify", "HEAD"], cancellationToken)) is { ExitCode: 0 } head ? head.StdOut.Trim() : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _baselineError = ex.Message;
            }
        }

        public IList<AIFunctionDeclaration> CreateTools() =>
        [
            CopilotTool.DefineTool(
                ([Description("When true, return the uncommitted changes that already existed before the run started (HEAD vs baseline) instead of the changes made during the run.")] bool preExisting = false,
                 CancellationToken cancellationToken = default) => GetDiffAsync(preExisting, cancellationToken),
                new CopilotToolOptions { SkipPermission = true },
                new AIFunctionFactoryOptions
                {
                    Name = "get_diff",
                    Description = "Returns the current changes in the workspace relative to the baseline recorded when the run started. Pre-existing user changes are excluded.",
                }),
            CopilotTool.DefineTool(
                ([Description("Concise commit message describing the change.")] string message,
                 CancellationToken cancellationToken = default) => CommitAndPushAsync(message, cancellationToken),
                new CopilotToolOptions { SkipPermission = true },
                new AIFunctionFactoryOptions
                {
                    Name = "commit_and_push",
                    Description = "Commits all changes in the conversation's worktree with the given message and pushes the conversation's dedicated branch to the configured remote. Never forces and never pushes another branch. Returns STATUS: PUSHED with branch and commit, or STATUS: FAILED with the error.",
                }),
        ];

        /// <summary>Commits everything in the worktree (if anything changed) and pushes the conversation branch. Never throws for git failures.</summary>
        public async Task<string> CommitAndPushAsync(string message, CancellationToken cancellationToken)
        {
            if (branch is null)
            {
                return "STATUS: FAILED\nThis conversation has no dedicated branch (it was created before per-conversation branches); nothing was committed or pushed.";
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return "STATUS: FAILED\nThe commit message must not be empty.";
            }

            if (string.IsNullOrEmpty(remote) || remote[0] == '-' || remote.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return "STATUS: FAILED\nThe configured git remote name is invalid (it must be non-empty, must not start with '-' and must not contain whitespace or control characters); nothing was committed.";
            }

            try
            {
                var remoteUrl = await TryGitAsync(["remote", "get-url", remote], cancellationToken);
                if (remoteUrl.ExitCode != 0)
                {
                    return $"STATUS: FAILED\nGit remote '{remote}' is not configured; nothing was committed.\n{remoteUrl.StdErr.Trim()}";
                }

                var current = await TryGitAsync(["symbolic-ref", "--short", "HEAD"], cancellationToken);
                if (current.ExitCode != 0 || current.StdOut.Trim() != branch)
                {
                    return $"STATUS: FAILED\nThe worktree is not on the conversation branch '{branch}' (current: '{current.StdOut.Trim()}'); nothing was committed.";
                }

                var add = await TryGitAsync(["add", "--all"], cancellationToken);
                if (add.ExitCode != 0)
                {
                    return $"STATUS: FAILED\ngit add failed: {Describe(add)}";
                }

                var staged = await TryGitAsync(["diff", "--cached", "--quiet"], cancellationToken);
                if (staged.ExitCode > 1 || staged.TimedOut)
                {
                    return $"STATUS: FAILED\ngit diff --cached failed: {Describe(staged)}";
                }

                if (staged.ExitCode == 1)
                {
                    var commit = await TryGitAsync(["commit", "-m", message], cancellationToken);
                    if (commit.ExitCode != 0)
                    {
                        return $"STATUS: FAILED\ngit commit failed: {Describe(commit)}";
                    }
                }

                var head = await TryGitAsync(["rev-parse", "HEAD"], cancellationToken);
                var sha = head.ExitCode == 0 ? head.StdOut.Trim() : "unknown";

                var push = await TryGitAsync(["push", "-u", remote, $"HEAD:refs/heads/{branch}"], cancellationToken);
                if (push.ExitCode != 0)
                {
                    return $"STATUS: FAILED\ngit push failed (the local commit {sha} is retained on branch {branch}): {Describe(push)}";
                }

                return $"STATUS: PUSHED\nBranch: {branch}\nRemote: {remote}\nCommit: {sha}{(staged.ExitCode == 0 ? "\n(No new changes to commit; pushed the existing branch head.)" : "")}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"STATUS: FAILED\n{ex.Message}";
            }
        }

        private static string Describe(ProcessResult result) =>
            result.TimedOut ? "timed out" : $"exit code {result.ExitCode}: {result.Combined.Trim()}";

        public async Task<string> GetDiffAsync(bool preExisting, CancellationToken cancellationToken)
        {
            if (_baselineTree is null)
            {
                return $"get_diff is unavailable: no baseline could be recorded for '{workingDirectory}'. {_baselineError}";
            }

            string from, to;
            if (preExisting)
            {
                from = _baselineHead ?? "HEAD";
                to = _baselineTree;
            }
            else
            {
                from = _baselineTree;
                to = await SnapshotTreeAsync(cancellationToken);
                if (to == _baselineTree)
                {
                    return "No changes relative to the baseline.";
                }
            }

            var stat = await GitAsync(["diff", "--no-color", "--stat", from, to], null, cancellationToken);
            if (string.IsNullOrWhiteSpace(stat))
            {
                return preExisting ? "No pre-existing uncommitted changes." : "No changes relative to the baseline.";
            }

            var diff = await GitAsync(["diff", "--no-color", from, to], null, cancellationToken);
            var result = $"{stat}\n{diff}";
            return result.Length <= MaxDiffChars
                ? result
                : $"{result[..MaxDiffChars]}\n... [diff truncated, {result.Length - MaxDiffChars} more characters]";
        }

        /// <summary>
        /// Writes the full working tree (tracked and untracked, honoring .gitignore) to a git tree object
        /// using a temporary index, so the repository's real index and working tree are left untouched.
        /// </summary>
        private async Task<string> SnapshotTreeAsync(CancellationToken cancellationToken)
        {
            var tempIndex = Path.Combine(Path.GetTempPath(), $"autopilot-{Guid.NewGuid():N}.index");
            try
            {
                var realIndex = (await GitAsync(["rev-parse", "--path-format=absolute", "--git-path", "index"], null, cancellationToken)).Trim();
                if (File.Exists(realIndex))
                {
                    File.Copy(realIndex, tempIndex);
                }

                await GitAsync(["add", "--all"], tempIndex, cancellationToken);
                return (await GitAsync(["write-tree"], tempIndex, cancellationToken)).Trim();
            }
            finally
            {
                File.Delete(tempIndex);
            }
        }

        private async Task<string> GitAsync(string[] arguments, string? indexFile, CancellationToken cancellationToken)
        {
            var result = await TryGitAsync(arguments, cancellationToken, indexFile);
            if (result.TimedOut || result.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({(result.TimedOut ? "timeout" : result.ExitCode)}): {result.StdErr.Trim()}");
            }

            return result.StdOut;
        }

        private Task<ProcessResult> TryGitAsync(string[] arguments, CancellationToken cancellationToken, string? indexFile = null)
        {
            var startInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (indexFile is not null)
            {
                startInfo.Environment["GIT_INDEX_FILE"] = indexFile;
            }

            // Never block waiting for credentials.
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            return RunProcessAsync(startInfo, GitTimeout, cancellationToken);
        }

        internal static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();
            var combined = new StringBuilder();

            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => Append(e.Data, stdOut);
            process.ErrorDataReceived += (_, e) => Append(e.Data, stdErr);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                cancellationToken.ThrowIfCancellationRequested();
                lock (combined)
                {
                    return new ProcessResult(-1, stdOut.ToString(), stdErr.ToString(), combined.ToString(), TimedOut: true);
                }
            }

            lock (combined)
            {
                return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString(), combined.ToString(), TimedOut: false);
            }

            void Append(string? line, StringBuilder target)
            {
                if (line is null)
                {
                    return;
                }

                lock (combined)
                {
                    target.AppendLine(line);
                    combined.AppendLine(line);
                }
            }
        }

        internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, string Combined, bool TimedOut);
    }
}
