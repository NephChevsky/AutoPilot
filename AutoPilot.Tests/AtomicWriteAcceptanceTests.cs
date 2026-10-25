using System.Text.Json;
using AutoPilot.Services;
using Microsoft.Extensions.Logging;

namespace AutoPilot.Tests;

public class AtomicWriteAcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "awa-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class NullLog<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private static readonly RepositoryInfo Repo = new("repo", Path.Combine(Path.GetTempPath(), "Repo-AW"));

    private string NewPath(string name) => Path.Combine(_dir, Guid.NewGuid().ToString("N"), name);
    private static UsageLedger Ledger(string p) => new(p, new NullLog<UsageLedger>());
    private static TaskStore Store(string p) => new(p, new NullLog<TaskStore>());
    private static readonly FileRetryPolicy Tiny = new(3, TimeSpan.FromMilliseconds(10));
    private static UsageLedger TinyLedger(string p) => new(p, new NullLog<UsageLedger>(), Tiny);
    private static TaskStore TinyStore(string p) => new(p, new NullLog<TaskStore>(), Tiny);
    private static SessionCost Cost(string id, double nano = 2e9) =>
        new() { SessionId = id, Title = "t-" + id, NanoAiu = nano, InputTokens = 10, OutputTokens = 5, CacheReadTokens = 3, CacheWriteTokens = 2 };

    private static void AssertNoTmp(string path) =>
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

    private static void AssertValidJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
    }

    private static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static Task ReleaseAfter(FileStream fs, int ms) => Task.Run(async () =>
    {
        await Task.Delay(ms);
        fs.Dispose();
    });

    private static async Task<Exception?> Catch(Func<Task> act)
    {
        try { await act(); return null; }
        catch (Exception e) { return e; }
    }

    // ---- AC-1 ----
    [Fact]
    public async Task Ledger_ShortLock_MutationSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = NewPath("usage.json");
        var l = Ledger(p);
        await l.RecordAsync(Cost("a", 1e9));
        var fs = Lock(p);
        var rel = ReleaseAfter(fs, 100);
        await l.RecordAsync(Cost("b", 3e9));
        await rel;
        var re = Ledger(p);
        Assert.NotNull(re.Get("a"));
        Assert.Equal(3e9, re.Get("b")!.NanoAiu);
        AssertNoTmp(p);
        AssertValidJson(p);
    }

    private async Task<(TaskStore s, string p, dynamic t1)> SeedStore()
    {
        var p = NewPath("tasks.json");
        var s = Store(p);
        var t1 = await s.CreateAsync(Repo, "one", "d");
        return (s, p, t1);
    }

    private static async Task WithShortLock(string p, Func<Task> act)
    {
        var rel = ReleaseAfter(Lock(p), 100);
        await act();
        await rel;
    }

    [Fact]
    public async Task TaskStore_ShortLock_CreateSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (s, p, t1) = await SeedStore();
        dynamic? t2 = null;
        await WithShortLock(p, async () => t2 = await s.CreateAsync(Repo, "two", "d"));
        var re = Store(p);
        Assert.NotNull(re.Get(t1.Id));
        Assert.Equal("two", re.Get(t2!.Id)!.Title);
        AssertNoTmp(p); AssertValidJson(p);
    }

    [Fact]
    public async Task TaskStore_ShortLock_UpdateSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (s, p, t1) = await SeedStore();
        await WithShortLock(p, async () => Assert.True(await s.UpdateAsync(t1.Id, "one-upd", "x")));
        Assert.Equal("one-upd", Store(p).Get(t1.Id)!.Title);
        AssertNoTmp(p); AssertValidJson(p);
    }

    [Fact]
    public async Task TaskStore_ShortLock_MarkSentSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (s, p, t1) = await SeedStore();
        await WithShortLock(p, async () => Assert.True(await s.MarkSentAsync(t1.Id, "sess-1")));
        var got = Store(p).Get(t1.Id)!;
        Assert.True(got.IsSent);
        Assert.Equal("sess-1", got.SessionId);
        AssertNoTmp(p); AssertValidJson(p);
    }

    [Fact]
    public async Task TaskStore_ShortLock_DeleteSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (s, p, t1) = await SeedStore();
        await WithShortLock(p, async () => Assert.True(await s.DeleteAsync(t1.Id)));
        Assert.Null(Store(p).Get(t1.Id));
        AssertNoTmp(p); AssertValidJson(p);
    }
    // ---- AC-2 / AC-3 / AC-5 ----
    [Fact]
    public async Task Ledger_LongLock_FailsWithOriginalTypeAndKeepsOldContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = NewPath("usage.json");
        var l = TinyLedger(p);
        await l.RecordAsync(Cost("a", 1e9));
        var fs = Lock(p);
        Exception? ex;
        try { ex = await Catch(() => l.RecordAsync(Cost("b", 3e9))); }
        finally { fs.Dispose(); }
        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException, ex!.GetType().FullName);
        AssertValidJson(p);
        var re = Ledger(p);
        Assert.Equal(1e9, re.Get("a")!.NanoAiu);
        Assert.Null(re.Get("b"));
        AssertNoTmp(p);
    }

    [Fact]
    public async Task TaskStore_LongLock_FailsWithOriginalTypeAndKeepsOldContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = NewPath("tasks.json");
        var s = TinyStore(p);
        var t1 = await s.CreateAsync(Repo, "one", "d");
        var fs = Lock(p);
        Exception? ex;
        try { ex = await Catch(() => s.CreateAsync(Repo, "two", "d")); }
        finally { fs.Dispose(); }
        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException, ex!.GetType().FullName);
        AssertValidJson(p);
        var re = Store(p);
        Assert.Single(re.GetAll());
        Assert.Equal("one", re.Get(t1.Id)!.Title);
        AssertNoTmp(p);
    }

    [Fact]
    public async Task Ledger_StillWorksAfterFailedWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = NewPath("usage.json");
        var l = TinyLedger(p);
        await l.RecordAsync(Cost("a", 1e9));
        var fs = Lock(p);
        try { Assert.NotNull(await Catch(() => l.RecordAsync(Cost("b", 3e9)))); }
        finally { fs.Dispose(); }
        await l.RecordAsync(Cost("c", 5e9));
        var re = Ledger(p);
        Assert.NotNull(re.Get("a"));
        Assert.NotNull(re.Get("c"));
        AssertNoTmp(p);
    }

    // ---- AC-3 concurrency ----
    [Fact]
    public async Task Ledger_ParallelMutationsAndReads_AllPersisted()
    {
        var p = NewPath("usage.json");
        var l = Ledger(p);
        await l.RecordAsync(Cost("seed"));
        using var cts = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                _ = l.GetAll(); _ = l.GetTotals();
                await Task.Delay(5);
            }
        })).ToArray();
        var writers = Enumerable.Range(0, 40).Select(i => Task.Run(() => l.RecordAsync(Cost("s" + i, (i + 1) * 1e9)))).ToArray();
        try { await Task.WhenAll(writers); }
        finally { cts.Cancel(); }
        await Task.WhenAll(readers);

        var re = Ledger(p);
        for (var i = 0; i < 40; i++) Assert.Equal((i + 1) * 1e9, re.Get("s" + i)!.NanoAiu);
        Assert.NotNull(re.Get("seed"));
        AssertValidJson(p);
        AssertNoTmp(p);
    }

    [Fact]
    public async Task TaskStore_ParallelMutationsAndReads_AllPersisted()
    {
        var p = NewPath("tasks.json");
        var s = Store(p);
        await s.CreateAsync(Repo, "seed", null);
        using var cts = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                _ = s.GetAll();
                await Task.Delay(5);
            }
        })).ToArray();
        var writers = Enumerable.Range(0, 40).Select(i => Task.Run(() => s.CreateAsync(Repo, "t" + i, "d"))).ToArray();
        try { await Task.WhenAll(writers); }
        finally { cts.Cancel(); }
        await Task.WhenAll(readers);

        var re = Store(p);
        Assert.Equal(41, re.GetAll().Count);
        for (var i = 0; i < 40; i++) Assert.Contains(re.GetAll(), t => t.Title == "t" + i);
        AssertValidJson(p);
        AssertNoTmp(p);
    }

    // ---- AC-4 ----
    [Fact]
    public async Task FreshInstance_CanLoad_WhileFileOpenForWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lp = NewPath("usage.json");
        await Ledger(lp).RecordAsync(Cost("a"));
        using (new FileStream(lp, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.NotNull(Ledger(lp).Get("a"));

        var tp = NewPath("tasks.json");
        var t = await Store(tp).CreateAsync(Repo, "x", null);
        using (new FileStream(tp, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.NotNull(Store(tp).Get(t.Id));
    }

    [Fact]
    public async Task StoreRead_DoesNotBlockRename_AfterConstruction()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lp = NewPath("usage.json");
        await Ledger(lp).RecordAsync(Cost("a"));
        var l = Ledger(lp);
        _ = l.GetAll();
        File.Move(lp, lp + ".moved");   // no lingering handle from the store

        var tp = NewPath("tasks.json");
        var t = await Store(tp).CreateAsync(Repo, "x", null);
        var s = Store(tp);
        _ = s.GetAll();
        File.Move(tp, tp + ".moved");
        Assert.True(File.Exists(tp + ".moved"));
    }

    // ---- AC-3 corrupt file behavior unchanged ----
    [Fact]
    public async Task CorruptFile_StillLoadsAsEmpty_AndCanBeOverwritten()
    {
        var p = NewPath("usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, "{ not json");
        var l = Ledger(p);
        Assert.Empty(l.GetAll());
        await l.RecordAsync(Cost("a"));
        Assert.NotNull(Ledger(p).Get("a"));
        AssertNoTmp(p);
    }
}
