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
        UsageLedger ledger,
        ConversationWorktrees worktrees,
        ILogger<AgentChatSession> logger) : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private static readonly TimeSpan MetricsTimeout = TimeSpan.FromSeconds(5);
        // Serializes snapshot-and-record so an older snapshot can never overwrite a newer one.
        private readonly SemaphoreSlim _usageGate = new(1, 1);
        // Resume-safe accounting: ledger record and SDK metrics as they were when this session was created/resumed.
        private UsageSnapshot? _usageBaseline;
        private SessionCost? _storedAtBaseline;
        private string? _title;
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

        /// <summary>Dedicated git branch of the open conversation; null when none is open or it is a legacy conversation.</summary>
        public string? CurrentBranch { get; private set; }

        /// <summary>Saved cost of the open conversation (null until it has been recorded).</summary>
        public SessionCost? CurrentCost { get; private set; }

        public IReadOnlyList<ChatEntry> GetEntries() => _transcript.GetEntries();

        public async Task SendAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)             || IsBusy || IsLoading)
            {
                return;
            }

            IsBusy = true;
            _title ??= UsageAccumulator.Truncate(prompt);
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
                if (_session is null)
                {
                    CurrentBranch = null;
                }
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
            CurrentBranch = null;
            IsBusy = false;
            await NotifyAsync();
        }

        /// <summary>Diff of the current conversation's branch; null when there is no open conversation or it has no dedicated branch.</summary>
        public Task<ConversationDiff?> GetCurrentDiffAsync(CancellationToken cancellationToken = default)
        {
            var sessionId = CurrentSessionId;
            if (sessionId is null || CurrentBranch is null)
            {
                return Task.FromResult<ConversationDiff?>(null);
            }

            return worktrees.GetDiffAsync(sessionId, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Changed = null;
            await CloseSessionAsync();
        }

        /// <summary>Lists previous conversations of the configured working directory and of its conversation worktrees, newest first.</summary>
        public async Task<IReadOnlyList<SessionMetadata>> ListConversationsAsync(CancellationToken cancellationToken = default)
        {
            var settings = options.Value;
            var expected = NormalizePath(settings.ResolveWorkingDirectory());
            var worktreesRoot = NormalizePath(settings.ResolveWorktreesDirectory());
            // The shared client auto-starts on its first call and a canceled start permanently breaks it (every later call
            // throws TaskCanceledException), so start it uncancelably; the caller's token only abandons the wait.
            await client.StartAsync(CancellationToken.None).WaitAsync(cancellationToken);
            // The server filter is an exact match, so worktree conversations are selected client-side.
            var sessions = await client.ListSessionsAsync(null, cancellationToken);
            return [.. sessions
                .Where(s => IsConversationDirectory(s.Context?.WorkingDirectory, expected, worktreesRoot))
                .OrderByDescending(s => s.ModifiedTime)];
        }

        /// <summary>
        /// True when <paramref name="directory"/> is the main working directory or lies strictly under the worktrees root
        /// (the root itself is not a conversation directory). Null, empty or malformed paths yield false; never throws.
        /// Both <paramref name="mainDirectory"/> and <paramref name="worktreesRoot"/> must already be normalized.
        /// </summary>
        public static bool IsConversationDirectory(string? directory, string mainDirectory, string worktreesRoot)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string normalized;
            try
            {
                normalized = NormalizePath(directory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                return false;
            }

            return string.Equals(normalized, mainDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(worktreesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
                CurrentBranch = null;

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
                CurrentBranch = null;
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

            // New conversation: pre-generate the id so the branch and worktree can be named after it.
            // Existing conversation: legacy ones (no dedicated branch) keep running in the main directory.
            string sessionDirectory = settings.ResolveWorkingDirectory();
            string? branch = null;
            var sessionId = resumeSessionId ?? Guid.NewGuid().ToString();
            if (resumeSessionId is null || await worktrees.BranchExistsAsync(resumeSessionId, CancellationToken.None))
            {
                var workspace = await worktrees.EnsureAsync(sessionId, CancellationToken.None);
                sessionDirectory = workspace.Directory;
                branch = workspace.Branch;
            }

            var workingDirectory = sessionDirectory;
            var tools = new WorkspaceTools(workingDirectory, settings.VerificationCommands, TimeSpan.FromSeconds(settings.VerificationTimeoutSeconds), branch, settings.GitRemote);
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
                var resumed = await client.ResumeSessionAsync(resumeSessionId, new ResumeSessionConfig
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
                await TakeBaselineAsync(resumed, resumed: true);
                CurrentBranch = branch;
                return resumed;
            }

            var session = await client.CreateSessionAsync(new SessionConfig
            {
                SessionId = sessionId,
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
            CurrentBranch = branch;
            await TakeBaselineAsync(session, resumed: false);
            return session;
        }

        /// <summary>
        /// Remembers the SDK metrics and ledger record at session start. A new session starts from zero; for a resumed one a
        /// failed baseline disables recording (null baseline) rather than risking double-counting.
        /// </summary>
        private async Task TakeBaselineAsync(CopilotSession session, bool resumed)
        {
            _storedAtBaseline = ledger.Get(session.SessionId);
            CurrentCost = _storedAtBaseline;
            _usageBaseline = resumed ? null : UsageSnapshot.Zero;
            if (!resumed)
            {
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(MetricsTimeout);
                var metrics = await session.Rpc.Usage.GetMetricsAsync(timeout.Token);
                _usageBaseline = UsageAccumulator.ToSnapshot(metrics, _transcript.OrchestratorName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read the usage baseline of session {SessionId}; its cost will not be recorded.", session.SessionId);
            }
        }

        /// <summary>
        /// Reads the current metrics and saves stored-at-baseline + (current - baseline) to the ledger. Never throws:
        /// failures are logged only and never reach the transcript.
        /// </summary>
        private Task SnapshotAndRecordAsync() =>
            SnapshotAndRecordAsync(_session, _usageBaseline, _storedAtBaseline, _title, closing: false);

        private async Task SnapshotAndRecordAsync(CopilotSession? session, UsageSnapshot? baseline, SessionCost? stored, string? title, bool closing)
        {
            var orchestratorName = _transcript.OrchestratorName;
            if (session is null || baseline is null)
            {
                return;
            }

            try
            {
                await _usageGate.WaitAsync();
                try
                {
                    // An idle snapshot that waited on the gate must not touch a session that was closed or replaced meanwhile.
                    if (!closing && !ReferenceEquals(_session, session))
                    {
                        return;
                    }

                    using var timeout = new CancellationTokenSource(MetricsTimeout);
                    var metrics = await session.Rpc.Usage.GetMetricsAsync(timeout.Token);
                    var current = UsageAccumulator.ToSnapshot(metrics, orchestratorName);
                    var cost = UsageAccumulator.Apply(session.SessionId, stored, baseline, current, title);
                    if (stored is null && !UsageAccumulator.HasUsage(cost))
                    {
                        return;
                    }

                    await ledger.RecordAsync(cost);
                    if (!closing && ReferenceEquals(_session, session))
                    {
                        CurrentCost = cost;
                    }
                }
                finally
                {
                    _usageGate.Release();
                }

                await NotifyAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record usage of session {SessionId}.", session.SessionId);
            }
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
                _ = SnapshotAndRecordAsync();
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

            // Capture and clear all state synchronously so a session created while we await is not affected.
            var session = _session;
            var baseline = _usageBaseline;
            var stored = _storedAtBaseline;
            var title = _title;
            _session = null;
            _usageBaseline = null;
            _storedAtBaseline = null;
            _title = null;
            CurrentCost = null;
            if (session is null)
            {
                return;
            }

            await SnapshotAndRecordAsync(session, baseline, stored, title, closing: true);
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose the Copilot session.");
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
