using System.Text.Json;
using AutoPilot.Services;
using GitHub.Copilot;

namespace AutoPilot.Tests
{
    public class ToolCallFormatterTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static ToolExecutionStartEvent Start(string id, string name, string? json, string? agentId = null) => new()
        {
            AgentId = agentId,
            Data = new ToolExecutionStartData { ToolCallId = id, ToolName = name, Arguments = json is null ? null : Json(json) },
        };

        [Theory]
        [InlineData("""{"path":"a/b.cs","pattern":"x"}""", "a/b.cs")]
        [InlineData("""{"pattern":"foo*","command":"ls"}""", "foo*")]
        [InlineData("""{"Command":"git status","description":"d"}""", "git status")]
        [InlineData("""{"other":1,"intent":"Look around"}""", "Look around")]
        [InlineData("""{"zeta":"first","alpha":"second"}""", "first")]
        [InlineData("""{"count":5}""", "5")]
        [InlineData("""{"command":"a\n  b\t\tc"}""", "a b c")]
        public void Summarize_picks_preferred_property(string json, string expected)
        {
            Assert.Equal(expected, ToolCallFormatter.Summarize(Json(json)));
        }

        [Fact]
        public void Summarize_returns_null_without_arguments()
        {
            Assert.Null(ToolCallFormatter.Summarize(null));
            Assert.Null(ToolCallFormatter.Summarize(Json("{}")));
            Assert.Null(ToolCallFormatter.FormatArguments(null));
            Assert.Null(ToolCallFormatter.FormatArguments(Json("{}")));
        }

        [Fact]
        public void Summarize_truncates_long_values_with_ellipsis()
        {
            var summary = ToolCallFormatter.Summarize(new Dictionary<string, object> { ["command"] = new string('x', 500) });

            Assert.NotNull(summary);
            Assert.Equal(ToolCallFormatter.MaxSummaryLength, summary.Length);
            Assert.EndsWith("…", summary);
        }

        [Fact]
        public void Summarize_handles_arbitrary_objects()
        {
            Assert.Equal("src", ToolCallFormatter.Summarize(new { Path = "src" }));
        }

        [Fact]
        public void FormatArguments_pretty_prints_json()
        {
            var text = ToolCallFormatter.FormatArguments(Json("""{"path":"a"}"""));

            Assert.Equal("{\n  \"path\": \"a\"\n}".ReplaceLineEndings(), text!.ReplaceLineEndings());
        }

        [Fact]
        public void CapResult_truncates_over_limit()
        {
            var capped = ToolCallFormatter.CapResult(new string('a', ToolCallFormatter.MaxResultLength + 10), out var truncated);

            Assert.True(truncated);
            Assert.Equal(ToolCallFormatter.MaxResultLength, capped!.Length);
            Assert.Null(ToolCallFormatter.CapResult("", out var emptyTruncated));
            Assert.False(emptyTruncated);
        }

        [Fact]
        public void FormatArguments_caps_huge_arguments_and_flags_truncation()
        {
            var text = ToolCallFormatter.FormatArguments(Json("{\"content\":\"" + new string('x', 200_000) + "\"}"), out var truncated);

            Assert.True(truncated);
            Assert.Equal(ToolCallFormatter.MaxArgumentsLength, text!.Length);
            Assert.False(ToolCallFormatter.FormatArguments(Json("""{"path":"a"}"""), out var small) is null);
            Assert.False(small);
        }

        [Fact]
        public void CapError_caps_long_messages_with_ellipsis()
        {
            var capped = ToolCallFormatter.CapError(new string('e', ToolCallFormatter.MaxResultLength + 50));

            Assert.Equal(ToolCallFormatter.MaxResultLength + 1, capped!.Length);
            Assert.EndsWith("…", capped);
            Assert.Equal("boom", ToolCallFormatter.CapError("boom"));
            Assert.Null(ToolCallFormatter.CapError(null));
        }

        [Fact]
        public void Summarize_bounds_huge_first_property()
        {
            var summary = ToolCallFormatter.Summarize(Json("{\"a\":[\"" + new string('x', 500_000) + "\"]}"));

            Assert.Equal(ToolCallFormatter.MaxSummaryLength, summary!.Length);
        }

        [Fact]
        public void FormatArguments_does_not_over_escape_characters()
        {
            var text = ToolCallFormatter.FormatArguments(Json("""{"t":"é<b>&'"}"""));

            Assert.Contains("é<b>&'", text);
            Assert.DoesNotContain("\\u", text);
        }

        [Fact]
        public void Truncation_does_not_split_surrogate_pairs()
        {
            var text = new string('a', ToolCallFormatter.MaxResultLength - 1) + "😀tail";

            var capped = ToolCallFormatter.CapResult(text, out var truncated);

            Assert.True(truncated);
            Assert.Equal(ToolCallFormatter.MaxResultLength - 1, capped!.Length);
            var truncatedText = ToolCallFormatter.Truncate(new string('a', 9) + "😀tail", 11);
            Assert.Equal(new string('a', 9) + "…", truncatedText);
        }

        [Fact]
        public void Transcript_captures_summary_arguments_and_result()
        {
            var transcript = new ChatTranscript();
            transcript.Apply(Start("t1", "view", """{"path":"a.cs"}"""));
            transcript.Apply(new ToolExecutionCompleteEvent
            {
                Data = new ToolExecutionCompleteData
                {
                    ToolCallId = "t1",
                    Success = true,
                    Result = new ToolExecutionCompleteResult { Content = "short", DetailedContent = "detailed" },
                },
            });

            var entry = Assert.Single(transcript.GetEntries());
            Assert.Equal("view", entry.Text);
            Assert.Equal("a.cs", entry.ToolSummary);
            Assert.Contains("\"path\": \"a.cs\"", entry.ToolArguments);
            Assert.Equal("detailed", entry.ToolResult);
            Assert.False(entry.ToolResultTruncated);
            Assert.Null(entry.ToolError);
        }

        [Fact]
        public void Transcript_falls_back_to_content_and_caps_result()
        {
            var transcript = new ChatTranscript();
            transcript.Apply(Start("t1", "view", null));
            transcript.Apply(new ToolExecutionCompleteEvent
            {
                Data = new ToolExecutionCompleteData
                {
                    ToolCallId = "t1",
                    Success = true,
                    Result = new ToolExecutionCompleteResult { Content = new string('z', 5000) },
                },
            });

            var entry = Assert.Single(transcript.GetEntries());
            Assert.Null(entry.ToolSummary);
            Assert.Null(entry.ToolArguments);
            Assert.Equal(ToolCallFormatter.MaxResultLength, entry.ToolResult!.Length);
            Assert.True(entry.ToolResultTruncated);
        }

        [Fact]
        public void Transcript_captures_error_on_failure()
        {
            var transcript = new ChatTranscript();
            transcript.Apply(Start("t1", "bash", """{"command":"boom"}"""));
            transcript.Apply(new ToolExecutionCompleteEvent
            {
                Data = new ToolExecutionCompleteData
                {
                    ToolCallId = "t1",
                    Success = false,
                    Error = new ToolExecutionCompleteError { Message = "it broke" },
                },
            });

            var entry = Assert.Single(transcript.GetEntries());
            Assert.False(entry.Succeeded);
            Assert.Equal("it broke", entry.ToolError);
            Assert.Null(entry.ToolResult);
        }

        [Fact]
        public void Task_tool_remains_hidden()
        {
            var transcript = new ChatTranscript();
            Assert.False(transcript.Apply(Start("t1", ChatTranscript.TaskToolName, """{"prompt":"x"}""")));
            Assert.Empty(transcript.GetEntries());
        }

        [Fact]
        public void Replay_and_live_produce_identical_tool_info()
        {
            SessionEvent[] events =
            [
                Start("t1", "grep", """{"pattern":"foo","path":"src"}"""),
                new ToolExecutionCompleteEvent
                {
                    Data = new ToolExecutionCompleteData
                    {
                        ToolCallId = "t1",
                        Success = true,
                        Result = new ToolExecutionCompleteResult { Content = "hit" },
                    },
                },
            ];

            var live = new ChatTranscript();
            foreach (var e in events)
            {
                live.Apply(e);
            }

            var replayed = new ChatTranscript();
            replayed.Replay(events);

            var a = Assert.Single(live.GetEntries());
            var b = Assert.Single(replayed.GetEntries());
            Assert.Equal(a.ToolSummary, b.ToolSummary);
            Assert.Equal(a.ToolArguments, b.ToolArguments);
            Assert.Equal(a.ToolResult, b.ToolResult);
            Assert.Equal(a.ToolError, b.ToolError);
            Assert.Equal(a.Succeeded, b.Succeeded);
            Assert.Equal("src", a.ToolSummary);
        }
    }
}
