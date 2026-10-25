using AutoPilot.Services;
using GitHub.Copilot;

namespace AutoPilot.Tests
{
    public class ChatTranscriptTests
    {
        private static ChatTranscript Create() => new() { OrchestratorName = "Boss" };

        private static SubagentStartedEvent Started(string toolCallId, string agentId, string display) => new()
        {
            AgentId = agentId,
            Data = new SubagentStartedData { ToolCallId = toolCallId, AgentName = "dev", AgentDisplayName = display, AgentDescription = "" },
        };

        [Fact]
        public void Replay_maps_user_messages_but_ignores_sub_agent_prompts()
        {
            var transcript = Create();
            transcript.Replay(
            [
                new UserMessageEvent { Data = new UserMessageData { Content = "hello" } },
                new UserMessageEvent { AgentId = "a1", Data = new UserMessageData { Content = "internal prompt" } },
            ]);

            var entry = Assert.Single(transcript.GetEntries());
            Assert.Equal(ChatEntryKind.User, entry.Kind);
            Assert.Equal("You", entry.Author);
            Assert.Equal("hello", entry.Text);
        }

        [Fact]
        public void Live_apply_does_not_duplicate_user_messages()
        {
            var transcript = Create();
            var changed = transcript.Apply(new UserMessageEvent { Data = new UserMessageData { Content = "hello" } });

            Assert.False(changed);
            Assert.Empty(transcript.GetEntries());
        }

        [Fact]
        public void Deltas_accumulate_and_final_message_replaces_text()
        {
            var transcript = Create();
            transcript.Apply(new AssistantMessageDeltaEvent { Data = new AssistantMessageDeltaData { MessageId = "m1", DeltaContent = "Hel" } });
            transcript.Apply(new AssistantMessageDeltaEvent { Data = new AssistantMessageDeltaData { MessageId = "m1", DeltaContent = "lo" } });
            Assert.Equal("Hello", Assert.Single(transcript.GetEntries()).Text);

            transcript.Apply(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m1", Content = "Hello!" } });
            var entry = Assert.Single(transcript.GetEntries());
            Assert.Equal("Hello!", entry.Text);
            Assert.Equal("Boss", entry.Author);
            Assert.False(entry.IsSubagent);
        }

        [Fact]
        public void Empty_assistant_message_creates_no_entry()
        {
            var transcript = Create();
            var changed = transcript.Apply(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m1", Content = "" } });

            Assert.False(changed);
            Assert.Empty(transcript.GetEntries());
        }

        [Fact]
        public void Sub_agent_messages_resolve_author_from_started_event()
        {
            var transcript = Create();
            transcript.Replay(
            [
                Started("tc1", "agent-1", "Developer"),
                new AssistantMessageEvent { AgentId = "agent-1", Data = new AssistantMessageData { MessageId = "m1", Content = "done" } },
                new AssistantMessageEvent { AgentId = "unknown", Data = new AssistantMessageData { MessageId = "m2", Content = "?" } },
            ]);

            var entries = transcript.GetEntries();
            Assert.Equal("Boss delegated to Developer", entries[0].Text);
            Assert.Equal(ChatEntryKind.Activity, entries[0].Kind);
            Assert.Equal("Developer", entries[1].Author);
            Assert.True(entries[1].IsSubagent);
            Assert.Equal("Sub-agent", entries[2].Author);
        }

        [Fact]
        public void Registered_subagent_names_are_used_for_authors()
        {
            var transcript = Create();
            transcript.RegisterSubagent("agent-9", "Tester");
            transcript.Apply(new AssistantMessageEvent { AgentId = "agent-9", Data = new AssistantMessageData { MessageId = "m", Content = "x" } });

            Assert.Equal("Tester", Assert.Single(transcript.GetEntries()).Author);
        }

        [Fact]
        public void Tool_calls_track_status_and_skip_task_tool()
        {
            var transcript = Create();
            transcript.Apply(new ToolExecutionStartEvent { Data = new ToolExecutionStartData { ToolCallId = "t1", ToolName = "task" } });
            transcript.Apply(new ToolExecutionStartEvent { Data = new ToolExecutionStartData { ToolCallId = "t2", ToolName = "view" } });
            var entry = Assert.Single(transcript.GetEntries());
            Assert.Null(entry.Succeeded);
            Assert.Equal("view", entry.Text);

            transcript.Apply(new ToolExecutionCompleteEvent { Data = new ToolExecutionCompleteData { ToolCallId = "t2", Success = false } });
            Assert.False(entry.Succeeded);
        }

        [Fact]
        public void Subagent_completion_failure_and_session_errors_become_entries()
        {
            var transcript = Create();
            transcript.Replay(
            [
                Started("tc1", "a1", "Developer"),
                new SubagentCompletedEvent { Data = new SubagentCompletedData { ToolCallId = "tc1", AgentName = "dev", AgentDisplayName = "Developer" } },
                new SubagentFailedEvent { Data = new SubagentFailedData { ToolCallId = "tc1", Error = "boom", AgentName = "dev", AgentDisplayName = "Developer" } },
                new SessionErrorEvent { Data = new SessionErrorData { Message = "bad", ErrorType = "test" } },
            ]);

            var entries = transcript.GetEntries();
            Assert.Equal("Developer finished", entries[1].Text);
            Assert.Equal(ChatEntryKind.Error, entries[2].Kind);
            Assert.Equal("Developer failed: boom", entries[2].Text);
            Assert.Equal(ChatEntryKind.Error, entries[3].Kind);
            Assert.Equal("bad", entries[3].Text);
        }

        [Fact]
        public void Subagent_completion_prefers_event_data_name_over_started_mapping()
        {
            var transcript = Create();
            transcript.Replay(
            [
                Started("tc1", "a1", "Developer"),
                new SubagentCompletedEvent { AgentId = "a1", Data = new SubagentCompletedData { ToolCallId = "tc1", AgentName = "coder", AgentDisplayName = "Code Wizard" } },
                new SubagentCompletedEvent { AgentId = "a1", Data = new SubagentCompletedData { ToolCallId = "other", AgentName = "coder", AgentDisplayName = "" } },
                new SubagentCompletedEvent { AgentId = "a1", Data = new SubagentCompletedData { ToolCallId = "other", AgentName = "", AgentDisplayName = "" } },
            ]);

            var entries = transcript.GetEntries();
            Assert.Equal("Code Wizard finished", entries[1].Text);
            Assert.Equal("coder finished", entries[2].Text);
            Assert.Equal("Developer finished", entries[3].Text);
        }

        [Fact]
        public void Clear_removes_entries_and_name_maps()
        {
            var transcript = Create();
            transcript.Replay([Started("tc1", "a1", "Developer")]);
            transcript.Clear();
            transcript.Apply(new AssistantMessageEvent { AgentId = "a1", Data = new AssistantMessageData { MessageId = "m", Content = "x" } });

            Assert.Equal("Sub-agent", Assert.Single(transcript.GetEntries()).Author);
        }
    }
}
