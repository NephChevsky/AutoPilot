using AutoPilot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPilot.Tests
{
    public sealed class UsageAccumulatorTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static UsageSnapshot Snap(double premium, double nano, long input = 0, long output = 0, double requests = 0, string model = "gpt", string agent = "orchestrator") =>
            new()
            {
                PremiumRequests = premium,
                NanoAiu = nano,
                InputTokens = input,
                OutputTokens = output,
                Models = { [model] = new ModelCost { Requests = requests, PremiumCost = premium, NanoAiu = nano, InputTokens = input, OutputTokens = output } },
                Agents = { [agent] = new AgentCost { DisplayName = agent, Requests = requests, PremiumCost = premium, NanoAiu = nano, InputTokens = input, OutputTokens = output } },
            };

        [Fact]
        public void Apply_NewSession_UsesCurrentTotals()
        {
            var cost = UsageAccumulator.Apply("s1", null, UsageSnapshot.Zero, Snap(15, 67_640_460_000, 100, 50, 15), "hello", T0);

            Assert.Equal("s1", cost.SessionId);
            Assert.Equal("hello", cost.Title);
            Assert.Equal(T0, cost.FirstSeen);
            Assert.Equal(15, cost.PremiumRequests);
            Assert.Equal(67.64046, cost.AiCredits, 5);
            Assert.Equal(100, cost.InputTokens);
            Assert.Equal(15, cost.Models["gpt"].Requests);
            Assert.Equal(100, cost.Agents["orchestrator"].InputTokens);
        }

        [Fact]
        public void Apply_Resume_AddsOnlyDeltaSinceBaseline()
        {
            var stored = UsageAccumulator.Apply("s1", null, UsageSnapshot.Zero, Snap(10, 10_000_000_000, 100, 10, 10), "t", T0);
            // Resumed: runtime metrics already include 4 premium requests from earlier.
            var baseline = Snap(4, 4_000_000_000, 40, 4, 4);
            var current = Snap(6, 6_000_000_000, 70, 9, 6);

            var cost = UsageAccumulator.Apply("s1", stored, baseline, current, "ignored", T0.AddHours(1));

            Assert.Equal(12, cost.PremiumRequests);
            Assert.Equal(12, cost.AiCredits, 6);
            Assert.Equal(130, cost.InputTokens);
            Assert.Equal(15, cost.OutputTokens);
            Assert.Equal(12, cost.Models["gpt"].Requests);
            Assert.Equal(130, cost.Agents["orchestrator"].InputTokens);
            Assert.Equal("t", cost.Title);
            Assert.Equal(T0, cost.FirstSeen);
            Assert.Equal(T0.AddHours(1), cost.LastUpdated);
        }

        [Fact]
        public void Apply_RepeatedSnapshotsFromSameStoredCopy_DoNotDoubleCount()
        {
            var stored = Apply(Snap(10, 0));
            var baseline = Snap(0, 0);

            var first = UsageAccumulator.Apply("s1", stored, baseline, Snap(2, 0), null, T0);
            var second = UsageAccumulator.Apply("s1", stored, baseline, Snap(5, 0), null, T0);

            Assert.Equal(12, first.PremiumRequests);
            Assert.Equal(15, second.PremiumRequests);
            Assert.Equal(10, stored.PremiumRequests);
        }

        [Fact]
        public void Apply_NegativeDelta_IsClamped()
        {
            var stored = Apply(Snap(10, 5_000_000_000, 50, 0, 10));

            var cost = UsageAccumulator.Apply("s1", stored, Snap(8, 8_000_000_000, 80, 0, 8), Snap(3, 3_000_000_000, 30, 0, 3), null, T0);

            Assert.Equal(10, cost.PremiumRequests);
            Assert.Equal(5, cost.AiCredits, 6);
            Assert.Equal(50, cost.InputTokens);
            Assert.Equal(10, cost.Models["gpt"].Requests);
        }

        [Fact]
        public void Apply_NewModelAndAgent_AreAddedAndOldOnesKept()
        {
            var stored = Apply(Snap(1, 0, model: "a", agent: "x"));

            var cost = UsageAccumulator.Apply("s1", stored, UsageSnapshot.Zero, Snap(2, 0, model: "b", agent: "y"), null, T0);

            Assert.Equal(["a", "b"], cost.Models.Keys.Order());
            Assert.Equal(["x", "y"], cost.Agents.Keys.Order());
            Assert.Equal(3, cost.PremiumRequests);
        }

        [Fact]
        public void Apply_DoesNotModifyInputs()
        {
            var stored = Apply(Snap(1, 0));
            var current = Snap(5, 0);
            UsageAccumulator.Apply("s1", stored, UsageSnapshot.Zero, current, null, T0);

            Assert.Equal(1, stored.PremiumRequests);
            Assert.Equal(1, stored.Models["gpt"].PremiumCost);
            Assert.Equal(5, current.PremiumRequests);
        }

        [Fact]
        public void Truncate_LimitsLengthAndHandlesBlank()
        {
            Assert.Null(UsageAccumulator.Truncate("  "));
            Assert.Null(UsageAccumulator.Truncate(null));
            Assert.Equal(100, UsageAccumulator.Truncate(new string('a', 250))!.Length);
            Assert.Equal("hi", UsageAccumulator.Truncate("  hi "));
        }

        [Fact]
        public void HasUsage_FalseForZeroDelta_TrueForAnySpend()
        {
            Assert.False(UsageAccumulator.HasUsage(Apply(Snap(0, 0))));
            Assert.True(UsageAccumulator.HasUsage(Apply(Snap(1, 0))));
            Assert.True(UsageAccumulator.HasUsage(Apply(Snap(0, 5))));
            Assert.True(UsageAccumulator.HasUsage(Apply(Snap(0, 0, input: 3))));
            Assert.True(UsageAccumulator.HasUsage(Apply(Snap(0, 0, output: 3))));
        }

        private static SessionCost Apply(UsageSnapshot snapshot) =>
            UsageAccumulator.Apply("s1", null, UsageSnapshot.Zero, snapshot, null, T0);
    }

    public sealed class UsageLedgerTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "autopilot-ledger-" + Guid.NewGuid().ToString("N"));

        private string FilePath => Path.Combine(_directory, "usage", "session-costs.json");

        private UsageLedger Create(ILogger<UsageLedger>? logger = null) => new(FilePath, logger ?? NullLogger<UsageLedger>.Instance);

        private static SessionCost Cost(string id, double premium, double nano, long input = 0) => new()
        {
            SessionId = id,
            Title = "title " + id,
            FirstSeen = DateTimeOffset.UnixEpoch,
            LastUpdated = DateTimeOffset.UnixEpoch,
            PremiumRequests = premium,
            NanoAiu = nano,
            InputTokens = input,
            Models = { ["gpt"] = new ModelCost { Requests = 2, PremiumCost = premium, NanoAiu = nano } },
            Agents = { ["orchestrator"] = new AgentCost { DisplayName = "Orchestrator", NanoAiu = nano } },
        };

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Fact]
        public async Task Record_CreatesFileAndSurvivesRestart()
        {
            var ledger = Create();
            Assert.False(File.Exists(FilePath));

            await ledger.RecordAsync(Cost("a", 3, 2_500_000_000, 10));

            Assert.True(File.Exists(FilePath));
            var reloaded = Create().Get("a");
            Assert.NotNull(reloaded);
            Assert.Equal(3, reloaded.PremiumRequests);
            Assert.Equal(2.5, reloaded.AiCredits, 6);
            Assert.Equal("title a", reloaded.Title);
            Assert.Equal(2, reloaded.Models["gpt"].Requests);
            Assert.Equal("Orchestrator", reloaded.Agents["orchestrator"].DisplayName);
        }

        [Fact]
        public async Task Record_UpsertsBySessionId()
        {
            var ledger = Create();
            await ledger.RecordAsync(Cost("a", 1, 0));
            await ledger.RecordAsync(Cost("a", 4, 0));

            Assert.Single(ledger.GetAll());
            Assert.Equal(4, Create().Get("a")!.PremiumRequests);
        }

        [Fact]
        public async Task GetTotals_SumsAllSessions()
        {
            var ledger = Create();
            await ledger.RecordAsync(Cost("a", 1.5, 1_000_000_000, 10));
            await ledger.RecordAsync(Cost("b", 2, 3_000_000_000, 5));

            var totals = ledger.GetTotals();

            Assert.Equal(2, totals.SessionCount);
            Assert.Equal(3.5, totals.PremiumRequests);
            Assert.Equal(4, totals.AiCredits, 6);
            Assert.Equal(15, totals.InputTokens);
        }

        [Fact]
        public void EmptyLedger_HasZeroTotals()
        {
            var ledger = Create();

            Assert.Null(ledger.Get("missing"));
            Assert.Equal(0, ledger.GetTotals().SessionCount);
            Assert.False(File.Exists(FilePath));
        }

        [Fact]
        public async Task Record_RaisesChanged()
        {
            var ledger = Create();
            var raised = 0;
            ledger.Changed += () => raised++;

            await ledger.RecordAsync(Cost("a", 1, 0));

            Assert.Equal(1, raised);
        }

        [Fact]
        public async Task CorruptFile_IsBackedUpWarnedAndLedgerKeepsWorking()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, "{ this is not json");
            var logger = new ListLogger();
            var ledger = Create(logger);

            Assert.Empty(ledger.GetAll());
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
            var backups = Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "session-costs.json.corrupt-*");
            Assert.Single(backups);
            Assert.Equal("{ this is not json", await File.ReadAllTextAsync(backups[0]));

            await ledger.RecordAsync(Cost("a", 1, 0));
            Assert.Single(Create().GetAll());
        }

        [Fact]
        public async Task LockedFile_IsNeverOverwritten_AndRecoversAfterRelease()
        {
            await Create().RecordAsync(Cost("a", 1, 0));
            await Create().RecordAsync(Cost("b", 2, 0));
            var ledger = Create();

            using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Empty(ledger.GetAll());
                Assert.Null(ledger.Get("a"));
                Assert.Equal(0, ledger.GetTotals().SessionCount);
                await Assert.ThrowsAnyAsync<IOException>(() => ledger.RecordAsync(Cost("c", 3, 0)));
            }

            Assert.Equal(["a", "b"], Create().GetAll().Select(c => c.SessionId).Order());
            Assert.Equal(2, ledger.GetAll().Count);
            await ledger.RecordAsync(Cost("c", 3, 0));
            Assert.Equal(3, Create().GetAll().Count);
        }

        [Theory]
        [InlineData("{\"sessions\":null}")]
        [InlineData("null")]
        [InlineData("[]")]
        public async Task StructurallyWrongFile_IsTreatedAsCorrupt(string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, content);
            var logger = new ListLogger();
            var ledger = Create(logger);

            Assert.Empty(ledger.GetAll());
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
            var backups = Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "session-costs.json.corrupt-*");
            Assert.Single(backups);
            Assert.Equal(content, await File.ReadAllTextAsync(backups[0]));

            await ledger.RecordAsync(Cost("a", 1, 0));
            Assert.Single(Create().GetAll());
        }

        [Fact]
        public async Task NullOrIdLessEntries_AreSkipped()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, "{\"sessions\":[null,{\"sessionId\":\"\"},{\"sessionId\":\"ok\",\"premiumRequests\":2}]}");

            var all = Create().GetAll();

            Assert.Equal("ok", Assert.Single(all).SessionId);
        }

        [Fact]
        public async Task ConcurrentRecords_AreAllPersisted()
        {
            var ledger = Create();

            await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => ledger.RecordAsync(Cost("s" + i, 1, 1_000_000_000)))));

            Assert.Equal(40, ledger.GetAll().Count);
            var reloaded = Create();
            Assert.Equal(40, reloaded.GetAll().Count);
            Assert.Equal(40, reloaded.GetTotals().PremiumRequests);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
        }

        [Fact]
        public async Task ResumeFlow_ThroughLedger_DoesNotDoubleCount()
        {
            var ledger = Create();
            await ledger.RecordAsync(UsageAccumulator.Apply("s", null, UsageSnapshot.Zero, new UsageSnapshot { PremiumRequests = 5 }));

            // App restart, session resumed; runtime metrics start at 5 and grow to 7 and then 8.
            var restarted = Create();
            var stored = restarted.Get("s");
            var baseline = new UsageSnapshot { PremiumRequests = 5 };
            await restarted.RecordAsync(UsageAccumulator.Apply("s", stored, baseline, new UsageSnapshot { PremiumRequests = 7 }));
            await restarted.RecordAsync(UsageAccumulator.Apply("s", stored, baseline, new UsageSnapshot { PremiumRequests = 8 }));

            Assert.Equal(8, Create().Get("s")!.PremiumRequests);
        }

        [Fact]
        public void Settings_ResolveUsageFile_UsesOverrideThenCopilotDirectory()
        {
            Assert.Equal(Path.GetFullPath("x.json"), new AutoPilotSettings { UsageFile = "x.json" }.ResolveUsageFile());
            Assert.Equal(
                Path.Combine(Path.GetFullPath(_directory), "usage", "session-costs.json"),
                new AutoPilotSettings { CopilotDirectory = _directory }.ResolveUsageFile());
            Assert.EndsWith(Path.Combine("Copilot", "usage", "session-costs.json"), new AutoPilotSettings().ResolveUsageFile());
        }

        private sealed class ListLogger : ILogger<UsageLedger>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
