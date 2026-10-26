using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class AutoPilotSettingsTests
    {
        [Fact]
        public void ResolveSessionStateDirectory_uses_configured_copilot_directory()
        {
            var settings = new AutoPilotSettings { CopilotDirectory = Path.Combine(Path.GetTempPath(), "cop") };

            Assert.Equal(Path.Combine(Path.GetFullPath(settings.CopilotDirectory), "session-state"), settings.ResolveSessionStateDirectory());
        }

        [Fact]
        public void ResolveSessionStateDirectory_defaults_to_user_profile_copilot_folder()
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

            Assert.Equal(expected, new AutoPilotSettings().ResolveSessionStateDirectory());
        }

        [Fact]
        public void Worktree_settings_have_defaults()
        {
            var settings = new AutoPilotSettings();

            Assert.Equal("autopilot/", settings.BranchPrefix);
            Assert.Equal("origin", settings.GitRemote);
            Assert.Equal("", settings.WorktreesDirectory);
        }

        [Fact]
        public void ResolveWorktreesDirectory_defaults_to_sibling_of_working_directory()
        {
            var repo = Path.Combine(Path.GetTempPath(), "my-repo");
            var settings = new AutoPilotSettings { WorkingDirectory = repo };

            Assert.Equal(Path.GetFullPath(repo) + ".worktrees", settings.ResolveWorktreesDirectory());
        }

        [Fact]
        public void ResolveWorktreesDirectory_uses_configured_path()
        {
            var path = Path.Combine(Path.GetTempPath(), "wt");
            var settings = new AutoPilotSettings { WorktreesDirectory = path };

            Assert.Equal(Path.GetFullPath(path), settings.ResolveWorktreesDirectory());
        }

        [Fact]
        public void ResolveWorktreesDirectory_for_repository_defaults_to_sibling_of_repository()
        {
            var repo = Path.Combine(Path.GetTempPath(), "parent", "my-repo") + Path.DirectorySeparatorChar;
            var settings = new AutoPilotSettings { WorkingDirectory = Path.Combine(Path.GetTempPath(), "parent") };

            Assert.Equal(Path.Combine(Path.GetTempPath(), "parent", "my-repo") + ".worktrees", settings.ResolveWorktreesDirectory(repo));
        }

        [Fact]
        public void ResolveWorktreesDirectory_for_repository_uses_configured_path_with_repository_name()
        {
            var root = Path.Combine(Path.GetTempPath(), "wt");
            var settings = new AutoPilotSettings { WorktreesDirectory = root };

            Assert.Equal(Path.Combine(Path.GetFullPath(root), "my-repo"), settings.ResolveWorktreesDirectory(Path.Combine(Path.GetTempPath(), "parent", "my-repo")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolveCopilotDirectory_returns_null_when_empty_or_whitespace(string value)
        {
            var settings = new AutoPilotSettings { CopilotDirectory = value };

            Assert.Null(settings.ResolveCopilotDirectory());
        }

        [Fact]
        public void ResolveCopilotDirectory_returns_null_by_default()
        {
            Assert.Null(new AutoPilotSettings().ResolveCopilotDirectory());
        }

        [Fact]
        public void ResolveCopilotDirectory_returns_absolute_path_unchanged()
        {
            var path = Path.Combine(Path.GetTempPath(), "copilot-data");
            var settings = new AutoPilotSettings { CopilotDirectory = path };

            Assert.Equal(Path.GetFullPath(path), settings.ResolveCopilotDirectory());
        }

        [Fact]
        public void ResolveCopilotDirectory_resolves_relative_path_to_full_path()
        {
            var settings = new AutoPilotSettings { CopilotDirectory = Path.Combine("data", "copilot") };

            var resolved = settings.ResolveCopilotDirectory();

            Assert.NotNull(resolved);
            Assert.True(Path.IsPathRooted(resolved));
            Assert.Equal(Path.GetFullPath(Path.Combine("data", "copilot")), resolved);
        }

        [Fact]
        public void ResolveTasksFile_uses_explicit_path()
        {
            var settings = new AutoPilotSettings { TasksFile = "t.json", CopilotDirectory = "C:\\other" };

            Assert.Equal(Path.GetFullPath("t.json"), settings.ResolveTasksFile());
        }

        [Fact]
        public void ResolveTasksFile_defaults_under_copilot_directory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cop");
            var settings = new AutoPilotSettings { CopilotDirectory = dir };

            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "tasks", "tasks.json"), settings.ResolveTasksFile());
        }

        [Fact]
        public void ResolveTasksFile_defaults_under_current_directory()
        {
            Assert.Equal(
                Path.Combine(Directory.GetCurrentDirectory(), "Copilot", "tasks", "tasks.json"),
                new AutoPilotSettings().ResolveTasksFile());
        }
    }
}
