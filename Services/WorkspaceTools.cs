using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AutoPilot.Services
{
    /// <summary>
    /// Host-implemented tools referenced by the agent definitions: <c>get_diff</c> and <c>run_verification</c>.
    /// One instance per conversation, so the diff baseline is the state of the workspace when the run started.
    /// </summary>
    public sealed class WorkspaceTools(string workingDirectory, IReadOnlyList<string> verificationCommands, TimeSpan verificationTimeout)
    {
        private const int MaxDiffChars = 120_000;
        private const int MaxCommandOutputChars = 12_000;
        private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

        private string? _baselineTree;
        private string? _baselineError;

        public async Task CaptureBaselineAsync(CancellationToken cancellationToken)
        {
            try
            {
                _baselineTree = await SnapshotTreeAsync(cancellationToken);
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
                (CancellationToken cancellationToken = default) => RunVerificationAsync(cancellationToken),
                new CopilotToolOptions { SkipPermission = true },
                new AIFunctionFactoryOptions
                {
                    Name = "run_verification",
                    Description = "Runs the repository's configured build and test commands and returns the real exit codes and output. The result is the authoritative verification outcome.",
                }),
        ];

        private async Task<string> GetDiffAsync(bool preExisting, CancellationToken cancellationToken)
        {
            if (_baselineTree is null)
            {
                return $"get_diff is unavailable: no baseline could be recorded for '{workingDirectory}'. {_baselineError}";
            }

            string from, to;
            if (preExisting)
            {
                from = "HEAD";
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

        private async Task<string> RunVerificationAsync(CancellationToken cancellationToken)
        {
            if (verificationCommands.Count == 0)
            {
                return "STATUS: NOT_CONFIGURED\nNo verification commands are configured (Settings:VerificationCommands).";
            }

            var report = new StringBuilder();
            var passed = true;
            foreach (var command in verificationCommands)
            {
                var startInfo = OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"")
                    : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
                startInfo.WorkingDirectory = workingDirectory;

                var result = await RunProcessAsync(startInfo, verificationTimeout, cancellationToken);
                var succeeded = !result.TimedOut && result.ExitCode == 0;

                report.AppendLine($"## {command}")
                    .AppendLine($"Exit code: {(result.TimedOut ? $"timed out after {verificationTimeout}" : result.ExitCode)}")
                    .AppendLine("Output (tail):")
                    .AppendLine(Tail(result.Combined, MaxCommandOutputChars))
                    .AppendLine();

                if (!succeeded)
                {
                    passed = false;
                    report.AppendLine("Stopped: remaining commands were not run.");
                    break;
                }
            }

            return $"STATUS: {(passed ? "PASSED" : "FAILED")}\n\n{report}";
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
            var startInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (indexFile is not null)
            {
                startInfo.Environment["GIT_INDEX_FILE"] = indexFile;
            }

            var result = await RunProcessAsync(startInfo, GitTimeout, cancellationToken);
            if (result.TimedOut || result.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({(result.TimedOut ? "timeout" : result.ExitCode)}): {result.StdErr.Trim()}");
            }

            return result.StdOut;
        }

        private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
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

        private static string Tail(string text, int maxChars) =>
            text.Length <= maxChars ? text : $"... [{text.Length - maxChars} earlier characters omitted]\n{text[^maxChars..]}";

        private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, string Combined, bool TimedOut);
    }
}
