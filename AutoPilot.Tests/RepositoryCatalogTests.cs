using AutoPilot.Services;
using Microsoft.Extensions.Options;

namespace AutoPilot.Tests
{
    public class RepositoryCatalogTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "autopilot-repos-" + Guid.NewGuid().ToString("N"));

        public RepositoryCatalogTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RepositoryCatalog CreateCatalog(string? worktrees = null) =>
            new(Options.Create(new AutoPilotSettings { WorkingDirectory = _root, WorktreesDirectory = worktrees ?? "" }));

        [Fact]
        public void Discover_returns_empty_for_missing_directory()
        {
            Assert.Empty(RepositoryCatalog.Discover(Path.Combine(_root, "missing")));
        }

        [Fact]
        public void Discover_finds_git_folders_and_files_sorted_case_insensitively_excluding_others()
        {
            Directory.CreateDirectory(Path.Combine(_root, "beta", ".git"));
            Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
            File.WriteAllText(Path.Combine(_root, "Alpha", ".git"), "gitdir: elsewhere");
            Directory.CreateDirectory(Path.Combine(_root, "plain"));
            Directory.CreateDirectory(Path.Combine(_root, "beta.worktrees", "session", ".git"));
            Directory.CreateDirectory(Path.Combine(_root, "nested", "deep", ".git"));
            File.WriteAllText(Path.Combine(_root, "file.txt"), "x");

            var repos = RepositoryCatalog.Discover(_root);

            Assert.Equal(["Alpha", "beta"], repos.Select(r => r.Name));
            Assert.Equal(Path.Combine(Path.GetFullPath(_root), "beta"), repos[1].Path);
        }

        [Fact]
        public void GetRepositories_rescans_on_every_call()
        {
            var catalog = CreateCatalog();
            Assert.Empty(catalog.GetRepositories());

            Directory.CreateDirectory(Path.Combine(_root, "new-repo", ".git"));

            Assert.Equal(["new-repo"], catalog.GetRepositories().Select(r => r.Name));
        }

        [Fact]
        public void GetWorktrees_returns_same_instance_per_repository_path()
        {
            var catalog = CreateCatalog();
            var repo = new RepositoryInfo("a", Path.Combine(_root, "a"));

            Assert.Same(catalog.GetWorktrees(repo), catalog.GetWorktrees(repo with { Path = repo.Path.ToUpperInvariant() }));
            Assert.NotSame(catalog.GetWorktrees(repo), catalog.GetWorktrees(new RepositoryInfo("b", Path.Combine(_root, "b"))));
        }
    }
}
