using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class ConversationDiffTests
    {
        private static async Task<(ConversationWorktrees Worktrees, string Dir)> SetupAsync(GitFixture git, string id = "s1")
        {
            var worktrees = git.CreateWorktrees();
            var workspace = await worktrees.EnsureAsync(id, CancellationToken.None);
            GitFixture.Git(workspace.Directory, "config", "user.name", "Test");
            GitFixture.Git(workspace.Directory, "config", "user.email", "test@example.com");
            GitFixture.Git(workspace.Directory, "config", "commit.gpgsign", "false");
            return (worktrees, workspace.Directory);
        }

        [Fact]
        public async Task Missing_branch_and_invalid_id_return_null()
        {
            using var git = new GitFixture();
            var worktrees = git.CreateWorktrees();

            Assert.Null(await worktrees.GetDiffAsync("nope", CancellationToken.None));
            Assert.Null(await worktrees.GetDiffAsync("../bad", CancellationToken.None));
            Assert.Null(await worktrees.GetDiffAsync("", CancellationToken.None));
        }

        [Fact]
        public async Task No_changes_returns_empty_files()
        {
            using var git = new GitFixture();
            var (worktrees, _) = await SetupAsync(git);

            var diff = await worktrees.GetDiffAsync("s1", CancellationToken.None);

            Assert.NotNull(diff);
            Assert.Empty(diff.Files);
            Assert.False(diff.Truncated);
            Assert.Equal("autopilot/s1", diff.Branch);
            Assert.Equal(GitFixture.Git(git.Repo, "rev-parse", "HEAD"), diff.BaseCommit);
        }

        [Fact]
        public async Task Committed_uncommitted_and_untracked_changes_are_included_and_ignored_excluded()
        {
            using var git = new GitFixture();
            var (worktrees, dir) = await SetupAsync(git);
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "ignored.log\n");
            File.WriteAllText(Path.Combine(dir, "committed.txt"), "c1\nc2\n");
            GitFixture.Git(dir, "add", "--all");
            GitFixture.Git(dir, "commit", "-m", "work");
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "hello\nmore\n");
            File.WriteAllText(Path.Combine(dir, "untracked.txt"), "u1\n");
            File.WriteAllText(Path.Combine(dir, "ignored.log"), "x\n");

            var diff = await worktrees.GetDiffAsync("s1", CancellationToken.None);

            Assert.NotNull(diff);
            var byPath = diff.Files.ToDictionary(f => f.Path);
            Assert.Equal(DiffFileStatus.Added, byPath["committed.txt"].Status);
            Assert.Equal(2, byPath["committed.txt"].Additions);
            Assert.Equal(DiffFileStatus.Modified, byPath["readme.txt"].Status);
            Assert.Equal(DiffFileStatus.Added, byPath["untracked.txt"].Status);
            Assert.Equal(DiffFileStatus.Added, byPath[".gitignore"].Status);
            Assert.DoesNotContain("ignored.log", byPath.Keys);
            Assert.Equal(diff.Files.Sum(f => f.Additions), diff.TotalAdditions);
            Assert.Equal(1, diff.TotalDeletions);
        }

        [Fact]
        public async Task Deleted_file_is_reported()
        {
            using var git = new GitFixture();
            var (worktrees, dir) = await SetupAsync(git);
            File.Delete(Path.Combine(dir, "readme.txt"));

            var diff = await worktrees.GetDiffAsync("s1", CancellationToken.None);

            var file = Assert.Single(diff!.Files);
            Assert.Equal("readme.txt", file.Path);
            Assert.Equal(DiffFileStatus.Deleted, file.Status);
            Assert.Equal(1, file.Deletions);
        }

        [Fact]
        public async Task Renamed_file_is_detected()
        {
            using var git = new GitFixture();
            var (worktrees, dir) = await SetupAsync(git);
            File.Move(Path.Combine(dir, "readme.txt"), Path.Combine(dir, "read me.txt"));

            var diff = await worktrees.GetDiffAsync("s1", CancellationToken.None);

            var file = Assert.Single(diff!.Files);
            Assert.Equal(DiffFileStatus.Renamed, file.Status);
            Assert.Equal("read me.txt", file.Path);
            Assert.Equal("readme.txt", file.OldPath);
        }

        [Fact]
        public async Task Binary_file_is_flagged()
        {
            using var git = new GitFixture();
            var (worktrees, dir) = await SetupAsync(git);
            File.WriteAllBytes(Path.Combine(dir, "data.bin"), [0, 1, 2, 0, 255, 0, 3]);

            var diff = await worktrees.GetDiffAsync("s1", CancellationToken.None);

            var file = Assert.Single(diff!.Files);
            Assert.Equal("data.bin", file.Path);
            Assert.True(file.IsBinary);
        }

        [Fact]
        public async Task Main_moving_forward_does_not_show_main_commits()
        {
            using var git = new GitFixture();
            var (worktrees, dir) = await SetupAsync(git);
            var baseCommit = GitFixture.Git(git.Repo, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(git.Repo, "main-only.txt"), "m\n");
            GitFixture.Git(git.Repo, "add", "--all");
            GitFixture.Git(git.Repo, "commit", "-m", "main moves");
            File.WriteAllText(Path.Combine(dir, "mine.txt"), "x\n");

            var diff = await worktrees.GetDiffAsync("s1", CancellationToken.None);

            Assert.Equal(baseCommit, diff!.BaseCommit);
            Assert.Equal(["mine.txt"], diff.Files.Select(f => f.Path).ToArray());
        }

        [Fact]
        public async Task Real_index_and_main_repo_are_not_modified()
        {
            using var git = new GitFixture();
            var (worktrees, dir) = await SetupAsync(git);
            File.WriteAllText(Path.Combine(dir, "untracked.txt"), "u\n");
            var indexPath = GitFixture.Git(dir, "rev-parse", "--path-format=absolute", "--git-path", "index");
            var indexBefore = File.ReadAllBytes(indexPath);
            var mainStatusBefore = GitFixture.Git(git.Repo, "status", "--porcelain");

            await worktrees.GetDiffAsync("s1", CancellationToken.None);

            Assert.Equal(indexBefore, File.ReadAllBytes(indexPath));
            Assert.Equal("?? untracked.txt", GitFixture.Git(dir, "status", "--porcelain"));
            Assert.Equal(mainStatusBefore, GitFixture.Git(git.Repo, "status", "--porcelain"));
        }

        [Fact]
        public async Task Missing_worktree_directory_falls_back_to_branch_tip()
        {
            using var git = new GitFixture();
            var (worktrees, dir) = await SetupAsync(git);
            File.WriteAllText(Path.Combine(dir, "committed.txt"), "c\n");
            GitFixture.Git(dir, "add", "--all");
            GitFixture.Git(dir, "commit", "-m", "work");
            GitFixture.Git(git.Repo, "worktree", "remove", "--force", dir);

            var diff = await worktrees.GetDiffAsync("s1", CancellationToken.None);

            Assert.Equal(["committed.txt"], diff!.Files.Select(f => f.Path).ToArray());
        }

        [Fact]
        public async Task Huge_diff_is_truncated_at_a_line_boundary()
        {
            using var git = new GitFixture();
            var (worktrees, dir) = await SetupAsync(git);
            File.WriteAllText(Path.Combine(dir, "big.txt"), string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 8000)));

            var diff = await worktrees.GetDiffAsync("s1", CancellationToken.None);

            Assert.True(diff!.Truncated);
            var file = Assert.Single(diff.Files);
            Assert.InRange(file.Additions, 1, 7999);
            Assert.All(file.Lines.Where(l => l.Kind == DiffLineKind.Added), l => Assert.Equal(99, l.Text.Length));
        }
    }
}
