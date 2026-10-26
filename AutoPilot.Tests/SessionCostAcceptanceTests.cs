using AutoPilot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPilot.Tests;

public class SessionCostAcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private UsageCache NewCache() => new(Path.Combine(_dir, "session-state"), "Orchestrator", _ => true, NullLogger<UsageCache>.Instance);

    private static SessionCost Cost(string id, double nano = 2e9, long inp = 10, long outp = 5, long cr = 3, long cw = 2) =>
        new() { SessionId = id, Title = "t-" + id, NanoAiu = nano, InputTokens = inp, OutputTokens = outp, CacheReadTokens = cr, CacheWriteTokens = cw };

    [Fact]
    public void Update_StoresAllFields_InMemoryOnly()
    {
        var cache = NewCache();
        var c = Cost("s1");
        c.ReasoningTokens = 7;
        c.Models["gpt"] = new ModelCost { Requests = 2, NanoAiu = 2e9, InputTokens = 10 };
        c.Agents["coder"] = new AgentCost { DisplayName = "Coder", Requests = 1, NanoAiu = 1e9 };
        cache.Update(c);

        var got = cache.Get("s1");
        Assert.NotNull(got);
        Assert.Equal(2.0, got!.AiCredits, 6);
        Assert.Equal(10, got.InputTokens);
        Assert.Equal(5, got.OutputTokens);
        Assert.Equal(3, got.CacheReadTokens);
        Assert.Equal(2, got.CacheWriteTokens);
        Assert.Equal(7, got.ReasoningTokens);
        Assert.Equal("t-s1", got.Title);
        Assert.Equal(2e9, got.Models["gpt"].NanoAiu);
        Assert.Equal("Coder", got.Agents["coder"].DisplayName);
        Assert.Null(cache.Get("nope"));
        Assert.False(Directory.Exists(_dir)); // nothing written to disk
    }

    [Fact]
    public void Update_UpsertsBySessionId_AndRaisesChanged()
    {
        var cache = NewCache();
        int changed = 0;
        cache.Changed += () => changed++;
        cache.Update(Cost("a", nano: 1e9));
        cache.Update(Cost("a", nano: 4e9));
        Assert.Equal(2, changed);
        Assert.Single(cache.GetAll());
        Assert.Equal(4e9, cache.Get("a")!.NanoAiu);
    }

    [Fact]
    public void Totals_SumAcrossSessions()
    {
        var cache = NewCache();
        Assert.Equal(0, cache.GetTotals().SessionCount);
        cache.Update(Cost("a", 2e9, 10, 5, 3, 2));
        cache.Update(Cost("b", 0.5e9, 100, 50, 30, 20));
        var t = cache.GetTotals();
        Assert.Equal(2, t.SessionCount);
        Assert.Equal(2.5e9, t.NanoAiu, 3);
        Assert.Equal(2.5, t.AiCredits, 9);
        Assert.Equal(110, t.InputTokens);
        Assert.Equal(55, t.OutputTokens);
        Assert.Equal(33, t.CacheReadTokens);
        Assert.Equal(22, t.CacheWriteTokens);
    }

    [Fact]
    public void Settings_ResolveSessionStateDirectory_UsesCopilotDirectory()
    {
        var cd = Path.Combine(_dir, "cop");
        Assert.Equal(Path.Combine(Path.GetFullPath(cd), "session-state"), new AutoPilotSettings { CopilotDirectory = cd }.ResolveSessionStateDirectory());
    }

    [Fact]
    public async Task ConcurrentUpdates_AllPresent()
    {
        var cache = NewCache();
        const int n = 100;
        await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(() => cache.Update(Cost("s" + i, nano: i * 1e9)))));
        Assert.Equal(n, cache.GetAll().Count);
        Assert.Equal(Enumerable.Range(0, n).Sum(i => (double)i), cache.GetTotals().AiCredits, 6);
    }

    [Fact]
    public async Task ConcurrentWritesSameSession_AndReads_NoExceptions()
    {
        var cache = NewCache();
        var tasks = new List<Task>();
        for (int i = 0; i < 60; i++)
        {
            int k = i;
            tasks.Add(Task.Run(() => cache.Update(Cost("same", nano: k * 1e9))));
            tasks.Add(Task.Run(() => cache.Update(Cost("other" + k % 5))));
            tasks.Add(Task.Run(() => { _ = cache.GetAll(); _ = cache.GetTotals(); _ = cache.Get("same"); }));
        }
        await Task.WhenAll(tasks);
        Assert.Equal(6, cache.GetAll().Count);
    }

    [Fact]
    public void TwoCachesOnDifferentDirectories_AreIndependent()
    {
        var a = NewCache();
        var b = NewCache();
        a.Update(Cost("x"));
        Assert.Null(b.Get("x"));
    }
}
