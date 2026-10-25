using System.Text.Json;
using AutoPilot.Services;
using Microsoft.Extensions.Logging;

namespace AutoPilot.Tests;

public class SessionCostAcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class CapLogger : ILogger<UsageLedger>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Levels) Levels.Add(logLevel);
        }
    }

    private string NewPath(string name = "session-costs.json") => Path.Combine(_dir, "usage", name);

    private static SessionCost Cost(string id, double prem = 1, double nano = 2e9, long inp = 10, long outp = 5, long cr = 3, long cw = 2) =>
        new() { SessionId = id, Title = "t-" + id, PremiumRequests = prem, NanoAiu = nano, InputTokens = inp, OutputTokens = outp, CacheReadTokens = cr, CacheWriteTokens = cw };

    private static UsageSnapshot Snap(double prem, double nano, long inp, long outp, double mReq, double aReq) => new()
    {
        PremiumRequests = prem, NanoAiu = nano, InputTokens = inp, OutputTokens = outp, CacheReadTokens = inp / 2, CacheWriteTokens = inp / 4,
        Models = { ["gpt"] = new ModelCost { Requests = mReq, PremiumCost = prem, NanoAiu = nano, InputTokens = inp, OutputTokens = outp } },
        Agents = { ["coder"] = new AgentCost { DisplayName = "Coder", Requests = aReq, PremiumCost = prem / 2, NanoAiu = nano / 2, InputTokens = inp / 2, OutputTokens = outp / 2 } },
    };

    // AC-3 settings
    [Fact]
    public void Settings_ResolveUsageFile()
    {
        var explicitPath = Path.Combine(_dir, "x", "u.json");
        Assert.Equal(Path.GetFullPath(explicitPath), new AutoPilotSettings { UsageFile = explicitPath, CopilotDirectory = "C:\\other" }.ResolveUsageFile());
        var cd = Path.Combine(_dir, "cop");
        Assert.Equal(Path.GetFullPath(Path.Combine(cd, "usage", "session-costs.json")), new AutoPilotSettings { CopilotDirectory = cd }.ResolveUsageFile());
        Assert.Equal(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Copilot", "usage", "session-costs.json")), new AutoPilotSettings().ResolveUsageFile());
    }

    // AC-1 / AC-3
    [Fact]
    public async Task Record_CreatesMissingFileAndDirectory_AndPersistsAllFields()
    {
        var path = NewPath();
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        var ledger = new UsageLedger(path, new CapLogger());
        var c = Cost("s1");
        c.ReasoningTokens = 7;
        c.Models["gpt"] = new ModelCost { Requests = 2, PremiumCost = 1, NanoAiu = 2e9, InputTokens = 10 };
        c.Agents["coder"] = new AgentCost { DisplayName = "Coder", Requests = 1, NanoAiu = 1e9 };
        await ledger.RecordAsync(c);
        Assert.True(File.Exists(path));

        var reopened = new UsageLedger(path, new CapLogger());
        var got = reopened.Get("s1");
        Assert.NotNull(got);
        Assert.Equal(1, got!.PremiumRequests);
        Assert.Equal(2.0, got.AiCredits, 6);
        Assert.Equal(10, got.InputTokens);
        Assert.Equal(5, got.OutputTokens);
        Assert.Equal(3, got.CacheReadTokens);
        Assert.Equal(2, got.CacheWriteTokens);
        Assert.Equal(7, got.ReasoningTokens);
        Assert.Equal("t-s1", got.Title);
        Assert.Equal(2e9, got.Models["gpt"].NanoAiu);
        Assert.Equal(10, got.Models["gpt"].InputTokens);
        Assert.Equal("Coder", got.Agents["coder"].DisplayName);
        Assert.Equal(1e9, got.Agents["coder"].NanoAiu);
        Assert.Null(reopened.Get("nope"));
    }

    [Fact]
    public async Task Record_UpsertsBySessionId_AndRaisesChanged()
    {
        var ledger = new UsageLedger(NewPath(), new CapLogger());
        int changed = 0;
        ledger.Changed += () => changed++;
        await ledger.RecordAsync(Cost("a", prem: 1));
        await ledger.RecordAsync(Cost("a", prem: 4));
        Assert.Equal(2, changed);
        Assert.Single(ledger.GetAll());
        Assert.Equal(4, ledger.Get("a")!.PremiumRequests);
    }

    // AC-1 totals
    [Fact]
    public async Task Totals_SumAcrossSessions_AndSurviveRestart()
    {
        var path = NewPath();
        var ledger = new UsageLedger(path, new CapLogger());
        Assert.Equal(0, ledger.GetTotals().SessionCount);
        await ledger.RecordAsync(Cost("a", 1.5, 2e9, 10, 5, 3, 2));
        await ledger.RecordAsync(Cost("b", 3, 0.5e9, 100, 50, 30, 20));
        foreach (var l in new[] { ledger, new UsageLedger(path, new CapLogger()) })
        {
            var t = l.GetTotals();
            Assert.Equal(2, t.SessionCount);
            Assert.Equal(4.5, t.PremiumRequests, 9);
            Assert.Equal(2.5e9, t.NanoAiu, 3);
            Assert.Equal(2.5, t.AiCredits, 9);
            Assert.Equal(110, t.InputTokens);
            Assert.Equal(55, t.OutputTokens);
            Assert.Equal(33, t.CacheReadTokens);
            Assert.Equal(22, t.CacheWriteTokens);
        }
    }

    // AC-2
    [Fact]
    public async Task Resume_DoesNotDoubleCount_AcrossRestartAndSuccessiveSnapshots()
    {
        var path = NewPath();
        var l1 = new UsageLedger(path, new CapLogger());
        var first = UsageAccumulator.Apply("s", null, UsageSnapshot.Zero, Snap(2, 4e9, 100, 40, 3, 2), "hello");
        await l1.RecordAsync(first);

        // restart; SDK metrics on resume report cumulative values (baseline = 2/4e9/100...)
        var l2 = new UsageLedger(path, new CapLogger());
        var stored = l2.Get("s")!;
        var baseline = Snap(2, 4e9, 100, 40, 3, 2);

        // immediately after resume: nothing new
        var r0 = UsageAccumulator.Apply("s", stored, baseline, baseline);
        Assert.Equal(2, r0.PremiumRequests);
        Assert.Equal(4e9, r0.NanoAiu);
        Assert.Equal(100, r0.InputTokens);

        // successive cumulative snapshots; each applied against same stored+baseline
        var r1 = UsageAccumulator.Apply("s", stored, baseline, Snap(3, 6e9, 150, 60, 4, 3));
        await l2.RecordAsync(r1);
        var r2 = UsageAccumulator.Apply("s", stored, baseline, Snap(5, 10e9, 250, 100, 6, 5));
        await l2.RecordAsync(r2);

        var final = new UsageLedger(path, new CapLogger()).Get("s")!;
        Assert.Equal(5, final.PremiumRequests);          // 2 + (5-2)
        Assert.Equal(10e9, final.NanoAiu);               // 4 + (10-4)
        Assert.Equal(250, final.InputTokens);
        Assert.Equal(100, final.OutputTokens);
        Assert.Equal(6, final.Models["gpt"].Requests);   // 3 + (6-3)
        Assert.Equal(10e9, final.Models["gpt"].NanoAiu);
        Assert.Equal(5, final.Agents["coder"].Requests); // 2 + (5-2)
        Assert.Equal(5e9, final.Agents["coder"].NanoAiu);
        Assert.Equal("hello", final.Title);
        Assert.Equal(first.FirstSeen, final.FirstSeen);
        // inputs not mutated
        Assert.Equal(2, stored.PremiumRequests);
        Assert.Equal(2, baseline.PremiumRequests);
    }

    [Fact]
    public void Apply_NegativeDeltaClampedToZero_AndNewModelsAdded()
    {
        var stored = Cost("s", 5, 5e9, 50, 20, 0, 0);
        var baseline = Snap(10, 10e9, 500, 200, 5, 5);
        var current = Snap(8, 8e9, 400, 100, 7, 4); // lower than baseline (counter reset)
        current.Models["claude"] = new ModelCost { Requests = 1, NanoAiu = 1e9, InputTokens = 9 };
        var r = UsageAccumulator.Apply("s", stored, baseline, current);
        Assert.Equal(5, r.PremiumRequests);
        Assert.Equal(5e9, r.NanoAiu);
        Assert.Equal(50, r.InputTokens);
        Assert.Equal(20, r.OutputTokens);
        Assert.Equal(1e9, r.Models["claude"].NanoAiu);
        Assert.Equal(2, r.Models["gpt"].Requests);
    }

    [Fact]
    public void Apply_NewSession_UsesCurrentMinusZeroBaseline()
    {
        var r = UsageAccumulator.Apply("n", null, UsageSnapshot.Zero, Snap(1, 3e9, 20, 10, 1, 1), "title");
        Assert.Equal("n", r.SessionId);
        Assert.Equal(1, r.PremiumRequests);
        Assert.Equal(3.0, r.AiCredits, 9);
        Assert.Equal("title", r.Title);
    }

    // AC-3 corrupt
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("\0\0\0garbage")]
    public async Task CorruptFile_LogsWarning_BacksUp_AndKeepsWorking(string content)
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        var log = new CapLogger();
        var ledger = new UsageLedger(path, log);
        _ = ledger.GetAll(); // loading may be lazy; first access must have loaded

        if (content.Length > 0)
        {
            Assert.Contains(LogLevel.Warning, log.Levels);
            var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "session-costs.json.corrupt-*");
            Assert.Single(backups);
            Assert.Equal(content, await File.ReadAllTextAsync(backups[0]));
        }
        Assert.Empty(ledger.GetAll());
        Assert.Equal(0, ledger.GetTotals().SessionCount);

        await ledger.RecordAsync(Cost("ok"));
        var again = new UsageLedger(path, new CapLogger());
        Assert.NotNull(again.Get("ok"));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
    }

    // AC-6
    [Fact]
    public async Task ConcurrentRecords_AllPersist_FileIsValidJson()
    {
        var path = NewPath();
        var ledger = new UsageLedger(path, new CapLogger());
        const int n = 100;
        await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(() => ledger.RecordAsync(Cost("s" + i, prem: i)))));
        Assert.Equal(n, ledger.GetAll().Count);
        var reopened = new UsageLedger(path, new CapLogger());
        Assert.Equal(n, reopened.GetAll().Count);
        Assert.Equal(Enumerable.Range(0, n).Sum(i => (double)i), reopened.GetTotals().PremiumRequests);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"));
    }

    [Fact]
    public async Task ConcurrentWritesSameSession_AndReads_NoExceptions()
    {
        var path = NewPath();
        var ledger = new UsageLedger(path, new CapLogger());
        var tasks = new List<Task>();
        for (int i = 0; i < 60; i++)
        {
            int k = i;
            tasks.Add(Task.Run(() => ledger.RecordAsync(Cost("same", prem: k))));
            tasks.Add(Task.Run(() => ledger.RecordAsync(Cost("other" + k % 5))));
            tasks.Add(Task.Run(() => { _ = ledger.GetAll(); _ = ledger.GetTotals(); _ = ledger.Get("same"); }));
        }
        await Task.WhenAll(tasks);
        Assert.Equal(6, ledger.GetAll().Count);
        var reopened = new UsageLedger(path, new CapLogger());
        Assert.Equal(6, reopened.GetAll().Count);
        Assert.Equal(ledger.Get("same")!.PremiumRequests, reopened.Get("same")!.PremiumRequests);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TwoLedgerInstancesOnDifferentFiles_AreIndependent()
    {
        var a = new UsageLedger(NewPath("a.json"), new CapLogger());
        var b = new UsageLedger(NewPath("b.json"), new CapLogger());
        await a.RecordAsync(Cost("x"));
        Assert.Null(b.Get("x"));
    }
}
