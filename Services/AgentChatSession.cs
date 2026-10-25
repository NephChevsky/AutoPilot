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
        private readonly Lock _gate = new();
        private readonly ChatTranscript _transcript = new();
        private CopilotSession? _session;
        private IDisposable? _subscription;
        private TaskCompletionSource<UserInputResponse>? _pendingAnswer;

        public event Func<Task>? Changed;

        public bool IsBusy { get; private set; }

        /// <summary>True while a persisted conversation is being opened; sends, resets and further loads are ignored.</summary>
        public bool IsLoading { get; private set; }

        public UserInputRequest? PendingQuestion { get; private set; }

        /// <summary>Id of the open conversation; null until the first message of a new conversation is sent.</summary>
        public string? CurrentSessionId { get; private set; }

        public IReadOnlyList<ChatEntry> GetEntries() => _transcript.GetEntries();

        public async Task SendAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)             || IsBusy || IsLoading)
            {
                return;
            }

            IsBusy = true;
            Add(new ChatEntry { Kind = ChatEntryKind.User, Author = "You", Text = prompt.Trim() });
            await NotifyAsync();

            try
            {
                _session ??= await CreateSessionAsync(resumeSessionId: null);
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
            if (IsLoading)
            {
                return;
            }

            await CloseSessionAsync();
            _transcript.Clear();
            CurrentSessionId = null;
            IsBusy = false;
            await NotifyAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Changed = null;
            await CloseSessionAsync();
        }

        /// <summary>Lists previous conversations of the configured working directory, newest first.</summary>
        public async Task<IReadOnlyList<SessionMetadata>> ListConversationsAsync(CancellationToken cancellationToken = default)
        {
            var workingDirectory = options.Value.ResolveWorkingDirectory();
            var sessions = await client.ListSessionsAsync(new SessionListFilter { WorkingDirectory = workingDirectory }, cancellationToken);
            var expected = NormalizePath(workingDirectory);
            return [.. sessions
                .Where(s => s.Context?.WorkingDirectory is not { } dir || string.Equals(NormalizePath(dir), expected, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.ModifiedTime)];
        }

        /// <summary>Closes the current conversation and opens a persisted one, replaying its transcript. No-op while busy.</summary>
        public async Task LoadConversationAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || IsBusy || IsLoading)
            {
                return;
            }

            IsLoading = true;
            CopilotSession? session = null;
            try
            {
                await NotifyAsync();
                await CloseSessionAsync();
                _transcript.Clear();
                CurrentSessionId = null;

                session = await CreateSessionAsync(sessionId);
                var events = await session.GetEventsAsync(CancellationToken.None);
                _transcript.Replay(events);
                _session = session;
                _subscription = session.On<SessionEvent>(OnSessionEvent);
                CurrentSessionId = session.SessionId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load conversation {SessionId}.", sessionId);
                if (session is not null && !ReferenceEquals(session, _session))
                {
                    try
                    {
                        await session.DisposeAsync();
                    }
                    catch (Exception disposeEx)
                    {
                        logger.LogWarning(disposeEx, "Failed to dispose the resumed Copilot session.");
                    }
                }

                await CloseSessionAsync();
                CurrentSessionId = null;
                Add(new ChatEntry { Kind = ChatEntryKind.Error, Author = "System", Text = $"Failed to load conversation: {ex.Message}" });
            }
            finally
            {
                IsLoading = false;
            }

            await NotifyAsync();
        }

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>Creates a new session, or resumes <paramref name="resumeSessionId"/>, with identical agents, tools and handlers.</summary>
        private async Task<CopilotSession> CreateSessionAsync(string? resumeSessionId)
        {
            var settings = options.Value;
            var agents = catalog.LoadAgents();
            var orchestrator = agents.FirstOrDefault(a => string.Equals(a.Name, settings.OrchestratorAgent, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Orchestrator agent '{settings.OrchestratorAgent}' was not found in '{catalog.AgentsDirectory}'.");
            _transcript.OrchestratorName = orchestrator.DisplayName;

            var workingDirectory = settings.ResolveWorkingDirectory();
            var tools = new WorkspaceTools(workingDirectory, settings.VerificationCommands, TimeSpan.FromSeconds(settings.VerificationTimeoutSeconds));
            await tools.CaptureBaselineAsync(CancellationToken.None);

            var customAgents = agents.Select(a => a.ToCustomAgentConfig()).ToList();
            var hooks = new SessionHooks
            {
                OnSubagentStart = (input, _) =>
                {
                    if (input.AgentId is not null)
                    {
                        _transcript.RegisterSubagent(input.AgentId, string.IsNullOrEmpty(input.AgentDisplayName) ? input.AgentName : input.AgentDisplayName);
                    }

                    return Task.FromResult<SubagentStartHookOutput?>(null);
                },
            };

            if (resumeSessionId is not null)
            {
                return await client.ResumeSessionAsync(resumeSessionId, new ResumeSessionConfig
                {
                    Model = orchestrator.Model,
                    ReasoningEffort = orchestrator.ReasoningEffort,
                    WorkingDirectory = workingDirectory,
                    CustomAgents = customAgents,
                    Agent = orchestrator.Name,
                    Tools = tools.CreateTools(),
                    Streaming = true,
                    SuppressResumeEvent = true,
                    OnPermissionRequest = PermissionHandler.ApproveAll,
                    OnUserInputRequest = (request, _) => WaitForUserAnswerAsync(request),
                    Hooks = hooks,
                });
            }

            var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = orchestrator.Model,
                ReasoningEffort = orchestrator.ReasoningEffort,
                WorkingDirectory = workingDirectory,
                CustomAgents = customAgents,
                Agent = orchestrator.Name,
                Tools = tools.CreateTools(),
                Streaming = true,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                OnUserInputRequest = (request, _) => WaitForUserAnswerAsync(request),
                Hooks = hooks,
            });

            _subscription = session.On<SessionEvent>(OnSessionEvent);
            CurrentSessionId = session.SessionId;
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

            Add(new ChatEntry { Kind = ChatEntryKind.Assistant, Author = _transcript.OrchestratorName, Text = request.Question });
            await NotifyAsync();
            return await pending.Task;
        }

        private void OnSessionEvent(SessionEvent sessionEvent)
        {
            if (sessionEvent is SessionIdleEvent)
            {
                IsBusy = false;
            }
            else if (!_transcript.Apply(sessionEvent))
            {
                return;
            }

            _ = NotifyAsync();
        }

        private void Add(ChatEntry entry) => _transcript.Add(entry);

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
