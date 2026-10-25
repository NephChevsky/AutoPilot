using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AutoPilot.Services
{
    /// <summary>
    /// Persistent, thread-safe store of per-session usage costs (a single JSON file). Registered as a singleton so all
    /// Blazor circuits share it. The file is loaded lazily; a corrupt file is backed up and replaced by an empty ledger,
    /// while an unreadable (e.g. locked) file is never overwritten: reads return empty data and writes throw until it can be read.
    /// </summary>
    public sealed class UsageLedger
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private readonly string _filePath;
        private readonly ILogger<UsageLedger> _logger;
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private Dictionary<string, SessionCost>? _records;

        public UsageLedger(string filePath, ILogger<UsageLedger> logger)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            _filePath = Path.GetFullPath(filePath);
            _logger = logger;
        }

        public UsageLedger(IOptions<AutoPilotSettings> options, ILogger<UsageLedger> logger)
            : this(options.Value.ResolveUsageFile(), logger)
        {
        }

        /// <summary>Raised after a record was saved successfully. Handlers run on the recording thread.</summary>
        public event Action? Changed;

        /// <summary>Full path of the ledger file.</summary>
        public string FilePath => _filePath;

        /// <summary>Inserts or replaces the record of <paramref name="cost"/>.SessionId and persists the ledger.</summary>
        public async Task RecordAsync(SessionCost cost, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(cost);
            ArgumentException.ThrowIfNullOrWhiteSpace(cost.SessionId);

            await _writeGate.WaitAsync(ct);
            try
            {
                List<SessionCost> snapshot;
                lock (_lock)
                {
                    var records = EnsureLoaded()
                        ?? throw new IOException($"The usage ledger '{_filePath}' is unreadable; not overwriting it.");
                    records[cost.SessionId] = cost;
                    snapshot = [.. records.Values];
                }

                await WriteAsync(snapshot, ct);
            }
            finally
            {
                _writeGate.Release();
            }

            if (Changed is { } handler)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Usage ledger change handler failed.");
                }
            }
        }

        /// <summary>Returns the record of a session, or null.</summary>
        public SessionCost? Get(string sessionId)
        {
            lock (_lock)
            {
                return EnsureLoaded()?.GetValueOrDefault(sessionId);
            }
        }

        /// <summary>Returns all records.</summary>
        public IReadOnlyList<SessionCost> GetAll()
        {
            lock (_lock)
            {
                return EnsureLoaded() is { } records ? [.. records.Values] : [];
            }
        }

        /// <summary>Sums all records.</summary>
        public CostTotals GetTotals() => UsageAccumulator.Total(GetAll());

        // Must be called with _lock held.
        // Returns null while the file is unreadable (nothing is cached in that case).
        private Dictionary<string, SessionCost>? EnsureLoaded() => _records ??= Load();

        private Dictionary<string, SessionCost>? Load()
        {
            var records = new Dictionary<string, SessionCost>(StringComparer.Ordinal);
            if (!File.Exists(_filePath))
            {
                return records;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return records;
                }

                var file = JsonSerializer.Deserialize<LedgerFile>(json, JsonOptions)
                    ?? throw new JsonException("The ledger file contains null.");
                var sessions = file.Sessions ?? throw new JsonException("The ledger file has no sessions list.");

                // Structurally wrong files (null/[]/null sessions list) are corrupt; individual null or id-less entries are skipped.
                foreach (var session in sessions)
                {
                    if (!string.IsNullOrWhiteSpace(session?.SessionId))
                    {
                        records[session.SessionId] = session;
                    }
                }

                return records;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                var backup = $"{_filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                try
                {
                    File.Move(_filePath, backup, overwrite: true);
                    _logger.LogWarning(ex, "Usage ledger '{File}' is corrupt; backed it up to '{Backup}' and started empty.", _filePath, backup);
                }
                catch (Exception moveEx)
                {
                    _logger.LogWarning(moveEx, "Usage ledger '{File}' is corrupt ({Reason}) and could not be backed up; starting empty.", _filePath, ex.Message);
                }

                return new Dictionary<string, SessionCost>(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable (not corrupt), e.g. briefly locked: do not cache anything so the next call retries,
                // and never write over the file in the meantime.
                _logger.LogWarning(ex, "Usage ledger '{File}' could not be read; will retry on next access.", _filePath);
                return null;
            }
        }

        private async Task WriteAsync(List<SessionCost> records, CancellationToken ct)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, new LedgerFile { Sessions = records }, JsonOptions, ct);
                }

                File.Move(temp, _filePath, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }

                throw;
            }
        }

        private sealed class LedgerFile
        {
            public int Version { get; set; } = 1;

            public List<SessionCost>? Sessions { get; set; } = [];
        }
    }
}
