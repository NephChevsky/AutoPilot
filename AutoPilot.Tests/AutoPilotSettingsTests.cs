using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class AutoPilotSettingsTests
    {
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
