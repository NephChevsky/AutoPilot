using GitHub.Copilot;
using Microsoft.Extensions.Options;

namespace AutoPilot.Services
{
    /// <summary>
    /// One chat conversation (scoped per Blazor circuit). The session starts on the orchestrator agent and
    /// registers every other agent of the catalog as a custom sub-agent it can delegate to.
    /// </summary>
    public sealed class AgentChatSession(
        CopilotClient client,
        AgentCatalog catalog,
        IOptions<AutoPilotSettings> options,
        ILogger<AgentChatSession> logger) : IAsyncDisposable
    {
        private const string TaskToolName = "task";

        private readonly Lock _gate = new();
        private readonly List<ChatEntry> _entries = [];
        private readonly Dictionary<string, string> _subagentByToolCallId = [];
        private readonly Dictionary<string, string> _subagentByAgentId = [];
        private CopilotSession? _session;
        private IDisposable? _subscription;
        private TaskCompletionSource<UserInputResponse>? _pendingAnswer;
        private string _orchestratorName = "Orchestrator";

        public event Func<Task>? Changed;

        public bool IsBusy { get; private set; }

        public UserInputRequest? PendingQuestion { get; private set; }

        public IReadOnlyList<ChatEntry> GetEntries()
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }

        public async Task SendAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt) || IsBusy)
            {
                return;
            }

            IsBusy = true;
            Add(new ChatEntry { Kind = ChatEntryKind.User, Author = "You", Text = prompt.Trim() });
            await NotifyAsync();

            try
            {
                _session ??= await CreateSessionAsync();
                await _session.SendAsync(new MessageOptions { Prompt = prompt.Trim() });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send message to the Copilot session.");
                Add(new ChatEntry { Kind = ChatEntryKind.Error, Author = "System", Text = ex.Message });
                IsBusy = false;
                await NotifyAsync();
            }
        }

        public async Task AnswerAsync(string answer, bool wasFreeform)
        {
            TaskCompletionSource<UserInputResponse>? pending;
            lock (_gate)
            {
                pending = _pendingAnswer;
                _pendingAnswer = null;
                PendingQuestion = null;
            }

            if (pending is null)
            {
                return;
            }

            Add(new ChatEntry { Kind = ChatEntryKind.User, Author = "You", Text = answer });
            pending.TrySetResult(new UserInputResponse { Answer = answer, WasFreeform = wasFreeform });
            await NotifyAsync();
        }

        public async Task AbortAsync()
        {
            CancelPendingQuestion();
            if (_session is not null)
            {
                await _session.AbortAsync();
            }

            Add(new ChatEntry { Kind = ChatEntryKind.Activity, Author = "System", Text = "Run stopped." });
            IsBusy = false;
            await NotifyAsync();
        }

        public async Task ResetAsync()
        {
            await CloseSessionAsync();
            lock (_gate)
            {
                _entries.Clear();
                _subagentByToolCallId.Clear();
                _subagentByAgentId.Clear();
            }

            IsBusy = false;
            await NotifyAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Changed = null;
            await CloseSessionAsync();
        }

        private async Task<CopilotSession> CreateSessionAsync()
        {
            var settings = options.Value;
            var agents = catalog.LoadAgents();
            var orchestrator = agents.FirstOrDefault(a => string.Equals(a.Name, settings.OrchestratorAgent, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Orchestrator agent '{settings.OrchestratorAgent}' was not found in '{catalog.AgentsDirectory}'.");
            _orchestratorName = orchestrator.DisplayName;

            var workingDirectory = settings.ResolveWorkingDirectory();
            var tools = new WorkspaceTools(workingDirectory, settings.VerificationCommands, TimeSpan.FromSeconds(settings.VerificationTimeoutSeconds));
            await tools.CaptureBaselineAsync(CancellationToken.None);

            var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = orchestrator.Model,
                ReasoningEffort = orchestrator.ReasoningEffort,
                WorkingDirectory = workingDirectory,
                CustomAgents = [.. agents.Select(a => a.ToCustomAgentConfig())],
                Agent = orchestrator.Name,
                Tools = tools.CreateTools(),
                Streaming = true,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                OnUserInputRequest = (request, _) => WaitForUserAnswerAsync(request),
                Hooks = new SessionHooks
                {
                    OnSubagentStart = (input, _) =>
                    {
                        if (input.AgentId is not null)
                        {
                            lock (_gate)
                            {
                                _subagentByAgentId[input.AgentId] = string.IsNullOrEmpty(input.AgentDisplayName) ? input.AgentName : input.AgentDisplayName;
                            }
                        }

                        return Task.FromResult<SubagentStartHookOutput?>(null);
                    },
                },
            });

            _subscription = session.On<SessionEvent>(OnSessionEvent);
            return session;
        }

        private async Task<UserInputResponse> WaitForUserAnswerAsync(UserInputRequest request)
        {
            var pending = new TaskCompletionSource<UserInputResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pendingAnswer?.TrySetCanceled();
                _pendingAnswer = pending;
                PendingQuestion = request;
            }

            Add(new ChatEntry { Kind = ChatEntryKind.Assistant, Author = _orchestratorName, Text = request.Question });
            await NotifyAsync();
            return await pending.Task;
        }

        private void OnSessionEvent(SessionEvent sessionEvent)
        {
            switch (sessionEvent)
            {
                case AssistantMessageDeltaEvent delta:
                    UpsertMessage(delta.Data.MessageId, delta.AgentId, delta.Data.DeltaContent, append: true);
                    break;

                case AssistantMessageEvent message:
                    UpsertMessage(message.Data.MessageId, message.AgentId, message.Data.Content, append: false);
                    break;

                case ToolExecutionStartEvent toolStart when toolStart.Data.ToolName != TaskToolName:
                    Add(new ChatEntry
                    {
                        Kind = ChatEntryKind.Tool,
                        Key = toolStart.Data.ToolCallId,
                        Author = ResolveAgent(toolStart.AgentId),
                        IsSubagent = toolStart.AgentId is not null,
                        Text = toolStart.Data.ToolName,
                    });
                    break;

                case ToolExecutionCompleteEvent toolComplete:
                    lock (_gate)
                    {
                        var entry = _entries.FindLast(e => e.Kind == ChatEntryKind.Tool && e.Key == toolComplete.Data.ToolCallId);
                        if (entry is not null)
                        {
                            entry.Succeeded = toolComplete.Data.Success;
                        }
                    }

                    break;

                case SubagentStartedEvent started:
                    var agentName = string.IsNullOrEmpty(started.Data.AgentDisplayName) ? started.Data.AgentName : started.Data.AgentDisplayName;
                    lock (_gate)
                    {
                        _subagentByToolCallId[started.Data.ToolCallId] = agentName;
                    }

                    Add(new ChatEntry { Kind = ChatEntryKind.Activity, Author = _orchestratorName, Text = $"{_orchestratorName} delegated to {agentName}" });
                    break;

                case SubagentCompletedEvent completed:
                    Add(new ChatEntry { Kind = ChatEntryKind.Activity, Author = _orchestratorName, Text = $"{ResolveTaskAgent(completed.Data.ToolCallId)} finished" });
                    break;

                case SubagentFailedEvent failed:
                    var failedName = ResolveTaskAgent(failed.Data.ToolCallId);
                    Add(new ChatEntry { Kind = ChatEntryKind.Error, Author = failedName, Text = $"{failedName} failed: {failed.Data.Error}" });
                    break;

                case SessionErrorEvent error:
                    Add(new ChatEntry { Kind = ChatEntryKind.Error, Author = "System", Text = error.Data.Message });
                    break;

                case SessionIdleEvent:
                    IsBusy = false;
                    break;

                default:
                    return;
            }

            _ = NotifyAsync();
        }

        private void UpsertMessage(string messageId, string? agentId, string? content, bool append)
        {
            lock (_gate)
            {
                var entry = _entries.FindLast(e => e.Kind == ChatEntryKind.Assistant && e.Key == messageId);
                if (entry is null)
                {
                    if (string.IsNullOrEmpty(content))
                    {
                        return;
                    }

                    _entries.Add(new ChatEntry
                    {
                        Kind = ChatEntryKind.Assistant,
                        Key = messageId,
                        Author = ResolveAgentUnlocked(agentId),
                        IsSubagent = agentId is not null,
                        Text = content,
                    });
                    return;
                }

                if (append)
                {
                    entry.Text += content;
                }
                else if (!string.IsNullOrEmpty(content))
                {
                    entry.Text = content;
                }
            }
        }

        /// <summary>Resolves the author of an event from its sub-agent instance id (null for the orchestrator).</summary>
        private string ResolveAgent(string? agentId)
        {
            lock (_gate)
            {
                return ResolveAgentUnlocked(agentId);
            }
        }

        private string ResolveAgentUnlocked(string? agentId) =>
            agentId is null ? _orchestratorName
            : _subagentByAgentId.TryGetValue(agentId, out var name) ? name
            : "Sub-agent";

        /// <summary>Resolves the sub-agent spawned by a given task tool call.</summary>
        private string ResolveTaskAgent(string toolCallId)
        {
            lock (_gate)
            {
                return _subagentByToolCallId.TryGetValue(toolCallId, out var name) ? name : "Sub-agent";
            }
        }

        private void Add(ChatEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        private void CancelPendingQuestion()
        {
            lock (_gate)
            {
                _pendingAnswer?.TrySetCanceled();
                _pendingAnswer = null;
                PendingQuestion = null;
            }
        }

        private async Task CloseSessionAsync()
        {
            CancelPendingQuestion();
            _subscription?.Dispose();
            _subscription = null;
            if (_session is not null)
            {
                var session = _session;
                _session = null;
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispose the Copilot session.");
                }
            }
        }

        private async Task NotifyAsync()
        {
            if (Changed is { } handler)
            {
                try
                {
                    await handler();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Chat change notification failed.");
                }
            }
        }
    }
}
