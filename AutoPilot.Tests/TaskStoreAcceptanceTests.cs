using AutoPilot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoPilot.Tests;

public class TaskStoreAcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsa-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class CapLogger : ILogger<TaskStore>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Levels) Levels.Add(logLevel);
        }
    }

    private string NewPath() => Path.Combine(_dir, "tasks", "tasks.json");
    private static TaskStore Store(string path) => new(path, new CapLogger());
    private static readonly RepositoryInfo Repo = new("repo", Path.Combine(Path.GetTempPath(), "Repo-A"));
    private static readonly RepositoryInfo Repo2 = new("other", Path.Combine(Path.GetTempPath(), "Repo-B"));

    // AC-2 settings
    [Fact]
    public void Settings_ResolveTasksFile()
    {
        var explicitPath = Path.Combine(_dir, "x", "t.json");
        Assert.Equal(Path.GetFullPath(explicitPath), new AutoPilotSettings { TasksFile = explicitPath, CopilotDirectory = "C:\\other" }.ResolveTasksFile());
        var cd = Path.Combine(_dir, "cop");
        Assert.Equal(Path.GetFullPath(Path.Combine(cd, "tasks", "tasks.json")), new AutoPilotSettings { CopilotDirectory = cd }.ResolveTasksFile());
        Assert.Equal(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Copilot", "tasks", "tasks.json")), new AutoPilotSettings().ResolveTasksFile());
        Assert.Equal("", new AutoPilotSettings().TasksFile);
    }

    [Fact]
    public void OptionsConstructor_UsesResolvedFile()
    {
        var p = NewPath();
        var s = new TaskStore(Options.Create(new AutoPilotSettings { TasksFile = p }), new CapLogger());
        Assert.Equal(Path.GetFullPath(p), s.FilePath);
    }

    // AC-1
    [Fact]
    public async Task Create_TrimsAndStoresFields()
    {
        var s = Store(NewPath());
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var t = await s.CreateAsync(Repo, "  Fix bug  ", "  details  ");
        Assert.Equal("Fix bug", t.Title);
        Assert.Equal("details", t.Description);
        Assert.Equal(Repo.Path, t.RepositoryPath);
        Assert.Equal("repo", t.RepositoryName);
        Assert.Equal(32, t.Id.Length);
        Assert.True(Guid.TryParseExact(t.Id, "N", out _));
        Assert.True(t.CreatedAt > before);
        Assert.False(t.IsSent);
        Assert.Null(t.SentAt);
        Assert.Null(t.SessionId);
        Assert.NotNull(s.Get(t.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankDescription_StoresNull(string? d)
    {
        var s = Store(NewPath());
        var t = await s.CreateAsync(Repo, "t", d);
        Assert.Null(t.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Create_BlankTitle_Throws(string? title)
    {
        var s = Store(NewPath());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => s.CreateAsync(Repo, title!, null));
        Assert.Empty(s.GetAll());
    }

    [Fact]
    public async Task Create_NullRepository_Throws()
    {
        var s = Store(NewPath());
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.CreateAsync(null!, "t", null));
    }

    // AC-5
    [Fact]
    public void ToPrompt_Formats()
    {
        Assert.Equal("T", new RepositoryTask { Title = "T" }.ToPrompt());
        Assert.Equal("T", new RepositoryTask { Title = "T", Description = "  " }.ToPrompt());
        Assert.Equal("T\n\nD", new RepositoryTask { Title = "T", Description = "D" }.ToPrompt());
    }

    // AC-2 persistence
    [Fact]
    public async Task Persistence_SurvivesRestart_AndNoTmpFiles()
    {
        var p = NewPath();
        var s = Store(p);
        Assert.Empty(s.GetAll());
        Assert.False(File.Exists(p));
        var t = await s.CreateAsync(Repo, "a", "b");
        await s.MarkSentAsync(t.Id, "sess");
        var t2 = await s.CreateAsync(Repo, "c", null);
        await s.UpdateAsync(t2.Id, "c2", "d");
        Assert.True(File.Exists(p));

        var r = Store(p);
        var all = r.GetAll();
        Assert.Equal(2, all.Count);
        var g = r.Get(t.Id)!;
        Assert.True(g.IsSent);
        Assert.Equal("sess", g.SessionId);
        Assert.NotNull(g.SentAt);
        Assert.Equal("a", g.Title);
        Assert.Equal("b", g.Description);
        Assert.Equal("c2", r.Get(t2.Id)!.Title);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p)!, "*.tmp"));
    }

    [Fact]
    public async Task CorruptFile_IsBackedUp_AndStoreStartsEmpty()
    {
        var p = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, "{ not json [[[");
        var s = Store(p);
        Assert.Empty(s.GetAll());
        var backups = Directory.GetFiles(Path.GetDirectoryName(p)!, "tasks.json.corrupt-*");
        Assert.Single(backups);
        Assert.Equal("{ not json [[[", await File.ReadAllTextAsync(backups[0]));
        var t = await s.CreateAsync(Repo, "ok", null);
        Assert.Single(Store(p).GetAll());
        Assert.Equal(t.Id, Store(p).GetAll()[0].Id);
    }

    [Fact]
    public async Task UnreadableFile_IsNotOverwritten()
    {
        var p = NewPath();
        var seed = Store(p);
        var t = await seed.CreateAsync(Repo, "keep", null);
        var original = await File.ReadAllTextAsync(p);

        TaskStore s;
        using (var fs = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            s = Store(p);
            Assert.Empty(s.GetAll());
            await Assert.ThrowsAsync<IOException>(() => s.CreateAsync(Repo, "new", null));
        }
        Assert.Equal(original, await File.ReadAllTextAsync(p));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p)!, "*.corrupt-*"));
        var fresh = Store(p);
        Assert.Equal(t.Id, Assert.Single(fresh.GetAll()).Id);
    }

    // AC-3
    [Fact]
    public async Task GetForRepository_FiltersCaseInsensitive_TrailingSeparator_OldestFirst()
    {
        var s = Store(NewPath());
        var a1 = await s.CreateAsync(Repo, "first", null);
        await Task.Delay(15);
        var b = await s.CreateAsync(Repo2, "b", null);
        await Task.Delay(15);
        var a2 = await s.CreateAsync(Repo, "second", null);
        await Task.Delay(15);
        var a3 = await s.CreateAsync(Repo, "third", null);

        var list = s.GetForRepository(Repo.Path.ToUpperInvariant() + Path.DirectorySeparatorChar);
        Assert.Equal(new[] { a1.Id, a2.Id, a3.Id }, list.Select(x => x.Id).ToArray());
        Assert.Equal(new[] { a1.Id, b.Id, a2.Id, a3.Id }, s.GetAll().Select(x => x.Id).ToArray());
        Assert.Single(s.GetForRepository(Repo2.Path));
        Assert.Empty(s.GetForRepository(Path.Combine(_dir, "nothing")));
    }

    // AC-4
    [Fact]
    public async Task Update_Pending_Works_BlankTitleThrows_MissingAndSentFalse()
    {
        var s = Store(NewPath());
        var t = await s.CreateAsync(Repo, "a", "b");
        Assert.True(await s.UpdateAsync(t.Id, "  new ", "  "));
        var g = s.Get(t.Id)!;
        Assert.Equal("new", g.Title);
        Assert.Null(g.Description);
        await Assert.ThrowsAsync<ArgumentException>(() => s.UpdateAsync(t.Id, "  ", "x"));
        Assert.Equal("new", s.Get(t.Id)!.Title);
        Assert.False(await s.UpdateAsync("missing", "x", null));

        await s.MarkSentAsync(t.Id, "s1");
        Assert.False(await s.UpdateAsync(t.Id, "changed", "x"));
        Assert.Equal("new", s.Get(t.Id)!.Title);
    }

    [Fact]
    public async Task Delete_PendingAndSent_MissingFalse()
    {
        var p = NewPath();
        var s = Store(p);
        var a = await s.CreateAsync(Repo, "a", null);
        var b = await s.CreateAsync(Repo, "b", null);
        await s.MarkSentAsync(b.Id, "s");
        Assert.True(await s.DeleteAsync(a.Id));
        Assert.True(await s.DeleteAsync(b.Id));
        Assert.False(await s.DeleteAsync(a.Id));
        Assert.False(await s.DeleteAsync("nope"));
        Assert.Empty(s.GetAll());
        Assert.Empty(Store(p).GetAll());
    }

    // AC-6
    [Fact]
    public async Task MarkSent_SetsFields_RejectsSecondAndMissingAndBlank()
    {
        var s = Store(NewPath());
        var t = await s.CreateAsync(Repo, "a", null);
        await Assert.ThrowsAsync<ArgumentException>(() => s.MarkSentAsync(t.Id, " "));
        Assert.False(s.Get(t.Id)!.IsSent);
        var before = DateTimeOffset.UtcNow;
        Assert.True(await s.MarkSentAsync(t.Id, "sess-1"));
        var g = s.Get(t.Id)!;
        Assert.True(g.IsSent);
        Assert.Equal("sess-1", g.SessionId);
        Assert.InRange((g.SentAt!.Value - before).TotalSeconds, -5, 5);
        Assert.False(await s.MarkSentAsync(t.Id, "sess-2"));
        Assert.Equal("sess-1", s.Get(t.Id)!.SessionId);
        Assert.False(await s.MarkSentAsync("missing", "x"));
    }

    // AC-9
    [Fact]
    public async Task DeleteBySession_DeletesAllLinked_KeepsOthers_RaisesChanged()
    {
        var p = NewPath();
        var s = Store(p);
        var a = await s.CreateAsync(Repo, "a", null);
        var b = await s.CreateAsync(Repo, "b", null);
        var c = await s.CreateAsync(Repo, "c", null);
        var d = await s.CreateAsync(Repo, "d", null);
        await s.MarkSentAsync(a.Id, "S");
        await s.MarkSentAsync(b.Id, "S");
        await s.MarkSentAsync(c.Id, "T");
        int changed = 0;
        s.Changed += () => changed++;

        Assert.Equal(2, await s.DeleteBySessionAsync("S"));
        Assert.Equal(1, changed);
        Assert.Equal(new[] { c.Id, d.Id }, s.GetAll().Select(x => x.Id).ToArray());
        Assert.Equal(2, Store(p).GetAll().Count);
    }

    [Fact]
    public async Task DeleteBySession_NoMatch_NoWriteNoChanged()
    {
        var p = NewPath();
        var s = Store(p);
        var a = await s.CreateAsync(Repo, "a", null);
        await s.MarkSentAsync(a.Id, "S");
        var stamp = File.GetLastWriteTimeUtc(p);
        var content = await File.ReadAllTextAsync(p);
        int changed = 0;
        s.Changed += () => changed++;
        await Task.Delay(50);
        Assert.Equal(0, await s.DeleteBySessionAsync("zzz"));
        Assert.Equal(0, changed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(p));
        Assert.Equal(content, await File.ReadAllTextAsync(p));
    }

    [Fact]
    public async Task DeleteBySession_PendingTasksWithoutSessionAreNotDeleted()
    {
        var s = Store(NewPath());
        await s.CreateAsync(Repo, "a", null);
        Assert.Equal(0, await s.DeleteBySessionAsync("anything"));
        Assert.Single(s.GetAll());
    }

    // Events
    [Fact]
    public async Task Changed_RaisedOnEachSuccessfulMutation_NotOnFailures()
    {
        var s = Store(NewPath());
        int n = 0;
        s.Changed += () => n++;
        var t = await s.CreateAsync(Repo, "a", null);
        Assert.Equal(1, n);
        await s.UpdateAsync(t.Id, "b", null);
        Assert.Equal(2, n);
        await s.MarkSentAsync(t.Id, "s");
        Assert.Equal(3, n);
        await s.UpdateAsync(t.Id, "c", null);
        await s.MarkSentAsync(t.Id, "s2");
        await s.DeleteAsync("missing");
        Assert.Equal(3, n);
        await s.DeleteAsync(t.Id);
        Assert.Equal(4, n);
    }

    // Copies
    [Fact]
    public async Task ReturnedTasks_AreCopies()
    {
        var s = Store(NewPath());
        var t = await s.CreateAsync(Repo, "a", "d");
        t.Title = "hacked";
        s.Get(t.Id)!.Title = "hacked2";
        s.GetAll()[0].Title = "hacked3";
        s.GetForRepository(Repo.Path)[0].Description = "x";
        var g = s.Get(t.Id)!;
        Assert.Equal("a", g.Title);
        Assert.Equal("d", g.Description);
        Assert.Null(s.Get("missing"));
    }

    // Concurrency
    [Fact]
    public async Task ConcurrentCreates_DoNotLoseTasks()
    {
        var p = NewPath();
        var s = Store(p);
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(() => s.CreateAsync(Repo, "t" + i, null)));
        var created = await Task.WhenAll(tasks);
        Assert.Equal(40, s.GetAll().Count);
        Assert.Equal(40, created.Select(x => x.Id).Distinct().Count());
        Assert.Equal(40, Store(p).GetAll().Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(p)!, "*.tmp"));
    }
}
