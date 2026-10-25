using GitHub.Copilot;

namespace AutoPilot.Services
{
    /// <summary>
    /// Ordered chat entries plus the sub-agent name maps, built from Copilot session events. Shared by live
    /// streaming and history replay so both render identically. Thread-safe: events arrive on SDK threads.
    /// </summary>
    public sealed class ChatTranscript
    {
        public const string TaskToolName = "task";
        public const string UnknownSubagentName = "Sub-agent";

        private readonly Lock _gate = new();
        private readonly List<ChatEntry> _entries = [];
        private readonly Dictionary<string, string> _subagentByToolCallId = [];
        private readonly Dictionary<string, string> _subagentByAgentId = [];
        private string _orchestratorName = "Orchestrator";
        private bool _contentFilterPending;

        public string OrchestratorName
        {
            get { lock (_gate) { return _orchestratorName; } }
            set { lock (_gate) { _orchestratorName = value; } }
        }

        public IReadOnlyList<ChatEntry> GetEntries()
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }

        public void Add(ChatEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _subagentByToolCallId.Clear();
                _subagentByAgentId.Clear();
                _contentFilterPending = false;
            }
        }

        /// <summary>Records the display name of a sub-agent instance (from the sub-agent start hook).</summary>
        public void RegisterSubagent(string agentId, string name)
        {
            lock (_gate)
            {
                _subagentByAgentId[agentId] = name;
            }
        }

        /// <summary>Replays persisted events. User messages are included since they are not added by the UI.</summary>
        public void Replay(IEnumerable<SessionEvent> events)
        {
            foreach (var sessionEvent in events)
            {
                Apply(sessionEvent, replay: true);
            }
        }

        /// <summary>
        /// Applies one event to the transcript. Returns true when the transcript changed.
        /// With <paramref name="replay"/>, user messages become entries (live, the UI adds them itself).
        /// </summary>
        public bool Apply(SessionEvent sessionEvent, bool replay = false)
        {
            lock (_gate)
            {
                switch (sessionEvent)
                {
                    case UserMessageEvent user when replay:
                        if (user.AgentId is not null || string.IsNullOrEmpty(user.Data.Content))
                        {
                            return false;
                        }

                        _entries.Add(new ChatEntry { Kind = ChatEntryKind.User, Author = "You", Text = user.Data.Content });
                        return true;

                    case AssistantMessageDeltaEvent delta:
                        return UpsertMessage(delta.Data.MessageId, delta.AgentId, delta.Data.DeltaContent, append: true);

                    case AssistantMessageEvent message:
                        return UpsertMessage(message.Data.MessageId, message.AgentId, message.Data.Content, append: false);

                    case ToolExecutionStartEvent toolStart when toolStart.Data.ToolName != TaskToolName:
                        _entries.Add(new ChatEntry
                        {
                            Kind = ChatEntryKind.Tool,
                            Key = toolStart.Data.ToolCallId,
                            Author = ResolveAgentUnlocked(toolStart.AgentId),
                            IsSubagent = toolStart.AgentId is not null,
                            Text = toolStart.Data.ToolName,
                            ToolSummary = ToolCallFormatter.Summarize(toolStart.Data.Arguments),
                            ToolArguments = ToolCallFormatter.FormatArguments(toolStart.Data.Arguments, out var argsTruncated),
                            ToolArgumentsTruncated = argsTruncated,
                        });
                        return true;

                    case ToolExecutionCompleteEvent toolComplete:
                        var tool = _entries.FindLast(e => e.Kind == ChatEntryKind.Tool && e.Key == toolComplete.Data.ToolCallId);
                        if (tool is null)
                        {
                            return false;
                        }

                        tool.Succeeded = toolComplete.Data.Success;
                        if (toolComplete.Data.Success)
                        {
                            var result = toolComplete.Data.Result;
                            var text = !string.IsNullOrEmpty(result?.DetailedContent) ? result.DetailedContent : result?.Content;
                            tool.ToolResult = ToolCallFormatter.CapResult(text, out var truncated);
                            tool.ToolResultTruncated = truncated;
                        }
                        else
                        {
                            tool.ToolError = ToolCallFormatter.CapError(toolComplete.Data.Error?.Message);
                        }

                        return true;

                    case SubagentStartedEvent started:
                        var agentName = string.IsNullOrEmpty(started.Data.AgentDisplayName) ? started.Data.AgentName : started.Data.AgentDisplayName;
                        _subagentByToolCallId[started.Data.ToolCallId] = agentName;
                        if (started.AgentId is not null)
                        {
                            _subagentByAgentId[started.AgentId] = agentName;
                        }

                        _entries.Add(new ChatEntry { Kind = ChatEntryKind.Activity, Author = _orchestratorName, Text = $"{_orchestratorName} delegated to {agentName}" });
                        return true;

                    case SubagentCompletedEvent completed:
                        _entries.Add(new ChatEntry { Kind = ChatEntryKind.Activity, Author = _orchestratorName, Text = $"{ResolveSubagentNameUnlocked(completed.Data.AgentDisplayName, completed.Data.AgentName, completed.Data.ToolCallId, completed.AgentId)} finished" });
                        return true;

                    case SubagentFailedEvent failed:
                        var failedName = ResolveSubagentNameUnlocked(failed.Data.AgentDisplayName, failed.Data.AgentName, failed.Data.ToolCallId, failed.AgentId);
                        _entries.Add(new ChatEntry { Kind = ChatEntryKind.Error, Author = failedName, Text = $"{failedName} failed: {failed.Data.Error}" });
                        return true;

                    case AssistantUsageEvent usage:
                        if (usage.Data.ContentFilterTriggered != true && usage.Data.FinishReason != "content_filter")
                        {
                            _contentFilterPending = false;
                            return false;
                        }

                        _contentFilterPending = true;
                        var blockedAuthor = ResolveAgentUnlocked(usage.AgentId);
                        var requestId = string.IsNullOrEmpty(usage.Data.ServiceRequestId) ? "" : $" (request ID: {usage.Data.ServiceRequestId})";
                        _entries.Add(new ChatEntry
                        {
                            Kind = ChatEntryKind.Error,
                            Author = blockedAuthor,
                            Text = $"{blockedAuthor}'s response was blocked by the content filter: model {usage.Data.Model} refused to answer{requestId}. Try rephrasing the request.",
                        });
                        return true;

                    case SessionErrorEvent error:
                        var errorText = error.Data.Message;
                        if (_contentFilterPending)
                        {
                            errorText += "\nThis error occurred while retrying after the content filter blocked the previous response; that refusal is the likely root cause.";
                            _contentFilterPending = false;
                        }

                        if (!string.IsNullOrEmpty(error.Data.Url))
                        {
                            errorText += $"\nMore information: {error.Data.Url}";
                        }

                        _entries.Add(new ChatEntry { Kind = ChatEntryKind.Error, Author = "System", Text = errorText });
                        return true;

                    default:
                        return false;
                }
            }
        }

        private bool UpsertMessage(string messageId, string? agentId, string? content, bool append)
        {
            var entry = _entries.FindLast(e => e.Kind == ChatEntryKind.Assistant && e.Key == messageId);
            if (entry is null)
            {
                if (string.IsNullOrEmpty(content))
                {
                    return false;
                }

                _entries.Add(new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    Key = messageId,
                    Author = ResolveAgentUnlocked(agentId),
                    IsSubagent = agentId is not null,
                    Text = content,
                });
                return true;
            }

            if (append)
            {
                entry.Text += content;
            }
            else if (!string.IsNullOrEmpty(content))
            {
                entry.Text = content;
            }

            return true;
        }

        private string ResolveAgentUnlocked(string? agentId) =>
            agentId is null ? _orchestratorName
            : _subagentByAgentId.TryGetValue(agentId, out var name) ? name
            : UnknownSubagentName;

        private string ResolveSubagentNameUnlocked(string? displayName, string? agentName, string? toolCallId, string? agentId) =>
            !string.IsNullOrEmpty(displayName) ? displayName
            : !string.IsNullOrEmpty(agentName) ? agentName
            : toolCallId is not null && _subagentByToolCallId.TryGetValue(toolCallId, out var byCall) ? byCall
            : agentId is not null && _subagentByAgentId.TryGetValue(agentId, out var byAgent) ? byAgent
            : UnknownSubagentName;
    }
}
