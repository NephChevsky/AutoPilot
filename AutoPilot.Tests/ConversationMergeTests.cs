using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class ConversationMergeTests
    {
        private const string Id = "conv1";
        private const string Branch = "autopilot/conv1";

        private static async Task<(ConversationWorktrees Worktrees, string Worktree)> StartAsync(GitFixture fixture, bool commit = true)
        {
            var worktrees = fixture.CreateWorktrees();
            var workspace = await worktrees.EnsureAsync(Id, CancellationToken.None);
            if (commit)
            {
                File.WriteAllText(Path.Combine(workspace.Directory, "feature.txt"), "feature");
                GitFixture.Git(workspace.Directory, "add", "--all");
                GitFixture.Git(workspace.Directory, "commit", "-m", "feature");
            }

            return (worktrees, workspace.Directory);
        }

        private static string RemoteRev(string remote, string reference) =>
            GitFixture.Git(Path.GetDirectoryName(remote)!, "--git-dir", remote, "rev-parse", reference);

        private static bool RemoteBranchExists(GitFixture fixture, string branch) =>
            GitFixture.Git(Path.GetDirectoryName(fixture.Remote)!, "--git-dir", fixture.Remote, "branch", "--list", branch).Length > 0;

        private static bool TrackingRefExists(GitFixture fixture, string branch) =>
            GitFixture.Git(fixture.Repo, "for-each-ref", $"refs/remotes/origin/{branch}").Length > 0;

        private static bool BranchExists(GitFixture fixture, string branch) =>
            GitFixture.Git(fixture.Repo, "branch", "--list", branch).Length > 0;

        [Fact]
        public async Task Merges_pushes_and_cleans_up()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            GitFixture.Git(worktree, "push", "origin", $"HEAD:refs/heads/{Branch}");
            Assert.True(RemoteBranchExists(fixture, Branch));
            Assert.True(TrackingRefExists(fixture, Branch));

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Merged, result.Status);
            Assert.Equal("main", result.TargetBranch);
            var head = GitFixture.Git(fixture.Repo, "rev-parse", "HEAD");
            Assert.Equal(head, result.MergeCommit);
            Assert.Equal(2, GitFixture.Git(fixture.Repo, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length - 1);
            Assert.True(File.Exists(Path.Combine(fixture.Repo, "feature.txt")));
            Assert.Equal(head, RemoteRev(fixture.Remote, "refs/heads/main"));
            Assert.False(RemoteBranchExists(fixture, Branch));
            Assert.False(TrackingRefExists(fixture, Branch));
            Assert.Contains("remote branch", result.Message);
            Assert.False(Directory.Exists(worktree));
            Assert.False(BranchExists(fixture, Branch));
            Assert.False(await worktrees.BranchExistsAsync(Id, CancellationToken.None));
        }

        [Fact]
        public async Task Merges_when_branch_was_never_pushed()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Merged, result.Status);
            Assert.False(RemoteBranchExists(fixture, Branch));
            Assert.False(Directory.Exists(worktree));
            Assert.False(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Remote_branch_with_unmerged_commits_is_kept_and_reported()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            GitFixture.Git(worktree, "push", "origin", $"HEAD:refs/heads/{Branch}");
            var clone = Path.Combine(Path.GetDirectoryName(fixture.Repo)!, "clone");
            GitFixture.Git(Path.GetDirectoryName(fixture.Repo)!, "clone", "-b", Branch, fixture.Remote, clone);
            GitFixture.Git(clone, "config", "user.name", "Other");
            GitFixture.Git(clone, "config", "user.email", "other@example.com");
            File.WriteAllText(Path.Combine(clone, "extra.txt"), "extra");
            GitFixture.Git(clone, "add", "--all");
            GitFixture.Git(clone, "commit", "-m", "extra");
            GitFixture.Git(clone, "push", "origin", $"HEAD:refs/heads/{Branch}");
            var remoteTip = RemoteRev(fixture.Remote, $"refs/heads/{Branch}");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.CleanupFailed, result.Status);
            Assert.Contains(Branch, result.Message);
            Assert.Contains("not contained", result.Message);
            Assert.Equal(GitFixture.Git(fixture.Repo, "rev-parse", "HEAD"), result.MergeCommit);
            Assert.Equal(result.MergeCommit, RemoteRev(fixture.Remote, "refs/heads/main"));
            Assert.Equal(remoteTip, RemoteRev(fixture.Remote, $"refs/heads/{Branch}"));
            Assert.False(Directory.Exists(worktree));
            Assert.False(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Remote_branch_deletion_failure_still_cleans_up_locally_and_names_branch()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            GitFixture.Git(worktree, "push", "origin", $"HEAD:refs/heads/{Branch}");
            GitFixture.Git(fixture.Repo, "--git-dir", fixture.Remote, "config", "receive.denyDeletes", "true");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.CleanupFailed, result.Status);
            Assert.Contains(Branch, result.Message);
            Assert.Contains("delete it manually", result.Message);
            Assert.Equal(result.MergeCommit, RemoteRev(fixture.Remote, "refs/heads/main"));
            Assert.True(RemoteBranchExists(fixture, Branch));
            Assert.False(Directory.Exists(worktree));
            Assert.False(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Cancelled_token_throws_and_changes_nothing()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            var before = GitFixture.Git(fixture.Repo, "rev-parse", "HEAD");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worktrees.MergeAsync(Id, "origin", cts.Token));

            Assert.Equal(before, GitFixture.Git(fixture.Repo, "rev-parse", "HEAD"));
            Assert.False(File.Exists(Path.Combine(fixture.Repo, ".git", "MERGE_HEAD")));
            Assert.True(Directory.Exists(worktree));
            Assert.True(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Rejects_dirty_conversation_worktree()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "changed");
            var before = GitFixture.Git(fixture.Repo, "rev-parse", "HEAD");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
            Assert.Contains("commit", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, GitFixture.Git(fixture.Repo, "rev-parse", "HEAD"));
            Assert.True(Directory.Exists(worktree));
            Assert.True(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Rejects_untracked_file_in_conversation_worktree()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            File.WriteAllText(Path.Combine(worktree, "untracked.txt"), "x");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
            Assert.True(File.Exists(Path.Combine(worktree, "untracked.txt")));
            Assert.True(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Ignores_ignored_files_in_conversation_worktree()
        {
            using var fixture = new GitFixture();
            File.WriteAllText(Path.Combine(fixture.Repo, ".gitignore"), "bin/\n");
            GitFixture.Git(fixture.Repo, "add", "--all");
            GitFixture.Git(fixture.Repo, "commit", "-m", "ignore");
            var (worktrees, worktree) = await StartAsync(fixture);
            Directory.CreateDirectory(Path.Combine(worktree, "bin"));
            File.WriteAllText(Path.Combine(worktree, "bin", "out.dll"), "x");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Merged, result.Status);
            Assert.False(Directory.Exists(worktree));
        }

        [Fact]
        public async Task Rejects_dirty_main_repository()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            File.WriteAllText(Path.Combine(fixture.Repo, "readme.txt"), "dirty");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
            Assert.Equal("dirty", File.ReadAllText(Path.Combine(fixture.Repo, "readme.txt")));
            Assert.False(File.Exists(Path.Combine(fixture.Repo, "feature.txt")));
            Assert.True(Directory.Exists(worktree));
        }

        [Fact]
        public async Task Rejects_detached_head_in_main_repository()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            GitFixture.Git(fixture.Repo, "checkout", "--detach");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
            Assert.Contains("detached", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(worktree));
            Assert.True(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Rejects_when_merge_is_in_progress_in_main_repository()
        {
            using var fixture = new GitFixture();
            var (worktrees, _) = await StartAsync(fixture);
            File.WriteAllText(Path.Combine(fixture.Repo, "readme.txt"), "main side");
            GitFixture.Git(fixture.Repo, "commit", "-am", "main change");
            GitFixture.Git(fixture.Repo, "checkout", "-b", "other", "HEAD~1");
            File.WriteAllText(Path.Combine(fixture.Repo, "readme.txt"), "other side");
            GitFixture.Git(fixture.Repo, "commit", "-am", "other change");
            GitFixture.Git(fixture.Repo, "checkout", "main");
            Assert.ThrowsAny<InvalidOperationException>(() => GitFixture.Git(fixture.Repo, "merge", "other"));

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
            Assert.True(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Rejects_missing_remote()
        {
            using var fixture = new GitFixture(withRemote: false);
            var (worktrees, worktree) = await StartAsync(fixture);

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
            Assert.False(File.Exists(Path.Combine(fixture.Repo, "feature.txt")));
            Assert.True(Directory.Exists(worktree));
        }

        [Theory]
        [InlineData("")]
        [InlineData("-evil")]
        [InlineData("my remote")]
        [InlineData("a\tb")]
        public async Task Rejects_invalid_remote_name(string remote)
        {
            using var fixture = new GitFixture();
            var (worktrees, _) = await StartAsync(fixture);

            var result = await worktrees.MergeAsync(Id, remote, CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
            Assert.False(File.Exists(Path.Combine(fixture.Repo, "feature.txt")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("..")]
        [InlineData("a/b")]
        [InlineData("unknown")]
        public async Task Rejects_unknown_or_invalid_id(string id)
        {
            using var fixture = new GitFixture();
            var worktrees = fixture.CreateWorktrees();

            var result = await worktrees.MergeAsync(id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Rejected, result.Status);
        }

        [Fact]
        public async Task Conflict_is_aborted_and_lists_files()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture, commit: false);
            File.WriteAllText(Path.Combine(worktree, "readme.txt"), "branch side");
            GitFixture.Git(worktree, "commit", "-am", "branch change");
            File.WriteAllText(Path.Combine(fixture.Repo, "readme.txt"), "main side");
            GitFixture.Git(fixture.Repo, "commit", "-am", "main change");
            var before = GitFixture.Git(fixture.Repo, "rev-parse", "HEAD");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Conflict, result.Status);
            Assert.Contains("readme.txt", result.Message);
            Assert.Equal(before, GitFixture.Git(fixture.Repo, "rev-parse", "HEAD"));
            Assert.Equal("", GitFixture.Git(fixture.Repo, "status", "--porcelain"));
            Assert.Equal("main side", File.ReadAllText(Path.Combine(fixture.Repo, "readme.txt")));
            Assert.False(File.Exists(Path.Combine(fixture.Repo, ".git", "MERGE_HEAD")));
            Assert.True(Directory.Exists(worktree));
            Assert.True(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Push_failure_keeps_merge_branch_and_worktree_and_retry_succeeds()
        {
            using var fixture = new GitFixture();
            var (worktrees, worktree) = await StartAsync(fixture);
            var goodRemote = fixture.Remote;
            var missing = Path.Combine(Path.GetDirectoryName(goodRemote)!, "does-not-exist.git");
            GitFixture.Git(fixture.Repo, "remote", "set-url", "origin", missing);

            var failed = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.PushFailed, failed.Status);
            Assert.Equal("main", failed.TargetBranch);
            Assert.Equal(GitFixture.Git(fixture.Repo, "rev-parse", "HEAD"), failed.MergeCommit);
            Assert.True(Directory.Exists(worktree));
            Assert.True(BranchExists(fixture, Branch));

            GitFixture.Git(fixture.Repo, "remote", "set-url", "origin", goodRemote);
            var retried = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.Merged, retried.Status);
            Assert.Equal(failed.MergeCommit, retried.MergeCommit);
            Assert.Equal(retried.MergeCommit, RemoteRev(goodRemote, "refs/heads/main"));
            Assert.False(Directory.Exists(worktree));
            Assert.False(BranchExists(fixture, Branch));
        }

        [Fact]
        public async Task Push_rejected_when_remote_diverged_is_not_forced()
        {
            using var fixture = new GitFixture();
            GitFixture.Git(fixture.Repo, "push", "origin", "main");
            var (worktrees, worktree) = await StartAsync(fixture);
            var clone = Path.Combine(Path.GetDirectoryName(fixture.Repo)!, "clone");
            GitFixture.Git(Path.GetDirectoryName(fixture.Repo)!, "clone", "-b", "main", fixture.Remote, clone);
            GitFixture.Git(clone, "config", "user.name", "Other");
            GitFixture.Git(clone, "config", "user.email", "other@example.com");
            File.WriteAllText(Path.Combine(clone, "other.txt"), "other");
            GitFixture.Git(clone, "add", "--all");
            GitFixture.Git(clone, "commit", "-m", "remote work");
            GitFixture.Git(clone, "push", "origin", "HEAD:main");
            var remoteHead = RemoteRev(fixture.Remote, "refs/heads/main");

            var result = await worktrees.MergeAsync(Id, "origin", CancellationToken.None);

            Assert.Equal(ConversationMergeStatus.PushFailed, result.Status);
            Assert.Equal(remoteHead, RemoteRev(fixture.Remote, "refs/heads/main"));
            Assert.True(Directory.Exists(worktree));
            Assert.True(BranchExists(fixture, Branch));
        }
    }
}
