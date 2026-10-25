using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class AutoPilotSettingsTests
    {
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
    }
}
