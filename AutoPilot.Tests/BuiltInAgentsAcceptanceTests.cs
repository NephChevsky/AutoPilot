using System.Linq;
using AutoPilot.Services;
using GitHub.Copilot;
using Xunit;

namespace AutoPilot.Tests
{
    public class BuiltInAgentsAcceptanceTests
    {
        private static readonly string[] Expected =
            ["explore", "task", "general-purpose", "code-review", "research", "security-review", "rubber-duck"];

        [Fact]
        public void Excluded_ContainsExactlyTheSevenBuiltIns()
        {
            Assert.Equal(Expected.OrderBy(x => x), BuiltInAgents.Excluded.OrderBy(x => x));
            Assert.Equal(Expected.Length, BuiltInAgents.Excluded.Distinct().Count());
        }

        [Fact]
        public void Apply_SessionConfig_SetsExclusions()
        {
            var cfg = BuiltInAgents.Apply(new SessionConfig());
            Assert.NotNull(cfg.ExcludedBuiltInAgents);
            Assert.Equal(Expected.OrderBy(x => x), cfg.ExcludedBuiltInAgents!.OrderBy(x => x));
        }

        [Fact]
        public void Apply_ResumeSessionConfig_SetsExclusions()
        {
            var cfg = BuiltInAgents.Apply(new ResumeSessionConfig());
            Assert.NotNull(cfg.ExcludedBuiltInAgents);
            Assert.Equal(Expected.OrderBy(x => x), cfg.ExcludedBuiltInAgents!.OrderBy(x => x));
        }

        [Fact]
        public void Apply_ReturnsSameInstance()
        {
            var s = new SessionConfig();
            var r = new ResumeSessionConfig();
            Assert.Same(s, BuiltInAgents.Apply(s));
            Assert.Same(r, BuiltInAgents.Apply(r));
        }

        [Fact]
        public void Apply_PreservesCustomAgents_IncludingBuiltInNamedOne()
        {
            var custom = new List<CustomAgentConfig>
            {
                new() { Name = "my-agent", Prompt = "p1" },
                new() { Name = "explore", Prompt = "p2" },
            };
            var s = BuiltInAgents.Apply(new SessionConfig { CustomAgents = custom });
            var r = BuiltInAgents.Apply(new ResumeSessionConfig { CustomAgents = custom });
            foreach (var agents in new[] { s.CustomAgents, r.CustomAgents })
            {
                Assert.NotNull(agents);
                Assert.Equal(2, agents!.Count);
                Assert.Equal("my-agent", agents[0].Name);
                Assert.Equal("explore", agents[1].Name);
                Assert.Equal("p2", agents[1].Prompt);
            }
        }

        [Fact]
        public void Apply_WithoutCustomAgents_LeavesThemUnset()
        {
            var cfg = BuiltInAgents.Apply(new SessionConfig());
            Assert.True(cfg.CustomAgents is null || cfg.CustomAgents.Count == 0);
        }

        [Fact]
        public void Apply_IsIdempotent()
        {
            var cfg = BuiltInAgents.Apply(BuiltInAgents.Apply(new SessionConfig()));
            Assert.Equal(Expected.Length, cfg.ExcludedBuiltInAgents!.Count());
        }

        [Fact]
        public void MutatingConfigList_DoesNotCorruptExcluded()
        {
            var cfg = BuiltInAgents.Apply(new SessionConfig());
            if (cfg.ExcludedBuiltInAgents is System.Collections.Generic.IList<string> list && !list.IsReadOnly)
            {
                list.Add("bogus");
                list.Remove("explore");
            }
            Assert.Equal(Expected.OrderBy(x => x), BuiltInAgents.Excluded.OrderBy(x => x));
            var other = BuiltInAgents.Apply(new SessionConfig());
            Assert.Equal(Expected.Length, other.ExcludedBuiltInAgents!.Count());
        }

        [Fact]
        public void Apply_SeparateConfigs_DoNotShareList()
        {
            var a = BuiltInAgents.Apply(new SessionConfig());
            var b = BuiltInAgents.Apply(new SessionConfig());
            Assert.NotSame(a.ExcludedBuiltInAgents, b.ExcludedBuiltInAgents);
        }
    }
}
