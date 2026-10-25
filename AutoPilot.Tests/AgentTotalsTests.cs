using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public sealed class AgentTotalsTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static SessionCost Session(DateTimeOffset updated, params (string Key, AgentCost Cost)[] agents)
        {
            var cost = new SessionCost { SessionId = Guid.NewGuid().ToString(), LastUpdated = updated };
            foreach (var (key, agent) in agents)
            {
                cost.Agents[key] = agent;
            }

            return cost;
        }

        [Fact]
        public void TotalByAgent_SumsAllFieldsAcrossSessions()
        {
            var a = Session(T0, ("dev", new AgentCost { Requests = 1, PremiumCost = 2, NanoAiu = 3e9, InputTokens = 4, OutputTokens = 5, CacheReadTokens = 6, CacheWriteTokens = 7, ReasoningTokens = 8 }));
            var b = Session(T0, ("dev", new AgentCost { Requests = 1, PremiumCost = 1, NanoAiu = 1e9, InputTokens = 1, OutputTokens = 1, CacheReadTokens = 1, CacheWriteTokens = 1, ReasoningTokens = 1 }), ("qa", new AgentCost { PremiumCost = 5 }));

            var totals = UsageAccumulator.TotalByAgent([a, b]);

            Assert.Equal(2, totals.Count);
            var dev = totals["dev"];
            Assert.Equal(2, dev.Requests);
            Assert.Equal(3, dev.PremiumCost);
            Assert.Equal(4, dev.AiCredits, 6);
            Assert.Equal(5, dev.InputTokens);
            Assert.Equal(6, dev.OutputTokens);
            Assert.Equal(7, dev.CacheReadTokens);
            Assert.Equal(8, dev.CacheWriteTokens);
            Assert.Equal(9, dev.ReasoningTokens);
            Assert.Equal(5, totals["qa"].PremiumCost);
        }

        [Fact]
        public void TotalByAgent_ToleratesEmptyNullAndDoesNotMutateInputs()
        {
            var a = Session(T0, ("dev", new AgentCost { PremiumCost = 2 }));
            var empty = new SessionCost { Agents = null! };

            var totals = UsageAccumulator.TotalByAgent([a, empty, null!, new SessionCost()]);

            Assert.Equal(2, totals["dev"].PremiumCost);
            Assert.NotSame(a.Agents["dev"], totals["dev"]);
            totals["dev"].PremiumCost = 99;
            Assert.Equal(2, a.Agents["dev"].PremiumCost);
            Assert.Empty(UsageAccumulator.TotalByAgent([]));
        }

        [Fact]
        public void TotalByAgent_DisplayNameFromMostRecentSessionThatHasOne()
        {
            var old = Session(T0, ("dev", new AgentCost { DisplayName = "Old" }));
            var recent = Session(T0.AddDays(2), ("dev", new AgentCost { DisplayName = "Recent" }));
            var newestNoName = Session(T0.AddDays(3), ("dev", new AgentCost()));

            Assert.Equal("Recent", UsageAccumulator.TotalByAgent([recent, newestNoName, old])["dev"].DisplayName);
            Assert.Equal("Recent", UsageAccumulator.TotalByAgent([old, newestNoName, recent])["dev"].DisplayName);
            Assert.Null(UsageAccumulator.TotalByAgent([newestNoName])["dev"].DisplayName);
        }

        [Fact]
        public async Task Ledger_GetAgentTotals_AggregatesRecordsAndIsEmptyWhenNone()
        {
            var dir = Path.Combine(Path.GetTempPath(), "autopilot-agents-" + Guid.NewGuid().ToString("N"));
            try
            {
                var ledger = new UsageLedger(Path.Combine(dir, "usage", "c.json"), Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageLedger>.Instance);
                Assert.Empty(ledger.GetAgentTotals());

                await ledger.RecordAsync(Session(T0, ("dev", new AgentCost { PremiumCost = 1 })));
                await ledger.RecordAsync(Session(T0, ("dev", new AgentCost { PremiumCost = 2 })));

                Assert.Equal(3, ledger.GetAgentTotals()["dev"].PremiumCost);
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }

        [Fact]
        public void TotalByAgent_KeysAreCaseSensitive()
        {
            var totals = UsageAccumulator.TotalByAgent([Session(T0, ("dev", new AgentCost()), ("Dev", new AgentCost()))]);

            Assert.Equal(2, totals.Count);
        }
    }
}
