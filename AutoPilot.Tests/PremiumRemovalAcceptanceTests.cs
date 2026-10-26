using AutoPilot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPilot.Tests;

public class PremiumRemovalAcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "prm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    public static IEnumerable<object[]> CostTypes() =>
    [
        [typeof(UsageFigures)], [typeof(ModelCost)], [typeof(AgentCost)],
        [typeof(SessionCost)], [typeof(CostTotals)], [typeof(UsageSnapshot)],
    ];

    // AC-2
    [Theory]
    [MemberData(nameof(CostTypes))]
    public void CostModel_HasNoPremiumMembers(Type t)
    {
        var members = t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
        Assert.DoesNotContain(members, m => m.Name.Contains("Premium", StringComparison.OrdinalIgnoreCase));
    }

    // AC-3
    [Fact]
    public void HasUsage_FalseWithoutCreditsOrTokens_TrueWithEither()
    {
        Assert.False(UsageAccumulator.HasUsage(new SessionCost { SessionId = "e" }));
        Assert.True(UsageAccumulator.HasUsage(new SessionCost { SessionId = "c", NanoAiu = 1e9 }));
        Assert.True(UsageAccumulator.HasUsage(new SessionCost { SessionId = "t", InputTokens = 1 }));
    }

    // Premium "cost" (requests.cost) in events must not surface anywhere; credits come from nanoAiu.
    [Fact]
    public void EventsWithRequestCost_LoadCreditsAndTokens_WithoutPremium()
    {
        var sessions = Path.Combine(_dir, "session-state");
        var d = Path.Combine(sessions, "a");
        Directory.CreateDirectory(d);
        File.WriteAllLines(Path.Combine(d, "events.jsonl"),
        [
            """{"type":"session.start","data":{"sessionId":"a","startTime":"2026-01-01T00:00:00Z","context":{"cwd":"C:\\r"}},"timestamp":"2026-01-01T00:00:00Z"}""",
            """{"type":"session.shutdown","data":{"totalNanoAiu":3000000000,"modelMetrics":{"gpt":{"requests":{"count":2,"cost":3},"usage":{"inputTokens":100,"outputTokens":50,"cacheReadTokens":4,"cacheWriteTokens":3,"reasoningTokens":2},"totalNanoAiu":3000000000}},"agentMetrics":{"main":{"totalNanoAiu":3000000000,"modelMetrics":{"gpt":{"requests":{"count":2,"cost":3},"usage":{"inputTokens":100,"outputTokens":50,"cacheReadTokens":4,"cacheWriteTokens":3,"reasoningTokens":2},"totalNanoAiu":3000000000}}}}},"timestamp":"2026-01-01T01:00:00Z"}""",
        ]);
        var cache = new UsageCache(sessions, "Orchestrator", _ => true, NullLogger<UsageCache>.Instance);
        var a = cache.Get("a");
        Assert.NotNull(a);
        Assert.Equal(3.0, a!.AiCredits, 6);
        Assert.Equal(100, a.InputTokens);
        Assert.Equal(50, a.OutputTokens);
        Assert.Equal(4, a.CacheReadTokens);
        Assert.Equal(3, a.CacheWriteTokens);
        Assert.Equal(2, a.ReasoningTokens);
        Assert.Equal(3.0, a.Models["gpt"].AiCredits, 6);
        Assert.Equal(2, a.Models["gpt"].Requests);
        Assert.Equal("Orchestrator", a.Agents.Values.Single().DisplayName);
        Assert.DoesNotContain("main", a.Agents.Values.Select(x => x.DisplayName));
        Assert.Equal(3.0, cache.GetTotals().AiCredits, 6);
    }

    // AC-3: ToSessionCost works on plain figures with no premium involvement
    [Fact]
    public void ToSessionCost_CarriesCreditsAndTokens()
    {
        var current = new UsageSnapshot { NanoAiu = 2e9, InputTokens = 10, OutputTokens = 4 };
        var cost = UsageAccumulator.ToSessionCost("s", current, "T", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(2.0, cost.AiCredits, 6);
        Assert.Equal(10, cost.InputTokens);
        Assert.Equal("T", cost.Title);
        Assert.True(UsageAccumulator.HasUsage(cost));
    }
}