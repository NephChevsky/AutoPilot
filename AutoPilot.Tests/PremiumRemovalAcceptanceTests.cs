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

    private string NewPath() => Path.Combine(_dir, "usage", "session-costs.json");

    private static UsageLedger Open(string path) => new(path, NullLogger<UsageLedger>.Instance);

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

    // AC-4
    [Fact]
    public async Task Record_WritesNoPremiumProperties()
    {
        var path = NewPath();
        var ledger = Open(path);
        var c = new SessionCost { SessionId = "s1", Title = "t", NanoAiu = 2e9, InputTokens = 10, OutputTokens = 5 };
        c.Models["gpt"] = new ModelCost { Requests = 2, NanoAiu = 2e9, InputTokens = 10 };
        c.Agents["coder"] = new AgentCost { DisplayName = "Coder", Requests = 1, NanoAiu = 1e9 };
        await ledger.RecordAsync(c);
        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("premium", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nanoAiu", text);
    }

    private const string Legacy = """
    {"sessions":[
      {"sessionId":"a","title":"A","nanoAiu":3000000000,"inputTokens":100,"outputTokens":50,"cacheReadTokens":4,"cacheWriteTokens":3,"reasoningTokens":2,
       "premiumRequests":7.5,
       "models":{"gpt":{"requests":2,"premiumCost":3,"nanoAiu":3000000000,"inputTokens":100}},
       "agents":{"coder":{"displayName":"Coder","requests":2,"premiumCost":1.5,"nanoAiu":2000000000,"inputTokens":60,"outputTokens":30}}},
      {"sessionId":"b","title":"B","nanoAiu":1000000000,"inputTokens":10,"outputTokens":5,"premiumRequests":1,
       "agents":{"coder":{"displayName":"Coder","premiumCost":9,"nanoAiu":1000000000,"inputTokens":10,"outputTokens":5}}}
    ]}
    """;

    // AC-5
    [Fact]
    public async Task LegacyFile_LoadsIgnoringPremium_AndResaveDropsIt()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Legacy);

        var ledger = Open(path);
        var a = ledger.Get("a");
        Assert.NotNull(a);
        Assert.Equal(3.0, a!.AiCredits, 6);
        Assert.Equal(100, a.InputTokens);
        Assert.Equal(50, a.OutputTokens);
        Assert.Equal(4, a.CacheReadTokens);
        Assert.Equal(3, a.CacheWriteTokens);
        Assert.Equal(2, a.ReasoningTokens);
        Assert.Equal(3.0, a.Models["gpt"].AiCredits, 6);
        Assert.Equal(2, a.Models["gpt"].Requests);
        Assert.Equal("Coder", a.Agents["coder"].DisplayName);
        Assert.Equal(2.0, a.Agents["coder"].AiCredits, 6);

        var totals = ledger.GetTotals();
        Assert.Equal(2, totals.SessionCount);
        Assert.Equal(4.0, totals.AiCredits, 6);
        Assert.Equal(110, totals.InputTokens);
        Assert.Equal(55, totals.OutputTokens);

        var agents = ledger.GetAgentTotals();
        Assert.Equal(3.0, agents["coder"].AiCredits, 6);
        Assert.Equal(70, agents["coder"].InputTokens);

        await ledger.RecordAsync(new SessionCost { SessionId = "c", NanoAiu = 1e9, InputTokens = 1 });
        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("premium", text, StringComparison.OrdinalIgnoreCase);

        var again = Open(path);
        Assert.Equal(3, again.GetTotals().SessionCount);
        Assert.Equal(5.0, again.GetTotals().AiCredits, 6);
    }

    // AC-3: Apply works on plain figures with no premium involvement
    [Fact]
    public void Apply_AccumulatesCreditsAndTokens()
    {
        var baseline = new UsageSnapshot();
        var current = new UsageSnapshot { NanoAiu = 2e9, InputTokens = 10, OutputTokens = 4 };
        var cost = UsageAccumulator.Apply("s", null, baseline, current, "T");
        Assert.Equal(2.0, cost.AiCredits, 6);
        Assert.Equal(10, cost.InputTokens);
        Assert.True(UsageAccumulator.HasUsage(cost));
    }
}
