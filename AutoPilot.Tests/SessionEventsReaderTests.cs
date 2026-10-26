using System.Text;
using AutoPilot.Services;

namespace AutoPilot.Tests
{
    /// <summary>Builds temporary <c>session-state</c> folders with <c>events.jsonl</c> files for tests.</summary>
    internal sealed class SessionStateFixture : IDisposable
    {
        public SessionStateFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "autopilot-sessions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public static string Start(string id, string cwd, string time = "2026-10-25T18:09:44.791Z") =>
            """{"type":"session.start","data":{"sessionId":"@@ID@@","startTime":"@@TIME@@","context":{"cwd":@@CWD@@}},"id":"1","timestamp":"@@TIME@@","parentId":null}""".Replace("@@ID@@", id).Replace("@@TIME@@", time).Replace("@@CWD@@", System.Text.Json.JsonSerializer.Serialize(cwd));

        public static string User(string content) =>
            """{"type":"user.message","data":{"content":""" + System.Text.Json.JsonSerializer.Serialize(content) + """},"id":"2","timestamp":"2026-10-25T18:09:50.000Z","parentId":"1"}""";

        public static string Shutdown(double nano, string time, long input = 100, long output = 50, string subAgent = "implementer") =>
            """
            {"type":"session.shutdown","data":{"shutdownType":"routine","totalNanoAiu":@@NANO@@,
            "modelMetrics":{"gpt":{"requests":{"count":14,"cost":15},"usage":{"inputTokens":@@IN@@,"outputTokens":@@OUT@@,"cacheReadTokens":3,"cacheWriteTokens":2,"reasoningTokens":1},"totalNanoAiu":@@NANO@@}},
            "agentMetrics":{"main":{"totalNanoAiu":1000000000,"modelMetrics":{"gpt":{"requests":{"count":4,"cost":4},"usage":{"inputTokens":10,"outputTokens":5,"cacheReadTokens":0,"cacheWriteTokens":0,"reasoningTokens":0},"totalNanoAiu":1000000000}}},
            "a1":{"agentName":"@@SUB@@","agentDisplayName":"Implementer","totalNanoAiu":2000000000,"modelMetrics":{"gpt":{"requests":{"count":2,"cost":2},"usage":{"inputTokens":20,"outputTokens":6,"cacheReadTokens":0,"cacheWriteTokens":0,"reasoningTokens":0},"totalNanoAiu":2000000000}}},
            "a2":{"agentName":"@@SUB@@","agentDisplayName":"Implementer","totalNanoAiu":3000000000,"modelMetrics":{"gpt":{"requests":{"count":3,"cost":3},"usage":{"inputTokens":30,"outputTokens":7,"cacheReadTokens":0,"cacheWriteTokens":0,"reasoningTokens":0},"totalNanoAiu":3000000000}}}}},"id":"9","timestamp":"@@TIME@@"}
            """.ReplaceLineEndings("").Replace("@@NANO@@", nano.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Replace("@@IN@@", input.ToString()).Replace("@@OUT@@", output.ToString()).Replace("@@SUB@@", subAgent).Replace("@@TIME@@", time);

        /// <summary>Writes a session folder and returns the events file path.</summary>
        public string Write(string id, params string[] lines)
        {
            var directory = Path.Combine(Root, id);
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "events.jsonl");
            File.WriteAllText(file, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    public sealed class SessionEventsReaderTests : IDisposable
    {
        private readonly SessionStateFixture _fixture = new();

        public void Dispose() => _fixture.Dispose();

        [Fact]
        public void LastShutdownWins()
        {
            var file = _fixture.Write("s1",
                SessionStateFixture.Start("s1", @"D:\repo"),
                SessionStateFixture.Shutdown(6_000_000_000, "2026-10-25T18:20:00.000Z", input: 60),
                SessionStateFixture.Shutdown(9_000_000_000, "2026-10-25T19:00:00.000Z", input: 90));

            var cost = SessionEventsReader.TryReadCost(file, "Orchestrator");

            Assert.NotNull(cost);
            Assert.Equal(9, cost.AiCredits, 6);
            Assert.Equal(90, cost.InputTokens);
            Assert.Equal(DateTimeOffset.Parse("2026-10-25T19:00:00Z"), cost.LastUpdated);
        }

        [Fact]
        public void NoShutdown_ReturnsNull()
        {
            var file = _fixture.Write("s1", SessionStateFixture.Start("s1", @"D:\repo"), SessionStateFixture.User("hi"));

            Assert.Null(SessionEventsReader.TryReadCost(file, "Orchestrator"));
            Assert.Equal(@"D:\repo", SessionEventsReader.TryReadSession(file, "Orchestrator")!.WorkingDirectory);
        }

        [Fact]
        public void ZeroUsage_ReturnsNull()
        {
            var file = _fixture.Write("s1",
                SessionStateFixture.Start("s1", @"D:\repo"),
                """{"type":"session.shutdown","data":{"totalNanoAiu":0,"modelMetrics":{},"agentMetrics":{}},"id":"3","timestamp":"2026-10-25T18:20:00.000Z"}""");

            Assert.Null(SessionEventsReader.TryReadCost(file, "Orchestrator"));
        }

        [Fact]
        public void MalformedLines_AreSkipped()
        {
            var file = _fixture.Write("s1",
                "not json at all",
                SessionStateFixture.Start("s1", @"D:\repo"),
                """{"type":"session.shutdown","data":{ broken""",
                "",
                SessionStateFixture.Shutdown(5_000_000_000, "2026-10-25T18:20:00.000Z"),
                "{\"type\":\"session.shutdown\",\"data\":");

            var cost = SessionEventsReader.TryReadCost(file, "Orchestrator");

            Assert.NotNull(cost);
            Assert.Equal(5, cost.AiCredits, 6);
        }

        [Fact]
        public void MalformedLines_AreCounted()
        {
            var file = _fixture.Write("s1",
                "not json at all",
                SessionStateFixture.Start("s1", @"D:\repo"),
                """{"type":"session.shutdown","data":{ broken""",
                "",
                "{ not json",
                SessionStateFixture.Shutdown(5_000_000_000, "2026-10-25T18:20:00.000Z"));

            Assert.Equal(3, SessionEventsReader.TryReadSession(file, "O")!.MalformedLines);
        }

        [Fact]
        public void MainAgent_UsesOrchestratorName_AndSubAgentsAreAggregatedByName()
        {
            var file = _fixture.Write("s1", SessionStateFixture.Start("s1", @"D:\repo"), SessionStateFixture.Shutdown(6_000_000_000, "2026-10-25T18:20:00.000Z"));

            var cost = SessionEventsReader.TryReadCost(file, "Orchestrator")!;

            Assert.Equal(["Orchestrator", "implementer"], cost.Agents.Keys.Order(StringComparer.Ordinal));
            var main = cost.Agents["Orchestrator"];
            Assert.Equal("Orchestrator", main.DisplayName);
            Assert.Equal(1, main.AiCredits, 6);
            Assert.Equal(4, main.Requests);
            var sub = cost.Agents["implementer"];
            Assert.Equal("Implementer", sub.DisplayName);
            Assert.Equal(5, sub.AiCredits, 6);
            Assert.Equal(5, sub.Requests);
            Assert.Equal(50, sub.InputTokens);
            Assert.Equal(13, sub.OutputTokens);
        }

        [Fact]
        public void Title_FirstUserMessage_StripsDateTimePrefixAndTruncates()
        {
            var file = _fixture.Write("s1",
                SessionStateFixture.Start("s1", @"D:\repo"),
                SessionStateFixture.User("<current_datetime>2026-10-25T19:00:00+01:00</current_datetime>\n\n" + new string('x', 300)),
                SessionStateFixture.User("second"),
                SessionStateFixture.Shutdown(1_000_000_000, "2026-10-25T18:20:00.000Z"));

            var cost = SessionEventsReader.TryReadCost(file, "O")!;

            Assert.Equal(new string('x', 100), cost.Title);
        }

        [Fact]
        public void FirstSeenAndLastUpdated_AreStartAndShutdownTimes()
        {
            var file = _fixture.Write("s1",
                SessionStateFixture.Start("s1", @"D:\repo", "2026-10-25T18:09:44.791Z"),
                SessionStateFixture.Shutdown(1_000_000_000, "2026-10-25T18:25:29.052Z"));

            var cost = SessionEventsReader.TryReadCost(file, "O")!;

            Assert.Equal("s1", cost.SessionId);
            Assert.Equal(DateTimeOffset.Parse("2026-10-25T18:09:44.791Z"), cost.FirstSeen);
            Assert.Equal(DateTimeOffset.Parse("2026-10-25T18:25:29.052Z"), cost.LastUpdated);
        }

        [Fact]
        public void FileOpenForWriting_IsStillReadable()
        {
            var file = _fixture.Write("s1", SessionStateFixture.Start("s1", @"D:\repo"), SessionStateFixture.Shutdown(2_000_000_000, "2026-10-25T18:20:00.000Z"));

            using var writer = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            var cost = SessionEventsReader.TryReadCost(file, "O");

            Assert.NotNull(cost);
            Assert.Equal(2, cost.AiCredits, 6);
        }

        [Fact]
        public void Reader_DoesNotModifyFile()
        {
            var file = _fixture.Write("s1", SessionStateFixture.Start("s1", @"D:\repo"), SessionStateFixture.Shutdown(2_000_000_000, "2026-10-25T18:20:00.000Z"));
            var before = File.ReadAllBytes(file);

            SessionEventsReader.TryReadSession(file, "O");

            Assert.Equal(before, File.ReadAllBytes(file));
        }
    }
}
