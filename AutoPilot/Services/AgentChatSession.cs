using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AutoPilot.Services
{
    /// <summary>A persisted conversation and the repository it belongs to.</summary>
    public sealed record ConversationSummary(SessionMetadata Session, RepositoryInfo Repository);

    /// <summary>
    /// One chat conversation (scoped per Blazor circuit). The session starts on the orchestrator agent and
    /// registers every other agent of the catalog as a custom sub-agent it can delegate to.
    /// </summary>
    public sealed class AgentChatSession(
        CopilotClient client,
        AgentCatalog catalog,
        IOptions<AutoPilotSettings> options,
        UsageCache cache,
        TaskStore tasks,
        RepositoryCatalog repositories,
        ILogger<AgentChatSession> logger) : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private static readonly TimeSpan MetricsTimeout = TimeSpan.FromSeconds(5);
        // Serializes snapshot-and-record so an older snapshot can never overwrite a newer one.
        private readonly SemaphoreSlim _usageGate = new(1, 1);
        private CostRefreshCoalescer? _costRefreshInstance;
        private CostRefreshCoalescer _costRefresh =>
            LazyInitializer.EnsureInitialized(ref _costRefreshInstance, () => new CostRefreshCoalescer(SnapshotAndRecordAsync))!;
        private string? _title;
        private readonly ChatTranscript _transcript = new();
        private CopilotSession? _session;
        private IDisposable? _subscription;
        private TaskCompletionSource<UserInputResponse>? _pendingAnswer;

        public event Func<Task>? Changed;

        public bool IsBusy { get; private set; }

        /// <summary>True while a persisted conversation is being opened; sends, resets and further loads are ignored.</summary>
        public bool IsLoading { get; private set; }

        /// <summary>True while the conversation branch is being merged; sends, resets and loads are ignored.</summary>
        public bool IsMerging { get; private set; }

        public UserInputRequest? PendingQuestion { get; private set; }

        /// <summary>Id of the open conversation; null until the first message of a new conversation is sent.</summary>
        public string? CurrentSessionId { get; private set; }

        /// <summary>Dedicated git branch of the open conversation; null when none is open or it is a legacy conversation.</summary>
        public string? CurrentBranch { get; private set; }

        /// <summary>Saved cost of the open conversation (null until it has been recorded).</summary>
        public SessionCost? CurrentCost { get; private set; }

        /// <summary>Repository chosen for the next new conversation; null until chosen.</summary>
        public RepositoryInfo? SelectedRepository { get; private set; }

        /// <summary>Repository of the open conversation; null when none is open.</summary>
        public RepositoryInfo? CurrentRepository { get; private set; }

        /// <summary>Chooses the repository of the next new conversation. Ignored once a conversation is open or while busy, loading or merging.</summary>
        public void SelectRepository(RepositoryInfo? repository)
        {
            if (CurrentSessionId is not null || IsBusy || IsLoading || IsMerging)
            {
                return;
            }

            SelectedRepository = repository;
        }

        public IReadOnlyList<ChatEntry> GetEntries() => _transcript.GetEntries();

        /// <summary>Sends a prompt. Returns true only when the prompt was handed to the session successfully.</summary>
        public async Task<bool> SendAsync(string prompt, IReadOnlyList<PromptImage>? images = null)
        {
            if (string.IsNullOrWhiteSpace(prompt) || IsBusy || IsLoading || IsMerging)
            {
                return false;
            }

            var repository = CurrentRepository ?? SelectedRepository;
            if (_session is null && repository is null)
            {
                return false;
            }

            IsBusy = true;
            _title ??= UsageAccumulator.Truncate(prompt);
            Add(new ChatEntry { Kind = ChatEntryKind.User, Author = "You", Text = prompt.Trim(), Images = images ?? [] });
            await NotifyAsync();

            try
            {
                _session ??= await CreateSessionAsync(resumeSessionId: null, repository!);
                var message = new MessageOptions { Prompt = prompt.Trim() };
                var attachments = ToAttachments(images);
                if (attachments is not null)
                {
                    message.Attachments = attachments;
                }

                await _session.SendAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send message to the Copilot session.");
                if (_session is null)
                {
                    CurrentSessionId = null;
                    CurrentBranch = null;
                    CurrentRepository = null;
                }
                Add(new ChatEntry { Kind = ChatEntryKind.Error, Author = "System", Text = ex.Message });
                IsBusy = false;
                await NotifyAsync();
                return false;
            }
        }

        /// <summary>Maps prompt images to SDK blob attachments; null when there are none.</summary>
        internal static List<Attachment>? ToAttachments(IReadOnlyList<PromptImage>? images) =>
            images is { Count: > 0 }
                ? [.. images.Select(image => (Attachment)new AttachmentBlob
                {
                    Data = Convert.ToBase64String(image.Data),
                    MimeType = image.MimeType,
                    DisplayName = image.DisplayName,
                })]
                : null;

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
            if (IsLoading || IsMerging)
            {
                return;
            }

            await CloseSessionAsync();
            _transcript.Clear();
            CurrentSessionId = null;
            CurrentBranch = null;
            CurrentRepository = null;
            IsBusy = false;
            await NotifyAsync();
        }

        /// <summary>Diff of the current conversation's branch; null when there is no open conversation or it has no dedicated branch.</summary>
        public Task<ConversationDiff?> GetCurrentDiffAsync(CancellationToken cancellationToken = default)
        {
            var sessionId = CurrentSessionId;
            if (sessionId is null || CurrentBranch is null || CurrentRepository is null)
            {
                return Task.FromResult<ConversationDiff?>(null);
            }

            return repositories.GetWorktrees(CurrentRepository).GetDiffAsync(sessionId, cancellationToken);
        }

        /// <summary>
        /// Merges the current conversation's branch into the branch checked out in the working directory, pushes it and cleans up.
        /// Returns null (no-op) when there is no conversation with a dedicated branch or a run, load or merge is in progress.
        /// The conversation is reopened afterwards (in its worktree when the branch still exists, otherwise as a legacy conversation).
        /// </summary>
        public async Task<ConversationMergeResult?> MergeCurrentAsync(CancellationToken cancellationToken = default)
        {
            var sessionId = CurrentSessionId;
            var repository = CurrentRepository;
            if (sessionId is null || CurrentBranch is null || repository is null || IsBusy || IsLoading || IsMerging)
            {
                return null;
            }

            var worktrees = repositories.GetWorktrees(repository);
            IsMerging = true;
            ConversationMergeResult result;
            try
            {
                await NotifyAsync();
                await CloseSessionAsync();
                try
                {
                    result = await worktrees.MergeAsync(sessionId, options.Value.GitRemote, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Nothing was merged (cancellation is only honoured before the merge starts); reopen the conversation and propagate.
                    try
                    {
                        await LoadConversationCoreAsync(sessionId, repository);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Reopening conversation {SessionId} after a cancelled merge failed.", sessionId);
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Merging conversation {SessionId} failed unexpectedly.", sessionId);
                    result = new ConversationMergeResult(ConversationMergeStatus.Rejected, $"Merge failed: {ex.Message}");
                }

                var success = result.Status is ConversationMergeStatus.Merged or ConversationMergeStatus.CleanupFailed;
                if (success)
                {
                    try
                    {
                        await tasks.DeleteBySessionAsync(sessionId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete the tasks linked to merged conversation {SessionId}.", sessionId);
                    }
                }

                await LoadConversationCoreAsync(sessionId, repository);
                Add(new ChatEntry
                {
                    Kind = success ? ChatEntryKind.Activity : ChatEntryKind.Error,
                    Author = "System",
                    Text = result.Message,
                });
            }
            finally
            {
                IsMerging = false;
            }

            await NotifyAsync();
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            Changed = null;
            _costRefresh.Dispose();
            await CloseSessionAsync();
        }

        /// <summary>
        /// Lists previous conversations of every discovered repository (its main directory or its conversation worktrees),
        /// newest first, each paired with its repository. Sessions of other directories are excluded.
        /// </summary>
        public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(CancellationToken cancellationToken = default)
        {
            var settings = options.Value;
            var scopes = repositories.GetRepositories()
                .Select(r => (Repository: r, Main: NormalizePath(r.Path), WorktreesRoot: NormalizePath(settings.ResolveWorktreesDirectory(r.Path))))
                .ToList();
            // The shared client auto-starts on its first call and a canceled start permanently breaks it (every later call
            // throws TaskCanceledException), so start it uncancelably; the caller's token only abandons the wait.
            await client.StartAsync(CancellationToken.None).WaitAsync(cancellationToken);
            // The server filter is an exact match, so worktree conversations are selected client-side.
            var sessions = await client.ListSessionsAsync(null, cancellationToken);
            var result = new List<ConversationSummary>();
            foreach (var session in sessions.OrderByDescending(s => s.ModifiedTime))
            {
                foreach (var scope in scopes)
                {
                    if (IsConversationDirectory(session.Context?.WorkingDirectory, scope.Main, scope.WorktreesRoot))
                    {
                        result.Add(new ConversationSummary(session, scope.Repository));
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Builds a predicate telling whether a session working directory belongs to a discovered repository (its main directory or
        /// its worktrees root), using the same rule as <see cref="ListConversationsAsync"/>. Repositories are discovered once, when this is called.
        /// </summary>
        public static Func<string?, bool> CreateConversationDirectoryFilter(RepositoryCatalog repositories, AutoPilotSettings settings)
        {
            ArgumentNullException.ThrowIfNull(repositories);
            ArgumentNullException.ThrowIfNull(settings);
            var scopes = repositories.GetRepositories()
                .Select(r => (Main: NormalizePath(r.Path), WorktreesRoot: NormalizePath(settings.ResolveWorktreesDirectory(r.Path))))
                .ToList();
            return directory => scopes.Any(s => IsConversationDirectory(directory, s.Main, s.WorktreesRoot));
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

        /// <summary>Closes the current conversation and opens a persisted one in its repository, replaying its transcript. No-op while busy.</summary>
        public async Task LoadConversationAsync(ConversationSummary conversation)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            if (IsBusy || IsLoading || IsMerging)
            {
                return;
            }

            await LoadConversationCoreAsync(conversation.Session.SessionId, conversation.Repository);
        }

        private async Task LoadConversationCoreAsync(string sessionId, RepositoryInfo repository)
        {
            IsLoading = true;
            CopilotSession? session = null;
            try
            {
                await NotifyAsync();
                await CloseSessionAsync();
                _transcript.Clear();
                CurrentSessionId = null;
                CurrentBranch = null;
                CurrentRepository = null;

                session = await CreateSessionAsync(sessionId, repository);
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
                CurrentRepository = null;
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
        private async Task<CopilotSession> CreateSessionAsync(string? resumeSessionId, RepositoryInfo repository)
        {
            var settings = options.Value;
            var agents = catalog.LoadAgents();
            var orchestrator = agents.FirstOrDefault(a => string.Equals(a.Name, settings.OrchestratorAgent, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Orchestrator agent '{settings.OrchestratorAgent}' was not found in '{catalog.AgentsDirectory}'.");
            _transcript.OrchestratorName = orchestrator.DisplayName;

            // New conversation: pre-generate the id so the branch and worktree can be named after it.
            // Existing conversation: legacy ones (no dedicated branch) keep running in the main directory.
            string sessionDirectory = repository.Path;
            string? branch = null;
            var worktrees = repositories.GetWorktrees(repository);
            var sessionId = resumeSessionId ?? Guid.NewGuid().ToString();
            if (resumeSessionId is null || await worktrees.BranchExistsAsync(resumeSessionId, CancellationToken.None))
            {
                var workspace = await worktrees.EnsureAsync(sessionId, CancellationToken.None);
                sessionDirectory = workspace.Directory;
                branch = workspace.Branch;
            }

            var workingDirectory = sessionDirectory;
            var tools = new WorkspaceTools(workingDirectory, branch, settings.GitRemote);
            await tools.CaptureBaselineAsync(CancellationToken.None);
            var taskTools = new RepositoryTaskTools(
                tasks,
                () => repositories.GetRepositories(),
                repository,
                sessionId,
                settings.OrchestratorAgent,
                async (question, choices, ct) =>
                {
                    var response = await WaitForUserAnswerAsync(new UserInputRequest { Question = question, Choices = [.. choices], AllowFreeform = true });
                    return (response.Answer, response.WasFreeform);
                });
            var allTools = new List<AIFunctionDeclaration>(tools.CreateTools());
            allTools.AddRange(taskTools.CreateTools());
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
                var resumed = await client.ResumeSessionAsync(resumeSessionId, BuiltInAgents.Apply(new ResumeSessionConfig
                {
                    Model = orchestrator.Model,
                    ReasoningEffort = orchestrator.ReasoningEffort,
                    WorkingDirectory = workingDirectory,
                    CustomAgents = customAgents,
                    Agent = orchestrator.Name,
                    Tools = allTools,
                    Streaming = true,
                    SuppressResumeEvent = true,
                    OnPermissionRequest = PermissionHandler.ApproveAll,
                    OnUserInputRequest = (request, _) => WaitForUserAnswerAsync(request),
                    Hooks = hooks,
                }));
                CurrentCost = cache.Get(resumed.SessionId);
                CurrentBranch = branch;
                CurrentRepository = repository;
                return resumed;
            }

            var session = await client.CreateSessionAsync(BuiltInAgents.Apply(new SessionConfig
            {
                SessionId = sessionId,
                Model = orchestrator.Model,
                ReasoningEffort = orchestrator.ReasoningEffort,
                WorkingDirectory = workingDirectory,
                CustomAgents = customAgents,
                Agent = orchestrator.Name,
                Tools = allTools,
                Streaming = true,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                OnUserInputRequest = (request, _) => WaitForUserAnswerAsync(request),
                Hooks = hooks,
            }));

            _subscription = session.On<SessionEvent>(OnSessionEvent);
            CurrentSessionId = session.SessionId;
            CurrentBranch = branch;
            CurrentRepository = repository;
            CurrentCost = cache.Get(session.SessionId);
            return session;
        }

        /// <summary>
        /// Reads the current (cumulative) SDK metrics and replaces the session's entry in the usage cache. Never throws:
        /// failures are logged only and never reach the transcript.
        /// </summary>
        private Task SnapshotAndRecordAsync() =>
            SnapshotAndRecordAsync(_session, _title, closing: false);

        private async Task SnapshotAndRecordAsync(CopilotSession? session, string? title, bool closing)
        {
            var orchestratorName = _transcript.OrchestratorName;
            if (session is null)
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
                    var existing = cache.Get(session.SessionId);
                    var now = DateTimeOffset.UtcNow;
                    var cost = UsageAccumulator.ToSessionCost(
                        session.SessionId, current, existing?.Title ?? UsageAccumulator.Truncate(title), existing?.FirstSeen ?? now, now);
                    if (existing is null && !UsageAccumulator.HasUsage(cost))
                    {
                        return;
                    }

                    cache.Update(cost);
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
            // Live cost: refresh after each model call / turn / sub-agent, before the transcript may early-return.
            if (sessionEvent is AssistantUsageEvent or AssistantTurnEndEvent or SubagentCompletedEvent or SubagentFailedEvent)
            {
                _costRefresh.Request();
            }

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
            var title = _title;
            _session = null;
            _title = null;
            CurrentCost = null;
            if (session is null)
            {
                return;
            }

            await SnapshotAndRecordAsync(session, title, closing: true);
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
