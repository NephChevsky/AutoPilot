using System.Diagnostics;
using AutoPilot.Services;

namespace AutoPilot.Tests
{
    /// <summary>Temporary git repository with a local bare remote and a worktrees folder.</summary>
    internal sealed class GitFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "autopilot-git-" + Guid.NewGuid().ToString("N"));

        public GitFixture(bool withRemote = true)
        {
            Directory.CreateDirectory(_root);
            Repo = Path.Combine(_root, "repo");
            Remote = Path.Combine(_root, "remote.git");
            Worktrees = Path.Combine(_root, "repo.worktrees");
            Directory.CreateDirectory(Repo);

            Git(_root, "init", "--bare", Remote);
            Git(Repo, "init", "-b", "main");
            Git(Repo, "config", "user.name", "Test");
            Git(Repo, "config", "user.email", "test@example.com");
            Git(Repo, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(Repo, "readme.txt"), "hello");
            Git(Repo, "add", "--all");
            Git(Repo, "commit", "-m", "initial");
            if (withRemote)
            {
                Git(Repo, "remote", "add", "origin", Remote);
            }
        }

        public string Repo { get; }

        public string Remote { get; }

        public string Worktrees { get; }

        public ConversationWorktrees CreateWorktrees() => new(Repo, Worktrees, "autopilot/");

        public static string Git(string directory, params string[] args)
        {
            var startInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
            }

            return stdout.Result.Trim();
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public class ConversationWorktreesTests
    {
        [Fact]
        public async Task EnsureAsync_creates_branch_and_worktree_from_head()
        {
            using var git = new GitFixture();
            var worktrees = git.CreateWorktrees();

            var workspace = await worktrees.EnsureAsync("abc-123", CancellationToken.None);

            Assert.Equal("autopilot/abc-123", workspace.Branch);
            Assert.Equal(Path.Combine(git.Worktrees, "abc-123"), workspace.Directory);
            Assert.True(File.Exists(Path.Combine(workspace.Directory, "readme.txt")));
            Assert.Equal("autopilot/abc-123", GitFixture.Git(workspace.Directory, "rev-parse", "--abbrev-ref", "HEAD"));
            Assert.Equal(GitFixture.Git(git.Repo, "rev-parse", "HEAD"), GitFixture.Git(workspace.Directory, "rev-parse", "HEAD"));
            Assert.True(await worktrees.BranchExistsAsync("abc-123", CancellationToken.None));
        }

        [Fact]
        public async Task EnsureAsync_is_idempotent()
        {
            using var git = new GitFixture();
            var worktrees = git.CreateWorktrees();

            var first = await worktrees.EnsureAsync("s1", CancellationToken.None);
            File.WriteAllText(Path.Combine(first.Directory, "work.txt"), "x");
            var second = await worktrees.EnsureAsync("s1", CancellationToken.None);

            Assert.Equal(first, second);
            Assert.True(File.Exists(Path.Combine(second.Directory, "work.txt")));
        }

        [Fact]
        public async Task EnsureAsync_recreates_missing_worktree_folder_from_existing_branch()
        {
            using var git = new GitFixture();
            var worktrees = git.CreateWorktrees();
            var first = await worktrees.EnsureAsync("s2", CancellationToken.None);
            File.WriteAllText(Path.Combine(first.Directory, "kept.txt"), "x");
            GitFixture.Git(first.Directory, "add", "--all");
            GitFixture.Git(first.Directory, "commit", "-m", "work");
            ForceDelete(first.Directory);

            var again = await worktrees.EnsureAsync("s2", CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(again.Directory, "kept.txt")));
            Assert.Equal("autopilot/s2", GitFixture.Git(again.Directory, "rev-parse", "--abbrev-ref", "HEAD"));
        }

        [Fact]
        public async Task BranchExistsAsync_is_false_for_unknown_or_invalid_ids()
        {
            using var git = new GitFixture();
            var worktrees = git.CreateWorktrees();

            Assert.False(await worktrees.BranchExistsAsync("legacy", CancellationToken.None));
            Assert.False(await worktrees.BranchExistsAsync("../x", CancellationToken.None));
        }

        [Theory]
        [InlineData("")]
        [InlineData("..")]
        [InlineData("a..b")]
        [InlineData("a/b")]
        [InlineData("a b")]
        [InlineData("a\\b")]
        public async Task EnsureAsync_rejects_invalid_ids(string id)
        {
            using var git = new GitFixture();
            var worktrees = git.CreateWorktrees();

            await Assert.ThrowsAsync<ArgumentException>(() => worktrees.EnsureAsync(id, CancellationToken.None));
        }

        [Fact]
        public async Task EnsureAsync_throws_when_directory_is_not_a_git_repository()
        {
            var dir = Path.Combine(Path.GetTempPath(), "autopilot-nogit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var worktrees = new ConversationWorktrees(dir, dir + ".worktrees", "autopilot/");

                await Assert.ThrowsAsync<InvalidOperationException>(() => worktrees.EnsureAsync("s", CancellationToken.None));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task EnsureAsync_throws_when_repository_has_no_commits()
        {
            var dir = Path.Combine(Path.GetTempPath(), "autopilot-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                GitFixture.Git(dir, "init");
                var worktrees = new ConversationWorktrees(dir, dir + ".worktrees", "autopilot/");

                await Assert.ThrowsAsync<InvalidOperationException>(() => worktrees.EnsureAsync("s", CancellationToken.None));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task Worktree_changes_do_not_appear_in_main_directory_or_other_worktrees()
        {
            using var git = new GitFixture();
            var worktrees = git.CreateWorktrees();
            var a = await worktrees.EnsureAsync("a", CancellationToken.None);
            var b = await worktrees.EnsureAsync("b", CancellationToken.None);

            File.WriteAllText(Path.Combine(a.Directory, "only-a.txt"), "x");

            Assert.False(File.Exists(Path.Combine(git.Repo, "only-a.txt")));
            Assert.False(File.Exists(Path.Combine(b.Directory, "only-a.txt")));
        }

        private static void ForceDelete(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    public class CommitAndPushTests
    {
        private static WorkspaceTools Tools(string directory, string? branch, string remote = "origin") =>
            new(directory, branch, remote);

        [Fact]
        public async Task Pushes_the_conversation_branch_to_the_remote()
        {
            using var git = new GitFixture();
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);
            File.WriteAllText(Path.Combine(workspace.Directory, "feature.txt"), "x");

            var result = await Tools(workspace.Directory, workspace.Branch).CommitAndPushAsync("Add feature", CancellationToken.None);

            var sha = GitFixture.Git(workspace.Directory, "rev-parse", "HEAD");
            Assert.StartsWith("STATUS: PUSHED", result);
            Assert.Contains(workspace.Branch, result);
            Assert.Contains(sha, result);
            Assert.Contains($"{sha}\trefs/heads/{workspace.Branch}", GitFixture.Git(git.Repo, "ls-remote", "origin"));
            Assert.Equal("Add feature", GitFixture.Git(workspace.Directory, "log", "-1", "--format=%s"));
            Assert.DoesNotContain("refs/heads/main", GitFixture.Git(git.Repo, "ls-remote", "origin"));
        }

        [Fact]
        public async Task Pushes_without_new_commit_when_nothing_changed()
        {
            using var git = new GitFixture();
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);
            var before = GitFixture.Git(workspace.Directory, "rev-parse", "HEAD");

            var result = await Tools(workspace.Directory, workspace.Branch).CommitAndPushAsync("Nothing", CancellationToken.None);

            Assert.StartsWith("STATUS: PUSHED", result);
            Assert.Equal(before, GitFixture.Git(workspace.Directory, "rev-parse", "HEAD"));
        }

        [Fact]
        public async Task Honors_gitignore()
        {
            using var git = new GitFixture();
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);
            File.WriteAllText(Path.Combine(workspace.Directory, ".gitignore"), "secret.txt\n");
            File.WriteAllText(Path.Combine(workspace.Directory, "secret.txt"), "x");

            await Tools(workspace.Directory, workspace.Branch).CommitAndPushAsync("Ignore", CancellationToken.None);

            Assert.DoesNotContain("secret.txt", GitFixture.Git(workspace.Directory, "ls-files"));
        }

        [Fact]
        public async Task Fails_without_remote_and_makes_no_commit()
        {
            using var git = new GitFixture(withRemote: false);
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);
            var before = GitFixture.Git(workspace.Directory, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(workspace.Directory, "feature.txt"), "x");

            var result = await Tools(workspace.Directory, workspace.Branch).CommitAndPushAsync("Add", CancellationToken.None);

            Assert.StartsWith("STATUS: FAILED", result);
            Assert.Contains("remote", result);
            Assert.Equal(before, GitFixture.Git(workspace.Directory, "rev-parse", "HEAD"));
        }

        [Fact]
        public async Task Fails_when_push_fails_and_retains_local_commit()
        {
            using var git = new GitFixture();
            GitFixture.Git(git.Repo, "remote", "set-url", "origin", Path.Combine(git.Repo, "does-not-exist.git"));
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);
            var before = GitFixture.Git(workspace.Directory, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(workspace.Directory, "feature.txt"), "x");

            var result = await Tools(workspace.Directory, workspace.Branch).CommitAndPushAsync("Add", CancellationToken.None);

            Assert.StartsWith("STATUS: FAILED", result);
            Assert.Contains("local commit", result);
            Assert.NotEqual(before, GitFixture.Git(workspace.Directory, "rev-parse", "HEAD"));
        }

        [Fact]
        public async Task Fails_when_commit_fails_for_missing_identity()
        {
            using var git = new GitFixture();
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);
            File.WriteAllText(Path.Combine(workspace.Directory, "feature.txt"), "x");
            // An empty local user.name overrides any global identity and makes git refuse to commit.
            GitFixture.Git(git.Repo, "config", "user.name", "");

            var result = await Tools(workspace.Directory, workspace.Branch).CommitAndPushAsync("Add", CancellationToken.None);

            Assert.StartsWith("STATUS: FAILED", result);
            Assert.Contains("git commit failed", result);
        }

        [Fact]
        public async Task Fails_for_empty_message()
        {
            using var git = new GitFixture();
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);

            var result = await Tools(workspace.Directory, workspace.Branch).CommitAndPushAsync("  ", CancellationToken.None);

            Assert.StartsWith("STATUS: FAILED", result);
        }

        [Theory]
        [InlineData("--upload-pack=x")]
        [InlineData("-o")]
        [InlineData("")]
        [InlineData("my remote")]
        public async Task Fails_for_invalid_remote_name_and_makes_no_commit(string remote)
        {
            using var git = new GitFixture();
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);
            var before = GitFixture.Git(workspace.Directory, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(workspace.Directory, "feature.txt"), "x");

            var result = await Tools(workspace.Directory, workspace.Branch, remote).CommitAndPushAsync("Add", CancellationToken.None);

            Assert.StartsWith("STATUS: FAILED", result);
            Assert.Contains("invalid", result);
            Assert.Equal(before, GitFixture.Git(workspace.Directory, "rev-parse", "HEAD"));
        }

        [Fact]
        public async Task Fails_for_legacy_conversation_without_branch()
        {
            using var git = new GitFixture();
            File.WriteAllText(Path.Combine(git.Repo, "feature.txt"), "x");
            var before = GitFixture.Git(git.Repo, "rev-parse", "HEAD");

            var result = await Tools(git.Repo, branch: null).CommitAndPushAsync("Add", CancellationToken.None);

            Assert.StartsWith("STATUS: FAILED", result);
            Assert.Equal(before, GitFixture.Git(git.Repo, "rev-parse", "HEAD"));
        }

        [Fact]
        public async Task GetDiff_preExisting_ignores_changes_committed_after_baseline()
        {
            using var git = new GitFixture();
            var workspace = await git.CreateWorktrees().EnsureAsync("s1", CancellationToken.None);
            File.WriteAllText(Path.Combine(workspace.Directory, "readme.txt"), "user edit");
            var tools = Tools(workspace.Directory, workspace.Branch);
            await tools.CaptureBaselineAsync(CancellationToken.None);

            var before = await tools.GetDiffAsync(preExisting: true, CancellationToken.None);
            File.WriteAllText(Path.Combine(workspace.Directory, "agent.txt"), "agent work");
            await tools.CommitAndPushAsync("Commit all", CancellationToken.None);
            var after = await tools.GetDiffAsync(preExisting: true, CancellationToken.None);

            Assert.Contains("readme.txt", before);
            Assert.Equal(before, after);
            Assert.DoesNotContain("agent.txt", after);
        }
    }
}
