using AutoPilot.Services;
using Microsoft.Extensions.Logging;

namespace AutoPilot.Tests;

public class AgentCostBreakdownAcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "acba-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class NullLog : ILogger<UsageLedger>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private string NewPath() => Path.Combine(_dir, "usage", "session-costs.json");

    private static AgentCost Agent(string? name, double req, double prem, double nano, long i, long o, long cr, long cw, long r) => new()
    {
        DisplayName = name, Requests = req, PremiumCost = prem, NanoAiu = nano,
        InputTokens = i, OutputTokens = o, CacheReadTokens = cr, CacheWriteTokens = cw, ReasoningTokens = r,
    };

    private static SessionCost Session(string id, int hoursOffset, params (string Key, AgentCost Cost)[] agents)
    {
        var s = new SessionCost { SessionId = id, Title = id };
        s.LastUpdated = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddHours(hoursOffset);
        foreach (var (k, c) in agents) s.Agents[k] = c;
        return s;
    }

    // AC-1
    [Fact]
    public void TotalByAgent_SumsAllFieldsAcrossSessions()
    {
        var a = Session("a", 0, ("coder", Agent("Coder", 1, 0.5, 1e9, 10, 20, 30, 40, 50)), ("rev", Agent(null, 2, 1, 2e9, 1, 1, 1, 1, 1)));
        var b = Session("b", 1, ("coder", Agent("Coder", 3, 1.5, 3e9, 5, 6, 7, 8, 9)));
        var totals = UsageAccumulator.TotalByAgent([a, b]);

        Assert.Equal(2, totals.Count);
        var c = totals["coder"];
        Assert.Equal(4, c.Requests);
        Assert.Equal(2, c.PremiumCost);
        Assert.Equal(4e9, c.NanoAiu);
        Assert.Equal(4.0, c.AiCredits, 6);
        Assert.Equal(15, c.InputTokens);
        Assert.Equal(26, c.OutputTokens);
        Assert.Equal(37, c.CacheReadTokens);
        Assert.Equal(48, c.CacheWriteTokens);
        Assert.Equal(59, c.ReasoningTokens);
        Assert.Equal(2, totals["rev"].Requests);
    }

    [Fact]
    public void TotalByAgent_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(UsageAccumulator.TotalByAgent([]));
    }

    [Fact]
    public void TotalByAgent_ToleratesNullAndEmptyAgents()
    {
        var nullAgents = new SessionCost { SessionId = "n" };
        nullAgents.Agents = null!;
        var empty = new SessionCost { SessionId = "e" };
        var real = Session("r", 0, ("x", Agent("X", 1, 1, 1e9, 1, 1, 1, 1, 1)));

        var totals = UsageAccumulator.TotalByAgent([nullAgents, empty, real]);

        Assert.Single(totals);
        Assert.Equal(1, totals["x"].Requests);
    }

    [Fact]
    public void TotalByAgent_DisplayName_FromMostRecentSessionHavingOne()
    {
        var oldest = Session("o", -5, ("x", Agent("Old", 1, 0, 0, 0, 0, 0, 0, 0)));
        var newest = Session("n", 5, ("x", Agent("New", 1, 0, 0, 0, 0, 0, 0, 0)));
        var newestNoName = Session("nn", 10, ("x", Agent(null, 1, 0, 0, 0, 0, 0, 0, 0)));

        // Order of enumeration must not matter.
        Assert.Equal("New", UsageAccumulator.TotalByAgent([oldest, newest, newestNoName])["x"].DisplayName);
        Assert.Equal("New", UsageAccumulator.TotalByAgent([newestNoName, newest, oldest])["x"].DisplayName);
        Assert.Equal("New", UsageAccumulator.TotalByAgent([newest, newestNoName, oldest])["x"].DisplayName);
    }

    [Fact]
    public void TotalByAgent_NoDisplayNameAnywhere_IsNull()
    {
        var s = Session("s", 0, ("x", Agent(null, 1, 0, 0, 0, 0, 0, 0, 0)));
        Assert.Null(UsageAccumulator.TotalByAgent([s])["x"].DisplayName);
    }

    [Fact]
    public void TotalByAgent_KeysAreOrdinalCaseSensitive()
    {
        var s = Session("s", 0, ("Coder", Agent(null, 1, 0, 0, 0, 0, 0, 0, 0)), ("coder", Agent(null, 2, 0, 0, 0, 0, 0, 0, 0)));
        var totals = UsageAccumulator.TotalByAgent([s]);
        Assert.Equal(2, totals.Count);
        Assert.Equal(1, totals["Coder"].Requests);
        Assert.Equal(2, totals["coder"].Requests);
        Assert.False(totals.ContainsKey("CODER"));
    }

    [Fact]
    public void TotalByAgent_DoesNotMutateInputs_AndIsRepeatable()
    {
        var a = Session("a", 0, ("x", Agent("X", 1, 1, 1e9, 1, 2, 3, 4, 5)));
        var b = Session("b", 1, ("x", Agent("X2", 1, 1, 1e9, 1, 2, 3, 4, 5)));
        var first = UsageAccumulator.TotalByAgent([a, b]);
        var second = UsageAccumulator.TotalByAgent([a, b]);

        Assert.Equal(first["x"].Requests, second["x"].Requests);
        Assert.Equal(2, first["x"].NanoAiu / 1e9);
        Assert.Equal(1, a.Agents["x"].Requests);
        Assert.Equal(1e9, a.Agents["x"].NanoAiu);
        Assert.Equal("X", a.Agents["x"].DisplayName);
        Assert.Equal(1, b.Agents["x"].Requests);
        Assert.NotSame(a.Agents["x"], first["x"]);
        Assert.NotSame(b.Agents["x"], first["x"]);
    }

    // AC-2
    [Fact]
    public void Ledger_GetAgentTotals_EmptyLedger_IsEmpty()
    {
        var ledger = new UsageLedger(NewPath(), new NullLog());
        Assert.Empty(ledger.GetAgentTotals());
    }

    [Fact]
    public void Ledger_GetAgentTotals_UnreadableFile_IsEmpty()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");
        var ledger = new UsageLedger(path, new NullLog());
        Assert.Empty(ledger.GetAgentTotals());
    }

    [Fact]
    public async Task Ledger_GetAgentTotals_AggregatesRecords_AndSurvivesReopen()
    {
        var path = NewPath();
        var ledger = new UsageLedger(path, new NullLog());
        await ledger.RecordAsync(Session("a", 0, ("coder", Agent("Coder", 1, 0.5, 1e9, 10, 20, 30, 40, 50))));
        await ledger.RecordAsync(Session("b", 1, ("coder", Agent("Coder", 2, 1, 2e9, 1, 1, 1, 1, 1)), ("rev", Agent("Reviewer", 1, 1, 5e8, 0, 0, 0, 0, 0))));
        await ledger.RecordAsync(new SessionCost { SessionId = "c" });

        foreach (var l in new[] { ledger, new UsageLedger(path, new NullLog()) })
        {
            var totals = l.GetAgentTotals();
            Assert.Equal(2, totals.Count);
            Assert.Equal(3, totals["coder"].Requests);
            Assert.Equal(1.5, totals["coder"].PremiumCost);
            Assert.Equal(3.0, totals["coder"].AiCredits, 6);
            Assert.Equal(11, totals["coder"].InputTokens);
            Assert.Equal(51, totals["coder"].ReasoningTokens);
            Assert.Equal(0.5, totals["rev"].AiCredits, 6);
            Assert.Equal("Reviewer", totals["rev"].DisplayName);
        }
    }

    [Fact]
    public async Task Ledger_GetAgentTotals_ReRecordingSameSession_DoesNotDoubleCount()
    {
        var ledger = new UsageLedger(NewPath(), new NullLog());
        await ledger.RecordAsync(Session("a", 0, ("x", Agent("X", 1, 1, 1e9, 1, 1, 1, 1, 1))));
        await ledger.RecordAsync(Session("a", 1, ("x", Agent("X", 4, 2, 3e9, 1, 1, 1, 1, 1))));
        var t = ledger.GetAgentTotals()["x"];
        Assert.Equal(4, t.Requests);
        Assert.Equal(3.0, t.AiCredits, 6);
    }

    // AC-7
    [Fact]
    public async Task ExistingTotals_UnaffectedByAgentData()
    {
        var ledger = new UsageLedger(NewPath(), new NullLog());
        var s = Session("a", 0, ("x", Agent("X", 1, 1, 1e9, 1, 1, 1, 1, 1)));
        s.PremiumRequests = 3;
        s.NanoAiu = 4e9;
        await ledger.RecordAsync(s);
        var totals = ledger.GetTotals();
        Assert.Equal(1, totals.SessionCount);
        Assert.Equal(3, totals.PremiumRequests);
        Assert.Equal(4.0, totals.AiCredits, 6);
        Assert.Single(ledger.GetAll());
    }
}
