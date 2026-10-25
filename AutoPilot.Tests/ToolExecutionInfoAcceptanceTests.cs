using System.Text.Json;
using AutoPilot.Services;
using GitHub.Copilot;

namespace AutoPilot.Tests
{
    public class ToolExecutionInfoAcceptanceTests
    {
        private static ChatTranscript New() => new() { OrchestratorName = "Boss" };

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

        private static ToolExecutionStartEvent Start(string id, string name, JsonElement? args, string? agentId = null) =>
            new() { AgentId = agentId, Data = new ToolExecutionStartData { ToolCallId = id, ToolName = name, Arguments = args } };

        private static ToolExecutionCompleteEvent Done(string id, string? content = null, string? detailed = null, string? agentId = null) =>
            new()
            {
                AgentId = agentId,
                Data = new ToolExecutionCompleteData
                {
                    ToolCallId = id,
                    Success = true,
                    Result = new ToolExecutionCompleteResult { Content = content!, DetailedContent = detailed }
                }
            };

        private static ToolExecutionCompleteEvent Fail(string id, string message) =>
            new()
            {
                Data = new ToolExecutionCompleteData
                {
                    ToolCallId = id,
                    Success = false,
                    Error = new ToolExecutionCompleteError { Message = message }
                }
            };

        private static ChatEntry OnlyTool(ChatTranscript t) =>
            Assert.Single(t.GetEntries(), e => e.Kind == ChatEntryKind.Tool);

        private static ChatEntry Run(JsonElement? args, string name = "view")
        {
            var t = New();
            t.Apply(Start("t1", name, args));
            return OnlyTool(t);
        }

        // ---------- AC-1 ----------
        [Fact]
        public void ac1_constants_match_contract()
        {
            Assert.Equal(120, ToolCallFormatter.MaxSummaryLength);
            Assert.Equal(4000, ToolCallFormatter.MaxResultLength);
        }

        [Theory]
        [InlineData("view", "{\"path\":\"src/a.txt\"}", "src/a.txt")]
        [InlineData("grep", "{\"pattern\":\"foo.*bar\"}", "foo.*bar")]
        [InlineData("glob", "{\"pattern\":\"**/*.cs\"}", "**/*.cs")]
        [InlineData("powershell", "{\"command\":\"dotnet build\"}", "dotnet build")]
        [InlineData("powershell", "{\"command\":\"dotnet build\",\"description\":\"Build it\"}", "dotnet build")]
        public void ac1_summary_contains_key_argument(string tool, string args, string expected)
        {
            var e = Run(Json(args), tool);
            Assert.NotNull(e.ToolSummary);
            Assert.Contains(expected, e.ToolSummary);
            Assert.DoesNotContain("\n", e.ToolSummary);
        }

        [Fact]
        public void ac1_intent_is_used_when_present()
        {
            var e = Run(Json("{\"intent\":\"Exploring repo\"}"), "report_intent");
            Assert.Contains("Exploring repo", e.ToolSummary);
        }

        [Theory]
        [InlineData("{\"intent\":\"Plan it\",\"description\":\"Desc\",\"foo\":\"bar\"}", "Plan it")]
        [InlineData("{\"foo\":\"bar\",\"description\":\"Desc\"}", "Desc")]
        public void ac1_intent_then_description_used_when_no_concrete_keys(string args, string expected)
        {
            var e = Run(Json(args), "custom");
            Assert.NotNull(e.ToolSummary);
            Assert.Contains(expected, e.ToolSummary);
            Assert.DoesNotContain("bar", e.ToolSummary);
        }

        [Fact]
        public void ac1_path_wins_over_intent()
        {
            var e = Run(Json("{\"intent\":\"x\",\"path\":\"a.txt\"}"), "view");
            Assert.Equal("a.txt", e.ToolSummary);
            Assert.Equal("a.txt", ToolCallFormatter.Summarize(Json("{\"intent\":\"x\",\"path\":\"a.txt\"}")));
        }

        [Fact]
        public void ac1_fallback_renders_first_argument_compactly()
        {
            var e = Run(Json("{\"foo\":\"bar\"}"), "custom");
            Assert.False(string.IsNullOrWhiteSpace(e.ToolSummary));
            Assert.Contains("bar", e.ToolSummary);
        }

        [Fact]
        public void ac1_long_summary_is_truncated_with_ellipsis()
        {
            var e = Run(Json("{\"path\":\"" + new string('x', 500) + "\"}"));
            Assert.NotNull(e.ToolSummary);
            Assert.True(e.ToolSummary!.Length <= ToolCallFormatter.MaxSummaryLength + 5, $"len={e.ToolSummary.Length}");
            Assert.True(e.ToolSummary.Length < 500);
            Assert.EndsWith("…", e.ToolSummary);
        }

        [Fact]
        public void ac1_short_summary_not_truncated()
        {
            var e = Run(Json("{\"path\":\"short.txt\"}"));
            Assert.DoesNotContain("…", e.ToolSummary);
        }

        [Fact]
        public void ac1_multiline_command_is_one_line()
        {
            var e = Run(Json("{\"command\":\"echo a\\necho b\\r\\necho c\"}"), "powershell");
            Assert.NotNull(e.ToolSummary);
            Assert.DoesNotContain("\n", e.ToolSummary);
            Assert.DoesNotContain("\r", e.ToolSummary);
        }

        [Fact]
        public void ac1_null_arguments_have_no_summary_or_arguments()
        {
            var e = Run(null);
            Assert.Null(e.ToolSummary);
            Assert.True(string.IsNullOrEmpty(e.ToolArguments));
            Assert.Null(ToolCallFormatter.Summarize(null));
        }

        [Fact]
        public void ac1_empty_object_arguments_have_no_summary()
        {
            var e = Run(Json("{}"));
            Assert.True(string.IsNullOrEmpty(e.ToolSummary));
        }

        [Fact]
        public void ac1_truncate_helper_behaviour()
        {
            Assert.Equal("abc", ToolCallFormatter.Truncate("abc", 3));
            var r = ToolCallFormatter.Truncate("abcdef", 3);
            Assert.EndsWith("…", r);
            Assert.True(r.Length <= 4);
            Assert.StartsWith("ab", r);
        }

        // ---------- AC-2 ----------
        [Fact]
        public void ac2_arguments_are_pretty_printed_json()
        {
            var e = Run(Json("{\"path\":\"a.txt\",\"n\":2}"));
            Assert.NotNull(e.ToolArguments);
            Assert.Contains("\n", e.ToolArguments);
            using var doc = JsonDocument.Parse(e.ToolArguments!);
            Assert.Equal("a.txt", doc.RootElement.GetProperty("path").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("n").GetInt32());
        }

        [Fact]
        public void ac2_format_arguments_null_is_null_or_empty()
        {
            Assert.True(string.IsNullOrEmpty(ToolCallFormatter.FormatArguments(null)));
        }

        [Fact]
        public void ac2_razor_markup_uses_native_details_collapsed_by_default()
        {
            var razor = File.ReadAllText(FindRazor());
            var idx = razor.IndexOf("case ChatEntryKind.Tool:", StringComparison.Ordinal);
            Assert.True(idx >= 0);
            var block = razor.Substring(idx, razor.IndexOf("case ChatEntryKind.Activity:", idx, StringComparison.Ordinal) - idx);
            Assert.Contains("<details>", block); // no "open" attribute
            Assert.DoesNotMatch(@"<details[^>]*\bopen\b", block);
            Assert.Contains("<summary>", block);
            Assert.Contains("ToolArguments", block);
        }

        // ---------- AC-3 ----------
        [Fact]
        public void ac3_success_shows_detailed_content_over_content()
        {
            var t = New();
            t.Apply(Start("t1", "view", Json("{\"path\":\"a\"}")));
            t.Apply(Done("t1", "short", "detailed text"));
            var e = OnlyTool(t);
            Assert.True(e.Succeeded);
            Assert.Equal("detailed text", e.ToolResult);
            Assert.False(e.ToolResultTruncated);
            Assert.Null(e.ToolError);
        }

        [Fact]
        public void ac3_success_falls_back_to_content()
        {
            var t = New();
            t.Apply(Start("t1", "view", Json("{\"path\":\"a\"}")));
            t.Apply(Done("t1", "only content", null));
            Assert.Equal("only content", OnlyTool(t).ToolResult);
        }

        [Fact]
        public void ac3_success_without_result_text_has_no_result()
        {
            var t = New();
            t.Apply(Start("t1", "view", null));
            t.Apply(new ToolExecutionCompleteEvent { Data = new ToolExecutionCompleteData { ToolCallId = "t1", Success = true } });
            var e = OnlyTool(t);
            Assert.True(e.Succeeded);
            Assert.True(string.IsNullOrEmpty(e.ToolResult));
            Assert.False(e.ToolResultTruncated);
        }

        [Fact]
        public void ac3_long_result_is_capped_and_flagged()
        {
            var t = New();
            t.Apply(Start("t1", "view", null));
            t.Apply(Done("t1", "c", new string('r', 10000)));
            var e = OnlyTool(t);
            Assert.True(e.ToolResultTruncated);
            Assert.NotNull(e.ToolResult);
            Assert.True(e.ToolResult!.Length <= ToolCallFormatter.MaxResultLength, $"len={e.ToolResult.Length}");
            Assert.True(e.ToolResult.Length >= ToolCallFormatter.MaxResultLength - 10);
        }

        [Fact]
        public void ac3_capresult_boundaries()
        {
            var exact = new string('a', ToolCallFormatter.MaxResultLength);
            Assert.Equal(exact, ToolCallFormatter.CapResult(exact, out var t1));
            Assert.False(t1);

            var over = new string('a', ToolCallFormatter.MaxResultLength + 1);
            var capped = ToolCallFormatter.CapResult(over, out var t2);
            Assert.True(t2);
            Assert.True(capped!.Length <= ToolCallFormatter.MaxResultLength);

            Assert.True(string.IsNullOrEmpty(ToolCallFormatter.CapResult(null, out var t3)));
            Assert.False(t3);
            Assert.True(string.IsNullOrEmpty(ToolCallFormatter.CapResult("", out var t4)));
            Assert.False(t4);
        }

        [Fact]
        public void ac3_razor_shows_truncation_notice()
        {
            var razor = File.ReadAllText(FindRazor());
            Assert.Contains("entry.ToolResultTruncated", razor);
            Assert.Contains("truncated", razor, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- AC-4 ----------
        [Fact]
        public void ac4_failure_captures_error_and_keeps_failed_status()
        {
            var t = New();
            t.Apply(Start("t1", "view", Json("{\"path\":\"nope\"}")));
            t.Apply(Fail("t1", "File not found: nope"));
            var e = OnlyTool(t);
            Assert.False(e.Succeeded);
            Assert.Equal("File not found: nope", e.ToolError);
        }

        [Fact]
        public void ac4_failure_without_error_object_does_not_throw()
        {
            var t = New();
            t.Apply(Start("t1", "view", null));
            t.Apply(new ToolExecutionCompleteEvent { Data = new ToolExecutionCompleteData { ToolCallId = "t1", Success = false } });
            Assert.False(OnlyTool(t).Succeeded);
        }

        [Fact]
        public void ac4_razor_renders_error_section()
        {
            var razor = File.ReadAllText(FindRazor());
            Assert.Contains("entry.ToolError", razor);
        }

        // ---------- AC-5 ----------
        private static List<SessionEvent> Script() =>
        [
            Start("t1", "view", Json("{\"path\":\"a.txt\"}")),
            Done("t1", "c1", "d1"),
            Start("t2", "powershell", Json("{\"command\":\"ls\"}"), "agent-x"),
            Fail("t2", "boom"),
            Start("t3", "grep", Json("{\"pattern\":\"" + new string('p', 300) + "\"}")),
            Done("t3", new string('z', 9000)),
            Start("t4", "noargs", null),
        ];

        private static object Project(ChatEntry e) =>
            (e.Kind, e.Text, e.Key, e.Author, e.IsSubagent, e.Succeeded, e.ToolSummary, e.ToolArguments, e.ToolResult, e.ToolResultTruncated, e.ToolError);

        [Fact]
        public void ac5_live_and_replay_produce_identical_tool_info()
        {
            var live = New();
            foreach (var e in Script()) live.Apply(e);
            var replay = New();
            replay.Replay(Script());

            var a = live.GetEntries().Where(x => x.Kind == ChatEntryKind.Tool).Select(Project).ToList();
            var b = replay.GetEntries().Where(x => x.Kind == ChatEntryKind.Tool).Select(Project).ToList();
            Assert.Equal(4, a.Count);
            Assert.Equal(a, b);
        }

        [Fact]
        public void ac5_apply_with_replay_flag_matches_live()
        {
            var live = New();
            var rep = New();
            foreach (var e in Script()) { live.Apply(e); rep.Apply(e, replay: true); }
            Assert.Equal(
                live.GetEntries().Where(x => x.Kind == ChatEntryKind.Tool).Select(Project).ToList(),
                rep.GetEntries().Where(x => x.Kind == ChatEntryKind.Tool).Select(Project).ToList());
        }

        // ---------- AC-6 ----------
        [Fact]
        public void ac6_task_tool_remains_hidden()
        {
            var t = New();
            t.Apply(Start("tt", ChatTranscript.TaskToolName, Json("{\"prompt\":\"x\"}")));
            t.Apply(Done("tt", "r"));
            Assert.DoesNotContain(t.GetEntries(), e => e.Kind == ChatEntryKind.Tool);
        }

        [Fact]
        public void ac6_status_transitions_and_author()
        {
            var t = New();
            t.Apply(Start("t1", "view", Json("{\"path\":\"a\"}")));
            var e = OnlyTool(t);
            Assert.Null(e.Succeeded);
            Assert.Equal("view", e.Text);
            Assert.Equal("t1", e.Key);
            Assert.Equal("Boss", e.Author);
            Assert.False(e.IsSubagent);
            t.Apply(Done("t1", "ok"));
            Assert.True(OnlyTool(t).Succeeded);

            var t2 = New();
            t2.Apply(Start("t1", "view", null));
            t2.Apply(Fail("t1", "bad"));
            Assert.False(OnlyTool(t2).Succeeded);
        }

        [Fact]
        public void ac6_complete_for_unknown_id_does_not_throw_or_add_entry()
        {
            var t = New();
            t.Apply(Done("missing", "x"));
            Assert.DoesNotContain(t.GetEntries(), e => e.Kind == ChatEntryKind.Tool && e.Key == "missing" && e.Succeeded == null);
        }

        // ---------- AC-7 ----------
        [Fact]
        public void ac7_markup_in_args_and_results_is_kept_raw_in_data_and_razor_encodes()
        {
            var t = New();
            t.Apply(Start("t1", "view", Json("{\"path\":\"<script>alert(1)</script>\"}")));
            t.Apply(Done("t1", "<img src=x onerror=1>"));
            var e = OnlyTool(t);
            // Data layer must not pre-encode (would double-encode) nor produce markup of its own.
            Assert.Contains("<script>", e.ToolSummary);
            Assert.Equal("<img src=x onerror=1>", e.ToolResult);

            var razor = File.ReadAllText(FindRazor());
            var idx = razor.IndexOf("case ChatEntryKind.Tool:", StringComparison.Ordinal);
            var block = razor.Substring(idx, razor.IndexOf("case ChatEntryKind.Activity:", idx, StringComparison.Ordinal) - idx);
            Assert.DoesNotContain("MarkupString", block);
            Assert.DoesNotContain("Html.Raw", block);
            Assert.Contains("@entry.ToolSummary", block);
            Assert.Contains("@entry.ToolArguments", block);
            Assert.Contains("@entry.ToolResult", block);
            Assert.Contains("@entry.ToolError", block);
        }

        private static string FindRazor()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var p = Path.Combine(dir.FullName, "AutoPilot", "Components", "Chat", "ChatBot.razor");
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("ChatBot.razor");
        }
    }
}
