using AutoPilot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPilot.Tests
{
    public sealed class UsageCacheTests : IDisposable
    {
        private readonly SessionStateFixture _fixture = new();

        public void Dispose() => _fixture.Dispose();

        private UsageCache Create(Func<string?, bool>? filter = null, ILogger<UsageCache>? logger = null, string? directory = null) =>
            new(directory ?? _fixture.Root, "Orchestrator", filter ?? (_ => true), logger ?? NullLogger<UsageCache>.Instance);

        private void WriteSession(string id, string cwd, double nano) =>
            _fixture.Write(id, SessionStateFixture.Start(id, cwd), SessionStateFixture.User("prompt " + id), SessionStateFixture.Shutdown(nano, "2026-10-25T18:25:29.052Z"));

        private static SessionCost Cost(string id, double nano) => new()
        {
            SessionId = id,
            NanoAiu = nano,
            Agents = { ["dev"] = new AgentCost { NanoAiu = nano } },
        };

        [Fact]
        public void Loads_SessionsFromDisk()
        {
            WriteSession("a", @"D:\repo", 6_000_000_000);
            WriteSession("b", @"D:\repo", 4_000_000_000);

            var cache = Create();

            Assert.Equal(["a", "b"], cache.GetAll().Select(c => c.SessionId).Order());
            Assert.Equal("prompt a", cache.Get("a")!.Title);
            Assert.Equal(10, cache.GetTotals().AiCredits, 6);
            Assert.Equal(2, cache.GetTotals().SessionCount);
            Assert.Equal(2 * 5, cache.GetAgentTotals()["implementer"].AiCredits, 6);
        }

        [Fact]
        public void Filter_ExcludesNonAutoPilotDirectories()
        {
            WriteSession("a", @"D:\repo", 6_000_000_000);
            WriteSession("b", @"C:\Users\x", 4_000_000_000);
            _fixture.Write("c", SessionStateFixture.Shutdown(1_000_000_000, "2026-10-25T18:25:29.052Z"));

            var cache = Create(dir => dir == @"D:\repo");

            Assert.Equal("a", Assert.Single(cache.GetAll()).SessionId);
            Assert.Null(cache.Get("b"));
            Assert.Null(cache.Get("c"));
            Assert.Equal(6, cache.GetTotals().AiCredits, 6);
        }

        [Fact]
        public void SessionsWithoutShutdownOrUsage_AreOmitted()
        {
            _fixture.Write("a", SessionStateFixture.Start("a", @"D:\repo"));
            _fixture.Write("b", SessionStateFixture.Start("b", @"D:\repo"),
                """{"type":"session.shutdown","data":{"totalNanoAiu":0,"modelMetrics":{},"agentMetrics":{}},"id":"3","timestamp":"2026-10-25T18:20:00.000Z"}""");
            Directory.CreateDirectory(Path.Combine(_fixture.Root, "empty"));

            Assert.Empty(Create().GetAll());
        }

        [Fact]
        public void MissingDirectory_IsEmptyAndWarns()
        {
            var logger = new ListLogger();
            var cache = Create(logger: logger, directory: Path.Combine(_fixture.Root, "nope"));

            Assert.Empty(cache.GetAll());
            Assert.Equal(0, cache.GetTotals().SessionCount);
            Assert.Contains(logger.Levels, l => l == LogLevel.Warning);
            Assert.False(Directory.Exists(Path.Combine(_fixture.Root, "nope")));
        }

        [Fact]
        public void UnreadableOrMalformedFile_IsSkippedWithWarning()
        {
            WriteSession("good", @"D:\repo", 1_000_000_000);
            var bad = _fixture.Write("bad", "garbage");
            File.WriteAllBytes(bad, [0xFF, 0xFE, 0x00, 0x01]);
            var logger = new ListLogger();

            var cache = Create(logger: logger);

            Assert.Equal("good", Assert.Single(cache.GetAll()).SessionId);
        }

        [Fact]
        public void ThrowingFilter_SkipsSessionWithWarning()
        {
            WriteSession("a", @"D:\repo", 1_000_000_000);
            var logger = new ListLogger();

            var cache = Create(_ => throw new InvalidOperationException("boom"), logger);

            Assert.Empty(cache.GetAll());
            Assert.Contains(logger.Levels, l => l == LogLevel.Warning);
        }

        [Fact]
        public void Update_ReplacesEntryAndRaisesChanged()
        {
            WriteSession("a", @"D:\repo", 1_000_000_000);
            var cache = Create();
            cache.EnsureLoaded();
            var raised = 0;
            cache.Changed += () => raised++;

            cache.Update(Cost("a", 7_000_000_000));
            cache.Update(Cost("n", 1_000_000_000));

            Assert.Equal(2, raised);
            Assert.Equal(7, cache.Get("a")!.AiCredits, 6);
            Assert.Equal(2, cache.GetAll().Count);
        }

        [Fact]
        public void Update_BeforeLoad_IsNotOverwrittenByDisk()
        {
            WriteSession("a", @"D:\repo", 1_000_000_000);
            WriteSession("b", @"D:\repo", 2_000_000_000);
            var cache = Create();

            cache.Update(Cost("a", 9_000_000_000));

            Assert.Equal(9, cache.Get("a")!.AiCredits, 6);
            Assert.Equal(2, cache.Get("b")!.AiCredits, 6);
            cache.EnsureLoaded();
            Assert.Equal(9, cache.Get("a")!.AiCredits, 6);
        }

        [Fact]
        public void Update_RejectsNullAndBlankSessionId()
        {
            var cache = Create();

            Assert.Throws<ArgumentNullException>(() => cache.Update(null!));
            Assert.Throws<ArgumentException>(() => cache.Update(new SessionCost { SessionId = " " }));
        }

        [Fact]
        public void EnsureLoaded_RunsOnce_AndRaisesChangedAfterLoad()
        {
            WriteSession("a", @"D:\repo", 1_000_000_000);
            var scans = 0;
            var cache = Create(_ =>
            {
                Interlocked.Increment(ref scans);
                return true;
            });
            var raised = 0;
            cache.Changed += () => raised++;

            Parallel.For(0, 16, _ => cache.EnsureLoaded());
            cache.EnsureLoaded();
            _ = cache.GetAll();
            WriteSession("late", @"D:\repo", 1_000_000_000);

            Assert.Equal(1, scans);
            Assert.Equal(1, raised);
            Assert.Null(cache.Get("late"));
        }

        [Fact]
        public void HandlerException_IsSwallowed()
        {
            var cache = Create();
            cache.Changed += () => throw new InvalidOperationException("x");

            cache.Update(Cost("a", 1));

            Assert.NotNull(cache.Get("a"));
        }

        [Fact]
        public void Cache_NeverWritesToDisk()
        {
            WriteSession("a", @"D:\repo", 1_000_000_000);
            string Snapshot() => string.Join("|", Directory.EnumerateFileSystemEntries(_fixture.Root, "*", SearchOption.AllDirectories)
                .Order().Select(p => p + File.GetLastWriteTimeUtc(p).Ticks));
            var before = Snapshot();

            var cache = Create();
            cache.Update(Cost("a", 5_000_000_000));
            cache.Update(Cost("z", 5_000_000_000));
            _ = cache.GetAll();

            Assert.Equal(before, Snapshot());
        }

        private sealed class ListLogger : ILogger<UsageCache>
        {
            public List<LogLevel> Levels { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Levels.Add(logLevel);
        }
    }
}
