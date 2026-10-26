using System.Text;
using System.Text.Json;

namespace AutoPilot.Services
{
    /// <summary>What was read from one session's <c>events.jsonl</c>.</summary>
    /// <param name="SessionId">Session id (from session.start, else the folder name).</param>
    /// <param name="WorkingDirectory">Working directory from session.start; null when unknown.</param>
    /// <param name="Cost">Cost of the last session.shutdown event; null when there is none or it has no usage.</param>
    /// <param name="MalformedLines">Number of lines skipped because they are not a JSON object (cheap structural check) or failed to parse.</param>
    public sealed record SessionEventsInfo(string SessionId, string? WorkingDirectory, SessionCost? Cost, int MalformedLines = 0);

    /// <summary>
    /// Read-only parser of the Copilot runtime's <c>events.jsonl</c> session files. The files are opened with full sharing so
    /// a session that is still writing is not disturbed. Malformed lines are skipped.
    /// </summary>
    public static class SessionEventsReader
    {
        private const string ShutdownPrefix = "\"type\":\"session.shutdown\"";
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>Returns the cost of the last shutdown event, or null when there is none, it has no usage, or the file is unreadable.</summary>
        public static SessionCost? TryReadCost(string eventsFile, string orchestratorName) =>
            TryReadSession(eventsFile, orchestratorName)?.Cost;

        /// <summary>
        /// Reads session id, working directory and cost of one events file. Throws on I/O errors
        /// (the caller decides how to report them); returns null only when the path is blank.
        /// </summary>
        public static SessionEventsInfo? TryReadSession(string eventsFile, string orchestratorName)
        {
            if (string.IsNullOrWhiteSpace(eventsFile))
            {
                return null;
            }

            var sessionId = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(eventsFile))) ?? "";
            string? cwd = null;
            DateTimeOffset? start = null;
            string? title = null;
            JsonElement? shutdown = null;
            DateTimeOffset? shutdownTime = null;
            JsonDocument? shutdownDocument = null;
            var malformed = 0;

            try
            {
                using var stream = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    var trimmedLine = line.AsSpan().Trim();
                    if (trimmedLine.IsEmpty)
                    {
                        continue;
                    }

                    if (trimmedLine[0] != '{' || trimmedLine[^1] != '}')
                    {
                        malformed++;
                        continue;
                    }

                    var isShutdown = line.Contains(ShutdownPrefix, StringComparison.Ordinal);
                    var isStart = line.StartsWith("{\"type\":\"session.start\"", StringComparison.Ordinal);
                    var isUser = title is null && line.StartsWith("{\"type\":\"user.message\"", StringComparison.Ordinal);
                    if (!isShutdown && !isStart && !isUser)
                    {
                        continue;
                    }

                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(line);
                    }
                    catch (JsonException)
                    {
                        malformed++;
                        continue;
                    }

                    var keep = false;
                    try
                    {
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement))
                        {
                            continue;
                        }

                        var type = typeElement.GetString();
                        root.TryGetProperty("data", out var data);
                        switch (type)
                        {
                            case "session.start" when start is null && data.ValueKind == JsonValueKind.Object:
                                if (data.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                                {
                                    sessionId = id.GetString()!;
                                }

                                start = GetTime(data, "startTime") ?? GetTime(root, "timestamp");
                                if (data.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object
                                    && context.TryGetProperty("cwd", out var cwdElement) && cwdElement.ValueKind == JsonValueKind.String)
                                {
                                    cwd = cwdElement.GetString();
                                }

                                break;
                            case "user.message" when title is null && data.ValueKind == JsonValueKind.Object:
                                if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                                {
                                    title = UsageAccumulator.Truncate(StripDateTimePrefix(content.GetString()));
                                }

                                break;
                            case "session.shutdown" when data.ValueKind == JsonValueKind.Object:
                                shutdownDocument?.Dispose();
                                shutdownDocument = document;
                                shutdown = data;
                                shutdownTime = GetTime(root, "timestamp");
                                keep = true;
                                break;
                        }
                    }
                    finally
                    {
                        if (!keep)
                        {
                            document.Dispose();
                        }
                    }
                }

                SessionCost? cost = null;
                if (shutdown is { } data2)
                {
                    cost = ToCost(sessionId, data2, orchestratorName, title, start, shutdownTime);
                }

                return new SessionEventsInfo(sessionId, cwd, cost, malformed);
            }
            finally
            {
                shutdownDocument?.Dispose();
            }
        }

        private static SessionCost? ToCost(string sessionId, JsonElement data, string orchestratorName, string? title, DateTimeOffset? start, DateTimeOffset? shutdownTime)
        {
            UsageMetricsData? metrics;
            try
            {
                metrics = data.Deserialize<UsageMetricsData>(JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return null;
            }

            if (metrics is null)
            {
                return null;
            }

            var snapshot = UsageAccumulator.ToSnapshot(metrics, orchestratorName);
            var lastUpdated = shutdownTime ?? start ?? DateTimeOffset.MinValue;
            var cost = UsageAccumulator.ToSessionCost(sessionId, snapshot, title, start ?? lastUpdated, lastUpdated);
            return UsageAccumulator.HasUsage(cost) ? cost : null;
        }

        private static DateTimeOffset? GetTime(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;

        private static string? StripDateTimePrefix(string? content)
        {
            const string open = "<current_datetime>";
            const string close = "</current_datetime>";
            if (content is null)
            {
                return null;
            }

            var trimmed = content.TrimStart();
            if (trimmed.StartsWith(open, StringComparison.Ordinal))
            {
                var end = trimmed.IndexOf(close, StringComparison.Ordinal);
                if (end >= 0)
                {
                    return trimmed[(end + close.Length)..];
                }
            }

            return content;
        }
    }
}
