using System.Text;
using System.Text.Json;
using AutoPilot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoPilot.Tests;

public class UsageCacheAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uca-" + Guid.NewGuid().ToString("N"));
    private string SessionState => Path.Combine(_root, "session-state");

    public UsageCacheAcceptanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class CapLogger : ILogger<UsageCache>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Levels) Levels.Add(logLevel);
        }
    }

    private const string Orch = "Orchestrator!";

    // ---- fixture helpers ----
    private static string J(string s) => JsonSerializer.Serialize(s);

    private static string Start(string id, string cwd, string time = "2026-10-25T18:09:44.791Z") =>
        $$$"""{"type":"session.start","data":{"sessionId":{{{J(id)}}},"startTime":"{{{time}}}","context":{"cwd":{{{J(cwd)}}}}},"id":"e1","timestamp":"{{{time}}}","parentId":null}""";

    private static string User(string content, string time = "2026-10-25T18:10:00.000Z") =>
        $$"""{"type":"user.message","data":{"content":{{J(content)}}},"id":"e2","timestamp":"{{time}}"}""";

    private static string Models(double nano, int count, int inp, int outp, int cr, int cw, int rs) =>
        $$$"""{"claude-opus-5.5":{"requests":{"count":{{{count}}},"cost":15},"usage":{"inputTokens":{{{inp}}},"outputTokens":{{{outp}}},"cacheReadTokens":{{{cr}}},"cacheWriteTokens":{{{cw}}},"reasoningTokens":{{{rs}}}},"totalNanoAiu":{{{nano.ToString("F0")}}}}}""";

    private static string Shutdown(string time, double total, string agentMetrics, string? models = null) =>
        $$$"""{"type":"session.shutdown","data":{"totalNanoAiu":{{{total.ToString("F0")}}},"modelMetrics":{{{models ?? Models(total, 1, 1, 2, 3, 4, 5)}}},"agentMetrics":{{{agentMetrics}}}},"id":"e9","timestamp":"{{{time}}}"}""";

    private static string Main(double nano, int count = 1) =>
        "\"main\":" + $$$"""{"totalNanoAiu":{{{nano.ToString("F0")}}},"modelMetrics":{{{Models(nano, count, 1, 2, 3, 4, 5)}}}}""";

    private static string Sub(string inst, string name, string display, double nano, int count = 1) =>
        $$$"""{{{J(inst)}}}:{"agentName":{{{J(name)}}},"agentDisplayName":{{{J(display)}}},"totalNanoAiu":{{{nano.ToString("F0")}}},"modelMetrics":{{{Models(nano, count, 10, 20, 30, 40, 50)}}}}""";

    private string WriteSession(string id, params string[] lines)
    {
        var dir = Path.Combine(SessionState, id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "events.jsonl");
        File.WriteAllLines(file, lines);
        return file;
    }

    private UsageCache Cache(Func<string?, bool>? filter = null, CapLogger? log = null, string? dir = null) =>
        new(dir ?? SessionState, Orch, filter ?? (_ => true), log ?? new CapLogger());

    private static string Agents(params string[] entries) => "{" + string.Join(",", entries) + "}";

    // ================= AC-1 =================
    [Fact]
    public void AC1_SettingsNoLongerExposeUsageFile()
    {
        var t = typeof(AutoPilotSettings);
        Assert.Null(t.GetProperty("UsageFile"));
        Assert.Null(t.GetMethod("ResolveUsageFile"));
        Assert.Null(typeof(UsageCache).Assembly.GetType("AutoPilot.Services.UsageLedger"));
    }

    [Fact]
    public void AC1_UsageAccumulator_HasNoApply()
    {
        Assert.Null(typeof(UsageAccumulator).GetMethod("Apply"));
    }

    [Fact]
    public void AC1_AppSettingsJson_HasNoUsageFileKey()
    {
        var path = FindAppSettings();
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("UsageFile", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session-costs", text, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }

    private static string FindAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "AutoPilot", "appsettings.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("appsettings.json not found");
    }

    [Fact]
    public void AC1_CacheUse_CreatesAndDeletesNoFiles_AndIgnoresExistingSessionCostsJson()
    {
        var legacyDir = Path.Combine(_root, "usage");
        Directory.CreateDirectory(legacyDir);
        var legacy = Path.Combine(legacyDir, "session-costs.json");
        const string content = """{"sessions":[{"sessionId":"legacy","nanoAiu":9000000000,"inputTokens":1}]}""";
        File.WriteAllText(legacy, content);
        var file = WriteSession("s1", Start("s1", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 2e9, Agents(Main(2e9))));
        var before = Snapshot();

        var cache = Cache();
        cache.Update(new SessionCost { SessionId = "live", NanoAiu = 1e9 });
        _ = cache.GetAll(); _ = cache.GetTotals(); _ = cache.GetAgentTotals();

        Assert.Equal(before, Snapshot());
        Assert.Equal(content, File.ReadAllText(legacy));
        Assert.Null(cache.Get("legacy"));
        Assert.NotNull(cache.Get("s1"));
        Assert.True(File.Exists(file));
    }

    private List<string> Snapshot() =>
        Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => x + "|" + (File.Exists(x) ? new FileInfo(x).Length + "|" + File.GetLastWriteTimeUtc(x).Ticks : "d"))
            .ToList();

    // ================= AC-2 =================
    [Fact]
    public void AC2_Settings_ResolveSessionStateDirectory()
    {
        var cd = Path.Combine(_root, "cop");
        Assert.Equal(Path.Combine(Path.GetFullPath(cd), "session-state"), new AutoPilotSettings { CopilotDirectory = cd }.ResolveSessionStateDirectory());
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state"),
            new AutoPilotSettings().ResolveSessionStateDirectory());
    }

    [Fact]
    public void AC2_FullMapping_MainOrchestratorSubAgentsAggregated_TitleAndTimestamps()
    {
        WriteSession("sess-1",
            Start("sess-1", @"C:\r", "2026-10-25T18:09:44.791Z"),
            User("<current_datetime>2026-10-25T18:09:00+02:00</current_datetime>\n\nFix the bug please"),
            User("second message"),
            Shutdown("2026-10-25T18:25:29.052Z", 62e9, Agents(
                Main(40e9, 14),
                Sub("i1", "implementer", "Implementer", 10e9, 2),
                Sub("i2", "implementer", "Implementer", 12e9, 3),
                Sub("t1", "tester", "Tester", 5e9, 1)),
                models: Models(62e9, 20, 1, 2, 3, 4, 5)));

        var c = Cache().Get("sess-1");
        Assert.NotNull(c);
        Assert.Equal("sess-1", c!.SessionId);
        Assert.Equal(62.0, c.AiCredits, 6);
        Assert.Equal(62e9, c.NanoAiu);
        Assert.Equal(1, c.InputTokens);
        Assert.Equal(2, c.OutputTokens);
        Assert.Equal(3, c.CacheReadTokens);
        Assert.Equal(4, c.CacheWriteTokens);
        Assert.Equal(5, c.ReasoningTokens);
        Assert.Equal("claude-opus-5.5", Assert.Single(c.Models).Key);
        Assert.Equal(20, c.Models["claude-opus-5.5"].Requests);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 18, 9, 44, 791, TimeSpan.Zero), c.FirstSeen);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 18, 25, 29, 52, TimeSpan.Zero), c.LastUpdated);
        Assert.Equal("Fix the bug please", c.Title);

        Assert.Equal(3, c.Agents.Count);
        var orch = Assert.Single(c.Agents.Values, a => a.DisplayName == Orch);
        Assert.Equal(40.0, orch.AiCredits, 6);
        Assert.Equal(14, orch.Requests);
        Assert.DoesNotContain("main", c.Agents.Values.Select(a => a.DisplayName));
        var impl = c.Agents["implementer"];
        Assert.Equal("Implementer", impl.DisplayName);
        Assert.Equal(22.0, impl.AiCredits, 6);
        Assert.Equal(5, impl.Requests);
        Assert.Equal(20, impl.InputTokens);
        Assert.Equal(40, impl.OutputTokens);
        Assert.Equal(5.0, c.Agents["tester"].AiCredits, 6);
    }

    [Fact]
    public void AC2_MatchesLiveMapping_ToSnapshotShape_ForReaderAndAccumulator()
    {
        var file = WriteSession("m", Start("m", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 3e9, Agents(Main(1e9), Sub("i1", "implementer", "Implementer", 2e9))));
        var viaReader = SessionEventsReader.TryReadCost(file, Orch);
        Assert.NotNull(viaReader);
        Assert.Equal("m", viaReader!.SessionId);
        var viaCache = Cache().Get("m")!;
        Assert.Equal(viaReader.NanoAiu, viaCache.NanoAiu);
        Assert.Equal(viaReader.Agents.Keys.OrderBy(x => x), viaCache.Agents.Keys.OrderBy(x => x));

        var info = SessionEventsReader.TryReadSession(file, Orch);
        Assert.NotNull(info);
        Assert.Equal("m", info!.SessionId);
        Assert.Equal(@"C:\r", info.WorkingDirectory);
        Assert.Equal(3e9, info.Cost!.NanoAiu);
    }

    [Fact]
    public void AC2_LastShutdownWins_ResumedSession()
    {
        WriteSession("r",
            Start("r", @"C:\r"),
            User("hello"),
            Shutdown("2026-10-25T18:25:29.052Z", 4e9, Agents(Main(4e9, 3))),
            Start("r", @"C:\r", "2026-10-26T08:00:00.000Z"),
            Shutdown("2026-10-26T09:00:00.000Z", 10e9, Agents(Main(10e9, 7))));

        var c = Cache().Get("r")!;
        Assert.Equal(10.0, c.AiCredits, 6);   // not 14
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero), c.LastUpdated);
        Assert.Equal(7, c.Agents.Values.Single().Requests);
        Assert.Equal(10.0, Cache().GetTotals().AiCredits, 6);
        Assert.Equal("hello", c.Title);
    }

    [Fact]
    public void AC2_FirstSeen_IsFirstSessionStart_WhenResumed()
    {
        WriteSession("r",
            Start("r", @"C:\r", "2026-10-25T18:09:44.791Z"),
            Shutdown("2026-10-25T18:25:29.052Z", 4e9, Agents(Main(4e9))),
            Start("r", @"C:\r", "2026-10-26T08:00:00.000Z"),
            Shutdown("2026-10-26T09:00:00.000Z", 10e9, Agents(Main(10e9))));
        var c = Cache().Get("r")!;
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 18, 9, 44, 791, TimeSpan.Zero), c.FirstSeen);
    }

    [Fact]
    public void AC2_Title_TruncatedTo100()
    {
        WriteSession("t", Start("t", @"C:\r"), User(new string('x', 500)), Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));
        var title = Cache().Get("t")!.Title;
        Assert.NotNull(title);
        Assert.True(title!.Length <= UsageAccumulator.MaxTitleLength + 1, title.Length.ToString()); // allow ellipsis
        Assert.StartsWith("xxxxx", title);
        Assert.Equal(UsageAccumulator.Truncate(new string('x', 500)), title);
        Assert.Equal(100, UsageAccumulator.MaxTitleLength);
    }

    [Fact]
    public void AC2_Omissions_NoShutdown_ZeroUsage_NoEventsFile()
    {
        WriteSession("noshut", Start("noshut", @"C:\r"), User("hi"));
        WriteSession("zero", Start("zero", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 0, Agents(Main(0)), models: "{}"));
        Directory.CreateDirectory(Path.Combine(SessionState, "empty-dir"));
        File.WriteAllText(Path.Combine(SessionState, "stray.txt"), "x");
        WriteSession("good", Start("good", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));

        var cache = Cache();
        Assert.Equal(["good"], cache.GetAll().Select(c => c.SessionId).ToArray());
        Assert.Null(cache.Get("noshut"));
        Assert.Null(cache.Get("zero"));
        Assert.Equal(1, cache.GetTotals().SessionCount);
    }

    [Fact]
    public void AC2_ShutdownThenLaterResumeWithoutShutdown_UsesLastShutdown()
    {
        WriteSession("r", Start("r", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 4e9, Agents(Main(4e9))),
            Start("r", @"C:\r", "2026-10-26T08:00:00.000Z"), User("more"));
        Assert.Equal(4.0, Cache().Get("r")!.AiCredits, 6);
    }

    [Fact]
    public void AC2_TotalsAndAgentTotals_AcrossSessions()
    {
        WriteSession("a", Start("a", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 3e9, Agents(Main(1e9), Sub("i", "implementer", "Implementer", 2e9))));
        WriteSession("b", Start("b", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 5e9, Agents(Main(2e9), Sub("i", "implementer", "Implementer", 3e9))));
        var cache = Cache();
        var t = cache.GetTotals();
        Assert.Equal(2, t.SessionCount);
        Assert.Equal(8.0, t.AiCredits, 6);
        Assert.Equal(2, cache.GetAll().Count);
        var ag = cache.GetAgentTotals();
        Assert.Equal(5.0, ag["implementer"].AiCredits, 6);
        Assert.Equal(3.0, ag.Values.Single(a => a.DisplayName == Orch).AiCredits, 6);
    }

    [Fact]
    public void AC2_RealSamples_WhenPresent_LoadWithoutThrowing_AndNoWrites()
    {
        const string real = @"D:\dev\AutoPilot\Copilot\session-state";
        if (!Directory.Exists(real)) return;
        var before = Directory.GetFiles(real, "events.jsonl", SearchOption.AllDirectories).Select(f => (f, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f))).ToList();
        var cache = new UsageCache(real, "Orchestrator", _ => true, new CapLogger());
        var all = cache.GetAll();
        Assert.All(all, c => Assert.True(UsageAccumulator.HasUsage(c)));
        Assert.Equal(all.Count, cache.GetTotals().SessionCount);
        var after = Directory.GetFiles(real, "events.jsonl", SearchOption.AllDirectories).Select(f => (f, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f))).ToList();
        Assert.Equal(before, after);
    }

    // ================= AC-3 =================
    [Fact]
    public void AC3_OnlyAutoPilotDirectories_AreCached_UsingConversationDirectoryRule()
    {
        var main = Path.Combine(_root, "repo");
        var wt = main + ".worktrees";
        string Cwd(string s) => s;
        void Add(string id, string cwd) => WriteSession(id, Start(id, cwd), Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));
        Add("main", main);
        Add("wtree", Path.Combine(wt, "abc"));
        Add("wtreeDeep", Path.Combine(wt, "abc", "sub"));
        Add("cli", Path.Combine(_root, "home"));
        Add("sibling", main + "-other");
        Add("wtRootItself", wt + "x");

        var cache = Cache(d => AgentChatSession.IsConversationDirectory(d, main, wt));
        var ids = cache.GetAll().Select(c => c.SessionId).OrderBy(x => x).ToArray();
        // Expectation derived from the shared rule itself, plus the explicit AC cases.
        var expected = new[] { "main", "wtree", "wtreeDeep", "cli", "sibling", "wtRootItself" }
            .Where(id => AgentChatSession.IsConversationDirectory(Directory(id), main, wt)).OrderBy(x => x).ToArray();
        Assert.Equal(expected, ids);
        Assert.Contains("main", ids);
        Assert.Contains("wtree", ids);
        Assert.DoesNotContain("cli", ids);
        Assert.DoesNotContain("sibling", ids);
        Assert.Null(cache.Get("cli"));

        string Directory(string id) => id switch
        {
            "main" => main, "wtree" => Path.Combine(wt, "abc"), "wtreeDeep" => Path.Combine(wt, "abc", "sub"),
            "cli" => Path.Combine(_root, "home"), "sibling" => main + "-other", _ => wt + "x",
        };
    }

    [Fact]
    public void AC3_ExcludedSessions_NotInTotalsNorAgentTotals()
    {
        WriteSession("in", Start("in", @"C:\in"), Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));
        WriteSession("out", Start("out", @"C:\out"), Shutdown("2026-10-25T18:25:29.052Z", 7e9, Agents(Main(7e9), Sub("i", "implementer", "Implementer", 1e9))));
        var cache = Cache(d => d == @"C:\in");
        Assert.Equal(1, cache.GetTotals().SessionCount);
        Assert.Equal(1.0, cache.GetTotals().AiCredits, 6);
        Assert.Single(cache.GetAll());
        Assert.DoesNotContain("implementer", cache.GetAgentTotals().Keys);
        Assert.Equal(1.0, cache.GetAgentTotals().Values.Sum(a => a.AiCredits), 6);
    }

    [Fact]
    public void AC3_MissingCwd_IsPassedToFilterAsNull_AndFilterDecides()
    {
        WriteSession("nocwd",
            """{"type":"session.start","data":{"sessionId":"nocwd","startTime":"2026-10-25T18:09:44.791Z"},"timestamp":"2026-10-25T18:09:44.791Z"}""",
            Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));
        Assert.Null(Cache(d => !string.IsNullOrEmpty(d)).Get("nocwd"));
        Assert.NotNull(Cache(_ => true).Get("nocwd"));
    }

    [Fact]
    public void AC3_RealFilter_FromRepositoryCatalog_Settings()
    {
        var work = Path.Combine(_root, "work");
        var repo = Path.Combine(work, "myrepo");
        System.IO.Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var settings = new AutoPilotSettings { WorkingDirectory = work };
        var filter = AgentChatSession.CreateConversationDirectoryFilter(new RepositoryCatalog(Options.Create(settings)), settings);
        var wtRoot = settings.ResolveWorktreesDirectory(repo);

        void Add(string id, string cwd) => WriteSession(id, Start(id, cwd), Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));
        Add("a", repo);
        Add("b", Path.Combine(wtRoot, "sid"));
        Add("c", Path.Combine(_root, ".copilot-cli-session"));
        var ids = Cache(filter).GetAll().Select(c => c.SessionId).OrderBy(x => x).ToArray();
        Assert.Equal(["a", "b"], ids);
    }

    // ================= AC-4 =================
    [Fact]
    public void AC4_MissingDirectory_YieldsEmpty_NoThrow_NoDirectoryCreated()
    {
        var missing = Path.Combine(_root, "nope", "session-state");
        var cache = Cache(dir: missing);
        cache.EnsureLoaded();
        Assert.Empty(cache.GetAll());
        Assert.Equal(0, cache.GetTotals().SessionCount);
        Assert.Empty(cache.GetAgentTotals());
        Assert.Null(cache.Get("x"));
        Assert.False(System.IO.Directory.Exists(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void AC4_MalformedLinesAndFiles_AreSkipped_ValidDataKept_NotModified()
    {
        var shutdown = Shutdown("2026-10-25T18:25:29.052Z", 2e9, Agents(Main(2e9)));
        WriteSession("mixed", Start("mixed", @"C:\r"), "{ not json", "", "   ", "null", "[1,2]", "\"str\"", """{"type":"session.shutdown","data":42}""", shutdown, "{\"type\":\"session.shut");
        WriteSession("garbage", "\0\0\0\u0001\u0002 binary");
        WriteSession("emptyfile");
        WriteSession("badshutdown", Start("badshutdown", @"C:\r"), """{"type":"session.shutdown","data":{"totalNanoAiu":"abc","modelMetrics":[]}}""");
        WriteSession("good", Start("good", @"C:\r"), shutdown);
        var before = Snapshot();
        var log = new CapLogger();

        var cache = Cache(log: log);
        var ids = cache.GetAll().Select(c => c.SessionId).OrderBy(x => x).ToArray();

        Assert.Contains("good", ids);
        Assert.Contains("mixed", ids);
        Assert.DoesNotContain("garbage", ids);
        Assert.DoesNotContain("emptyfile", ids);
        Assert.Equal(2.0, cache.Get("mixed")!.AiCredits, 6);
        Assert.Equal(before, Snapshot());
    }

    // AC-4: "malformed lines are skipped with a logged warning"
    [Fact]
    public void AC4_MalformedLines_AreReportedWithLoggedWarning()
    {
        WriteSession("mixed", Start("mixed", @"C:\r"), "{ not json", Shutdown("2026-10-25T18:25:29.052Z", 2e9, Agents(Main(2e9))));
        var log = new CapLogger();
        Assert.NotNull(Cache(log: log).Get("mixed"));
        Assert.Contains(LogLevel.Warning, log.Levels);
    }

    // AC-4: "missing directory ... skipped with a logged warning"
    [Fact]
    public void AC4_MissingDirectory_LogsWarning()
    {
        var log = new CapLogger();
        Cache(log: log, dir: Path.Combine(_root, "absent")).EnsureLoaded();
        Assert.Contains(LogLevel.Warning, log.Levels);
    }

    [Fact]
    public void AC4_UnreadableFile_ExclusiveLock_IsSkippedWithWarning_OthersLoad()
    {
        if (!OperatingSystem.IsWindows()) return;
        var locked = WriteSession("locked", Start("locked", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 9e9, Agents(Main(9e9))));
        WriteSession("ok", Start("ok", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));
        var log = new CapLogger();
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var cache = Cache(log: log);
            Assert.Equal(["ok"], cache.GetAll().Select(c => c.SessionId).ToArray());
        }
        Assert.Contains(LogLevel.Warning, log.Levels);
    }

    [Fact]
    public void AC4_FileOpenForWriting_WithShareReadWrite_IsReadable_AndStillWritableDuringAndAfterLoad()
    {
        var file = WriteSession("live", Start("live", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 2e9, Agents(Main(2e9))));
        using var writer = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var cache = Cache();
        Assert.Equal(2.0, cache.Get("live")!.AiCredits, 6);

        var bytes = Encoding.UTF8.GetBytes("\n" + User("later") + "\n");
        writer.Write(bytes); writer.Flush();      // the runtime keeps writing after we loaded
        // and we hold no handle open: file can be renamed/deleted
        writer.Dispose();
        File.Move(file, file + ".moved");
        Assert.True(File.Exists(file + ".moved"));
    }

    [Fact]
    public void AC4_LoadHappensOnce_LaterDiskChangesIgnored_EnsureLoadedIdempotent()
    {
        var file = WriteSession("a", Start("a", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 2e9, Agents(Main(2e9))));
        var cache = Cache();
        int changed = 0;
        cache.Changed += () => changed++;
        cache.EnsureLoaded();
        var afterFirst = changed;
        cache.EnsureLoaded();
        cache.EnsureLoaded();
        _ = cache.GetAll();
        Assert.Equal(afterFirst, changed);
        Assert.True(afterFirst <= 1);

        File.WriteAllLines(file, [Start("a", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 99e9, Agents(Main(99e9)))]);
        WriteSession("b", Start("b", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));
        Assert.Equal(2.0, cache.Get("a")!.AiCredits, 6);
        Assert.Null(cache.Get("b"));
    }

    [Fact]
    public void AC4_ChangedRaisedAfterInitialLoad_WhenFirstReadTriggersIt()
    {
        WriteSession("a", Start("a", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 2e9, Agents(Main(2e9))));
        var cache = Cache();
        int changed = 0;
        cache.Changed += () => changed++;
        cache.EnsureLoaded();
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task AC4_ParallelEnsureLoaded_LoadsOnce_ConsistentResult()
    {
        for (var i = 0; i < 30; i++)
            WriteSession("s" + i, Start("s" + i, @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", (i + 1) * 1e9, Agents(Main((i + 1) * 1e9))));
        var cache = Cache();
        int changed = 0;
        cache.Changed += () => Interlocked.Increment(ref changed);
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 16).Select(k => Task.Run(() =>
        {
            gate.Wait();
            if (k % 2 == 0) cache.EnsureLoaded();
            return cache.GetAll().Count;
        })).ToArray();
        gate.Set();
        var counts = await Task.WhenAll(tasks);
        Assert.All(counts, c => Assert.Equal(30, c));
        Assert.Equal(1, changed);
        Assert.Equal(30, cache.GetTotals().SessionCount);
    }

    [Fact]
    public async Task AC4_ConcurrentUpdatesAndReads_NoExceptions_AllPresent()
    {
        var cache = Cache();
        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            var k = i;
            tasks.Add(Task.Run(() => cache.Update(new SessionCost { SessionId = "u" + k % 20, NanoAiu = (k + 1) * 1e9 })));
            tasks.Add(Task.Run(() => { _ = cache.GetAll(); _ = cache.GetTotals(); _ = cache.GetAgentTotals(); _ = cache.Get("u1"); }));
        }
        await Task.WhenAll(tasks);
        Assert.Equal(20, cache.GetAll().Count);
    }

    // ================= AC-5 / AC-6 =================
    [Fact]
    public void AC5_Update_InsertsReplaces_RaisesChanged_Get_ReturnsLive()
    {
        var cache = Cache();
        int changed = 0;
        cache.Changed += () => changed++;
        cache.EnsureLoaded();
        var baseline = changed;
        cache.Update(new SessionCost { SessionId = "a", NanoAiu = 1e9 });
        cache.Update(new SessionCost { SessionId = "a", NanoAiu = 4e9 });
        Assert.Equal(baseline + 2, changed);
        Assert.Single(cache.GetAll());
        Assert.Equal(4e9, cache.Get("a")!.NanoAiu);
        Assert.Null(cache.Get("zzz"));
    }

    [Fact]
    public void AC5_Update_Invalid_Throws()
    {
        var cache = Cache();
        Assert.ThrowsAny<ArgumentException>(() => cache.Update(new SessionCost { SessionId = "" }));
        Assert.ThrowsAny<ArgumentException>(() => cache.Update(new SessionCost { SessionId = "   " }));
        Assert.ThrowsAny<ArgumentException>(() => cache.Update(null!));
        Assert.Empty(cache.GetAll());
    }

    [Fact]
    public void AC5_Update_ForSessionNotMatchingFilter_IsStillCached_LiveSessionsAreAutoPilot()
    {
        var cache = Cache(_ => false);
        cache.Update(new SessionCost { SessionId = "live", NanoAiu = 1e9 });
        Assert.NotNull(cache.Get("live"));
        Assert.Equal(1, cache.GetTotals().SessionCount);
    }

    [Fact]
    public void AC6_ResumedSession_LiveUpdateReplacesCachedValue_NoDoubleCount()
    {
        WriteSession("r", Start("r", @"C:\r"), User("t"), Shutdown("2026-10-25T18:25:29.052Z", 4e9, Agents(Main(4e9, 3))));
        var cache = Cache();
        Assert.Equal(4.0, cache.GetTotals().AiCredits, 6);

        // live SDK value after resume is cumulative (4 + 2)
        cache.Update(new SessionCost { SessionId = "r", Title = "t", NanoAiu = 6e9, InputTokens = 7 });
        Assert.Equal(6.0, cache.Get("r")!.AiCredits, 6);
        Assert.Equal(1, cache.GetTotals().SessionCount);
        Assert.Equal(6.0, cache.GetTotals().AiCredits, 6);
        Assert.Equal(7, cache.GetTotals().InputTokens);
    }

    [Fact]
    public void AC6_LiveUpdateBeforeLoad_IsNeverOverwrittenByOlderDiskData()
    {
        WriteSession("r", Start("r", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 4e9, Agents(Main(4e9, 3))));
        WriteSession("other", Start("other", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 1e9, Agents(Main(1e9))));
        var cache = Cache();
        cache.Update(new SessionCost { SessionId = "r", NanoAiu = 6e9 });   // before any read/EnsureLoaded
        cache.EnsureLoaded();
        Assert.Equal(6e9, cache.Get("r")!.NanoAiu);
        Assert.Equal(1e9, cache.Get("other")!.NanoAiu);
        Assert.Equal(7.0, cache.GetTotals().AiCredits, 6);
    }

    [Fact]
    public void AC6_LiveUpdateOfLowerValue_StillReplaces()
    {
        WriteSession("r", Start("r", @"C:\r"), Shutdown("2026-10-25T18:25:29.052Z", 4e9, Agents(Main(4e9, 3))));
        var cache = Cache();
        cache.Update(new SessionCost { SessionId = "r", NanoAiu = 1e9 });
        Assert.Equal(1e9, cache.Get("r")!.NanoAiu);
    }

    [Fact]
    public void AC6_UsageAccumulator_ToSessionCost_ToSnapshot_Helpers()
    {
        var snap = new UsageSnapshot
        {
            NanoAiu = 3e9, InputTokens = 5, OutputTokens = 6, CacheReadTokens = 7, CacheWriteTokens = 8, ReasoningTokens = 9,
            Agents = { ["x"] = new AgentCost { DisplayName = "X", Requests = 2, NanoAiu = 3e9 } },
        };
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var last = first.AddHours(2);
        var c = UsageAccumulator.ToSessionCost("id", snap, "title", first, last);
        Assert.Equal("id", c.SessionId);
        Assert.Equal("title", c.Title);
        Assert.Equal(first, c.FirstSeen);
        Assert.Equal(last, c.LastUpdated);
        Assert.Equal(3.0, c.AiCredits, 6);
        Assert.Equal(9, c.ReasoningTokens);
        Assert.Equal("X", c.Agents["x"].DisplayName);
        Assert.False(UsageAccumulator.HasUsage(new SessionCost { SessionId = "e" }));
        Assert.Null(UsageAccumulator.Truncate(null));
        Assert.Equal("abc", UsageAccumulator.Truncate("abc"));
    }
}
