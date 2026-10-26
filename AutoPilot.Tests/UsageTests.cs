using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public sealed class UsageAccumulatorTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static UsageSnapshot Snap(double nano, long input = 0, long output = 0, double requests = 0, string model = "gpt", string agent = "orchestrator") =>
            new()
            {
                NanoAiu = nano,
                InputTokens = input,
                OutputTokens = output,
                Models = { [model] = new ModelCost { Requests = requests, NanoAiu = nano, InputTokens = input, OutputTokens = output } },
                Agents = { [agent] = new AgentCost { DisplayName = agent, Requests = requests, NanoAiu = nano, InputTokens = input, OutputTokens = output } },
            };

        private static SessionCost ToCost(UsageSnapshot snapshot) =>
            UsageAccumulator.ToSessionCost("s1", snapshot, null, T0, T0);

        [Fact]
        public void ToSessionCost_MapsSnapshotAndMetadata()
        {
            var cost = UsageAccumulator.ToSessionCost("s1", Snap(67_640_460_000, 100, 50, 15), "hello", T0, T0.AddHours(1));

            Assert.Equal("s1", cost.SessionId);
            Assert.Equal("hello", cost.Title);
            Assert.Equal(T0, cost.FirstSeen);
            Assert.Equal(T0.AddHours(1), cost.LastUpdated);
            Assert.Equal(67.64046, cost.AiCredits, 5);
            Assert.Equal(100, cost.InputTokens);
            Assert.Equal(50, cost.OutputTokens);
            Assert.Equal(15, cost.Models["gpt"].Requests);
            Assert.Equal(100, cost.Agents["orchestrator"].InputTokens);
            Assert.Equal("orchestrator", cost.Agents["orchestrator"].DisplayName);
        }

        [Fact]
        public void ToSessionCost_CopiesSnapshot()
        {
            var snapshot = Snap(1_000_000_000);
            var cost = ToCost(snapshot);

            cost.Models["gpt"].NanoAiu = 5;
            cost.Agents["orchestrator"].NanoAiu = 5;

            Assert.Equal(1_000_000_000, snapshot.Models["gpt"].NanoAiu);
            Assert.Equal(1_000_000_000, snapshot.Agents["orchestrator"].NanoAiu);
        }

        [Fact]
        public void Truncate_LimitsLengthAndHandlesBlank()
        {
            Assert.Null(UsageAccumulator.Truncate("  "));
            Assert.Null(UsageAccumulator.Truncate(null));
            Assert.Equal(100, UsageAccumulator.Truncate(new string('a', 250))!.Length);
            Assert.Equal("hi", UsageAccumulator.Truncate("  hi "));
        }

        [Fact]
        public void HasUsage_FalseForZero_TrueForAnySpend()
        {
            Assert.False(UsageAccumulator.HasUsage(ToCost(Snap(0))));
            Assert.True(UsageAccumulator.HasUsage(ToCost(Snap(5))));
            Assert.True(UsageAccumulator.HasUsage(ToCost(Snap(0, input: 3))));
            Assert.True(UsageAccumulator.HasUsage(ToCost(Snap(0, output: 3))));
        }
    }
}
