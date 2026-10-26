namespace AutoPilot.Services
{
    /// <summary>
    /// In-memory, thread-safe cache of per-session usage costs. It is seeded once from the Copilot runtime's own session files
    /// (<c>&lt;session-state&gt;\&lt;id&gt;\events.jsonl</c>, last <c>session.shutdown</c> event) and then updated live from the SDK metrics.
    /// Nothing is ever written to disk. Registered as a singleton so all Blazor circuits share it.
    /// </summary>
    public sealed class UsageCache
    {
        private readonly string _sessionStateDirectory;
        private readonly string _orchestratorName;
        private readonly Func<string?, bool> _isAutoPilotDirectory;
        private readonly ILogger<UsageCache> _logger;
        private readonly Lock _lock = new();
        private readonly Lock _loadLock = new();
        private readonly Dictionary<string, SessionCost> _records = new(StringComparer.Ordinal);
        private volatile bool _loaded;

        /// <param name="sessionStateDirectory">The runtime's <c>session-state</c> folder.</param>
        /// <param name="orchestratorName">Display name used for the "main" agent.</param>
        /// <param name="isAutoPilotDirectory">Tells whether a session working directory belongs to an AutoPilot repository.</param>
        public UsageCache(string sessionStateDirectory, string orchestratorName, Func<string?, bool> isAutoPilotDirectory, ILogger<UsageCache> logger)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionStateDirectory);
            ArgumentNullException.ThrowIfNull(isAutoPilotDirectory);
            ArgumentNullException.ThrowIfNull(logger);
            _sessionStateDirectory = sessionStateDirectory;
            _orchestratorName = orchestratorName ?? "";
            _isAutoPilotDirectory = isAutoPilotDirectory;
            _logger = logger;
        }

        /// <summary>Raised after <see cref="Update"/> and after the initial load completed. Handlers run on the calling thread.</summary>
        public event Action? Changed;

        /// <summary>Inserts or replaces the entry of <paramref name="cost"/>.SessionId in memory.</summary>
        public void Update(SessionCost cost)
        {
            ArgumentNullException.ThrowIfNull(cost);
            ArgumentException.ThrowIfNullOrWhiteSpace(cost.SessionId);

            lock (_lock)
            {
                _records[cost.SessionId] = cost;
            }

            RaiseChanged();
        }

        /// <summary>Returns the entry of a session, or null.</summary>
        public SessionCost? Get(string sessionId)
        {
            EnsureLoaded();
            lock (_lock)
            {
                return _records.GetValueOrDefault(sessionId);
            }
        }

        /// <summary>Returns all entries.</summary>
        public IReadOnlyList<SessionCost> GetAll()
        {
            EnsureLoaded();
            lock (_lock)
            {
                return [.. _records.Values];
            }
        }

        /// <summary>Sums all entries.</summary>
        public CostTotals GetTotals() => UsageAccumulator.Total(GetAll());

        /// <summary>Sums per-agent figures over all entries, keyed by agent name.</summary>
        public IReadOnlyDictionary<string, AgentCost> GetAgentTotals() => UsageAccumulator.TotalByAgent(GetAll());

        /// <summary>
        /// Scans the session-state folder once (idempotent, thread-safe; concurrent callers wait for the running scan).
        /// Entries updated live in the meantime are never overwritten by disk data. Never throws for I/O problems.
        /// </summary>
        public void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (_loadLock)
            {
                if (_loaded)
                {
                    return;
                }

                try
                {
                    Load();
                }
                finally
                {
                    _loaded = true;
                }
            }

            RaiseChanged();
        }

        private void Load()
        {
            string[] directories;
            try
            {
                if (!Directory.Exists(_sessionStateDirectory))
                {
                    _logger.LogWarning("Session state directory '{Directory}' does not exist; no past usage loaded.", _sessionStateDirectory);
                    return;
                }

                directories = Directory.GetDirectories(_sessionStateDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Session state directory '{Directory}' could not be read; no past usage loaded.", _sessionStateDirectory);
                return;
            }

            foreach (var directory in directories)
            {
                var file = Path.Combine(directory, "events.jsonl");
                try
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    var info = SessionEventsReader.TryReadSession(file, _orchestratorName);
                    if (info is { MalformedLines: > 0 })
                    {
                        _logger.LogWarning("Session file '{File}' contains {Count} malformed line(s); skipped.", file, info.MalformedLines);
                    }

                    if (info?.Cost is not { } cost || !_isAutoPilotDirectory(info.WorkingDirectory))
                    {
                        continue;
                    }

                    lock (_lock)
                    {
                        // A live update always wins over older disk data.
                        _records.TryAdd(cost.SessionId, cost);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session file '{File}' could not be read; skipped.", file);
                }
            }
        }

        private void RaiseChanged()
        {
            if (Changed is { } handler)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Usage cache change handler failed.");
                }
            }
        }
    }
}
