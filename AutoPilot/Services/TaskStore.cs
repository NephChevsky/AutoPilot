using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AutoPilot.Services
{
    /// <summary>
    /// Persistent, thread-safe store of repository tasks (a single JSON file). Registered as a singleton so all
    /// Blazor circuits share it. The file is loaded lazily; a corrupt file is backed up and replaced by an empty store,
    /// while an unreadable (e.g. locked) file is never overwritten: reads return empty data and writes throw until it can be read.
    /// </summary>
    public sealed class TaskStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private readonly string _filePath;
        private readonly ILogger<TaskStore> _logger;
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private Dictionary<string, RepositoryTask>? _tasks;

        public TaskStore(string filePath, ILogger<TaskStore> logger)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            _filePath = Path.GetFullPath(filePath);
            _logger = logger;
        }

        public TaskStore(IOptions<AutoPilotSettings> options, ILogger<TaskStore> logger)
            : this(options.Value.ResolveTasksFile(), logger)
        {
        }

        /// <summary>Raised after a mutation was saved successfully. Handlers run on the mutating thread.</summary>
        public event Action? Changed;

        /// <summary>Full path of the tasks file.</summary>
        public string FilePath => _filePath;

        /// <summary>All tasks, oldest first.</summary>
        public IReadOnlyList<RepositoryTask> GetAll()
        {
            lock (_lock)
            {
                return EnsureLoaded() is { } tasks ? Sorted(tasks.Values) : [];
            }
        }

        /// <summary>Tasks of a repository (path compared case-insensitively), oldest first.</summary>
        public IReadOnlyList<RepositoryTask> GetForRepository(string repositoryPath)
        {
            if (string.IsNullOrWhiteSpace(repositoryPath))
            {
                return [];
            }

            var key = Normalize(repositoryPath);
            lock (_lock)
            {
                return EnsureLoaded() is { } tasks
                    ? Sorted(tasks.Values.Where(t => string.Equals(Normalize(t.RepositoryPath), key, StringComparison.OrdinalIgnoreCase)))
                    : [];
            }
        }

        public RepositoryTask? Get(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            lock (_lock)
            {
                return EnsureLoaded()?.GetValueOrDefault(id)?.Clone();
            }
        }

        /// <summary>The oldest pending (unsent) task of a repository with the same trimmed title (case-insensitive), or null.</summary>
        public RepositoryTask? FindPendingByTitle(string repositoryPath, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var trimmed = title.Trim();
            return GetForRepository(repositoryPath)
                .FirstOrDefault(t => !t.IsSent && string.Equals(t.Title.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        }

        public Task<RepositoryTask> CreateAsync(RepositoryInfo repository, string title, string? description, CancellationToken ct = default) =>
            CreateAsync(repository, title, description, createdBy: null, sourceSessionId: null, ct);

        /// <summary>Creates a task; <paramref name="createdBy"/> and <paramref name="sourceSessionId"/> mark it as agent-created.</summary>
        public async Task<RepositoryTask> CreateAsync(RepositoryInfo repository, string title, string? description, string? createdBy, string? sourceSessionId, CancellationToken ct = default) =>
            (await CreateCoreAsync(repository, title, description, createdBy, sourceSessionId, skipDuplicate: false, ct)).Task;

        /// <summary>
        /// Like <see cref="CreateAsync(RepositoryInfo, string, string?, string?, string?, CancellationToken)"/>, but atomically (under the write gate)
        /// creates nothing when a pending task with the same title already exists on the repository; that task is returned with Created = false.
        /// </summary>
        public Task<(RepositoryTask Task, bool Created)> CreateIfNoPendingDuplicateAsync(RepositoryInfo repository, string title, string? description, string? createdBy, string? sourceSessionId, CancellationToken ct = default) =>
            CreateCoreAsync(repository, title, description, createdBy, sourceSessionId, skipDuplicate: true, ct);

        private async Task<(RepositoryTask Task, bool Created)> CreateCoreAsync(RepositoryInfo repository, string title, string? description, string? createdBy, string? sourceSessionId, bool skipDuplicate, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentException.ThrowIfNullOrWhiteSpace(title);

            var task = new RepositoryTask
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryPath = repository.Path,
                RepositoryName = repository.Name,
                Title = title.Trim(),
                Description = NormalizeDescription(description),
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
                SourceSessionId = string.IsNullOrWhiteSpace(sourceSessionId) ? null : sourceSessionId,
            };

            RepositoryTask? duplicate = null;
            await MutateAsync(tasks =>
            {
                if (skipDuplicate)
                {
                    var key = Normalize(repository.Path);
                    duplicate = tasks.Values
                        .Where(t => !t.IsSent
                            && string.Equals(Normalize(t.RepositoryPath), key, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(t.Title.Trim(), task.Title, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(t => t.CreatedAt)
                        .FirstOrDefault()?.Clone();
                    if (duplicate is not null)
                    {
                        return false;
                    }
                }

                tasks[task.Id] = task;
                return true;
            }, ct);
            return duplicate is null ? (task.Clone(), true) : (duplicate, false);
        }

        /// <summary>Edits a pending task. Returns false when it is missing or already sent.</summary>
        public async Task<bool> UpdateAsync(string id, string title, string? description, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            var trimmed = title.Trim();
            var normalized = NormalizeDescription(description);

            return await MutateAsync(tasks =>
            {
                if (id is null || !tasks.TryGetValue(id, out var task) || task.IsSent)
                {
                    return false;
                }

                task.Title = trimmed;
                task.Description = normalized;
                return true;
            }, ct);
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
            await MutateAsync(tasks => id is not null && tasks.Remove(id), ct);

        /// <summary>Marks a pending task as sent to a conversation. Returns false when it is missing or already sent.</summary>
        public async Task<bool> MarkSentAsync(string id, string sessionId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

            return await MutateAsync(tasks =>
            {
                if (id is null || !tasks.TryGetValue(id, out var task) || task.IsSent)
                {
                    return false;
                }

                task.SentAt = DateTimeOffset.UtcNow;
                task.SessionId = sessionId;
                return true;
            }, ct);
        }

        /// <summary>
        /// Deletes all tasks sent to a conversation and clears the source conversation of the tasks it created (those are kept).
        /// Returns the number deleted.
        /// </summary>
        public async Task<int> DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return 0;
            }

            var count = 0;
            await MutateAsync(tasks =>
            {
                var ids = tasks.Values.Where(t => string.Equals(t.SessionId, sessionId, StringComparison.Ordinal)).Select(t => t.Id).ToList();
                foreach (var id in ids)
                {
                    tasks.Remove(id);
                }

                count = ids.Count;
                var cleared = false;
                foreach (var task in tasks.Values.Where(t => string.Equals(t.SourceSessionId, sessionId, StringComparison.Ordinal)))
                {
                    task.SourceSessionId = null;
                    cleared = true;
                }

                return count > 0 || cleared;
            }, ct);
            return count;
        }

        /// <summary>
        /// Applies <paramref name="mutation"/> to a copy of the tasks; when it returns true, persists the result,
        /// publishes it and raises Changed. Nothing is changed in memory when the write fails.
        /// </summary>
        private async Task<bool> MutateAsync(Func<Dictionary<string, RepositoryTask>, bool> mutation, CancellationToken ct)
        {
            await _writeGate.WaitAsync(ct);
            try
            {
                Dictionary<string, RepositoryTask> working;
                lock (_lock)
                {
                    var tasks = EnsureLoaded()
                        ?? throw new IOException($"The tasks file '{_filePath}' is unreadable; not overwriting it.");
                    working = tasks.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
                }

                if (!mutation(working))
                {
                    return false;
                }

                await WriteAsync(Sorted(working.Values), ct);
                lock (_lock)
                {
                    _tasks = working;
                }
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
                    _logger.LogDebug(ex, "Task store change handler failed.");
                }
            }

            return true;
        }

        private static List<RepositoryTask> Sorted(IEnumerable<RepositoryTask> tasks) =>
            [.. tasks.OrderBy(t => t.CreatedAt).Select(t => t.Clone())];

        private static string? NormalizeDescription(string? description) =>
            string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        // Must be called with _lock held.
        // Returns null while the file is unreadable (nothing is cached in that case).
        private Dictionary<string, RepositoryTask>? EnsureLoaded() => _tasks ??= Load();

        private Dictionary<string, RepositoryTask>? Load()
        {
            var tasks = new Dictionary<string, RepositoryTask>(StringComparer.Ordinal);
            if (!File.Exists(_filePath))
            {
                return tasks;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return tasks;
                }

                var file = JsonSerializer.Deserialize<TasksFile>(json, JsonOptions)
                    ?? throw new JsonException("The tasks file contains null.");
                var list = file.Tasks ?? throw new JsonException("The tasks file has no tasks list.");

                // Structurally wrong files are corrupt; individual null or id-less entries are skipped.
                var skipped = 0;
                foreach (var task in list)
                {
                    if (task is { HasId: true })
                    {
                        tasks[task.Id] = task;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                if (skipped > 0)
                {
                    _logger.LogWarning("Tasks file '{File}': skipped {Count} null or id-less task entries.", _filePath, skipped);
                }

                return tasks;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                var backup = $"{_filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                try
                {
                    File.Move(_filePath, backup, overwrite: true);
                    _logger.LogWarning(ex, "Tasks file '{File}' is corrupt; backed it up to '{Backup}' and started empty.", _filePath, backup);
                }
                catch (Exception moveEx)
                {
                    _logger.LogWarning(moveEx, "Tasks file '{File}' is corrupt ({Reason}) and could not be backed up; starting empty.", _filePath, ex.Message);
                }

                return new Dictionary<string, RepositoryTask>(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Tasks file '{File}' could not be read; will retry on next access.", _filePath);
                return null;
            }
        }

        private async Task WriteAsync(List<RepositoryTask> tasks, CancellationToken ct)
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
                    await JsonSerializer.SerializeAsync(stream, new TasksFile { Tasks = tasks }, JsonOptions, ct);
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

        private sealed class TasksFile
        {
            public int Version { get; set; } = 1;

            public List<RepositoryTask>? Tasks { get; set; } = [];
        }
    }
}
