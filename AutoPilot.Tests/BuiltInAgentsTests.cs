using AutoPilot.Services;
using GitHub.Copilot;

namespace AutoPilot.Tests
{
    public class BuiltInAgentsTests
    {
        private static readonly string[] Expected =
            ["explore", "task", "general-purpose", "code-review", "research", "security-review", "rubber-duck"];

        [Fact]
        public void Excluded_ContainsExactlyTheKnownBuiltIns()
        {
            Assert.Equal(Expected, BuiltInAgents.Excluded);
        }

        [Fact]
        public void Apply_SetsExclusionOnNewSessionConfig()
        {
            var config = BuiltInAgents.Apply(new SessionConfig());

            Assert.Equal(Expected, config.ExcludedBuiltInAgents);
        }

        [Fact]
        public void Apply_SetsExclusionOnResumeSessionConfig()
        {
            var config = BuiltInAgents.Apply(new ResumeSessionConfig());

            Assert.Equal(Expected, config.ExcludedBuiltInAgents);
        }

        [Fact]
        public void Apply_DoesNotTouchCustomAgentsEvenWithBuiltInName()
        {
            var custom = new CustomAgentConfig { Name = "explore", Prompt = "mine" };
            var config = BuiltInAgents.Apply(new SessionConfig { CustomAgents = [custom] });

            Assert.Same(custom, Assert.Single(config.CustomAgents!));
            Assert.Equal(Expected, config.ExcludedBuiltInAgents);
        }
    }
}
