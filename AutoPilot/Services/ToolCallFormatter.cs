using System.Text.Json;

namespace AutoPilot.Services
{
    /// <summary>Formats tool call arguments and results for display.</summary>
    public static class ToolCallFormatter
    {
        public const int MaxSummaryLength = 120;
        public const int MaxResultLength = 4000;
        public const int MaxArgumentsLength = 20000;

        private const int RawPrefixLength = 4 * MaxSummaryLength;

        private static readonly string[] PreferredKeys = ["path", "pattern", "command", "query", "url", "intent", "description"];

        // Relaxed escaping is safe because the output is only ever HTML-encoded by Blazor; never render it as markup.
        private static readonly JsonSerializerOptions PrettyOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Converts arbitrary arguments to a JsonElement; null when absent or unserializable.</summary>
        public static JsonElement? ToJson(object? arguments)
        {
            if (arguments is null)
            {
                return null;
            }

            try
            {
                var element = arguments is JsonElement e ? e : JsonSerializer.SerializeToElement(arguments);
                return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>Pretty-printed JSON, or null when there are no arguments.</summary>
        public static string? FormatArguments(object? arguments) => FormatArguments(arguments, out _);

        /// <summary>Pretty-printed JSON capped at <see cref="MaxArgumentsLength"/>; <paramref name="truncated"/> tells whether it was cut.</summary>
        public static string? FormatArguments(object? arguments, out bool truncated)
        {
            truncated = false;
            var json = ToJson(arguments);
            if (json is null)
            {
                return null;
            }

            var element = json.Value;
            if (element.ValueKind == JsonValueKind.Object && !element.EnumerateObject().Any())
            {
                return null;
            }

            return Cap(JsonSerializer.Serialize(element, PrettyOptions), MaxArgumentsLength, out truncated);
        }

        /// <summary>One-line summary of the arguments, or null when there are none.</summary>
        public static string? Summarize(object? arguments)
        {
            var json = ToJson(arguments);
            if (json is null)
            {
                return null;
            }

            var element = json.Value;
            string? raw = null;
            if (element.ValueKind == JsonValueKind.Object)
            {
                var properties = element.EnumerateObject().ToList();
                foreach (var key in PreferredKeys)
                {
                    var match = properties.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);
                    if (match.Value.ValueKind != JsonValueKind.Undefined && !string.IsNullOrWhiteSpace(Render(match.Value)))
                    {
                        raw = Render(match.Value);
                        break;
                    }
                }

                if (raw is null)
                {
                    var first = properties.FirstOrDefault(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                        && !string.IsNullOrWhiteSpace(Render(p.Value)));
                    raw = first.Value.ValueKind == JsonValueKind.Undefined ? null : Render(first.Value);
                }

                if (raw is null && properties.Count > 0)
                {
                    raw = properties[0].Value.GetRawText();
                }
            }
            else
            {
                raw = Render(element);
            }

            if (raw is not null && raw.Length > RawPrefixLength)
            {
                raw = raw[..SafeCut(raw, RawPrefixLength)];
            }

            return string.IsNullOrWhiteSpace(raw) ? null : Truncate(CollapseWhitespace(raw), MaxSummaryLength);
        }

        /// <summary>Caps text at <paramref name="max"/> characters, appending an ellipsis.</summary>
        public static string Truncate(string text, int max) =>
            text.Length <= max ? text : string.Concat(text.AsSpan(0, SafeCut(text, max - 1)), "…");

        /// <summary>Caps result text; <paramref name="truncated"/> tells whether it was cut.</summary>
        public static string? CapResult(string? text, out bool truncated) =>
            Cap(text, MaxResultLength, out truncated);

        /// <summary>Caps an error message, appending an ellipsis when cut.</summary>
        public static string? CapError(string? text)
        {
            var capped = Cap(text, MaxResultLength, out var truncated);
            return truncated ? capped + "…" : capped;
        }

        private static string? Cap(string? text, int max, out bool truncated)
        {
            truncated = false;
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (text.Length > max)
            {
                truncated = true;
                return text[..SafeCut(text, max)];
            }

            return text;
        }

        private static int SafeCut(string text, int length) =>
            length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;

        private static string Render(JsonElement value) =>
            value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

        private static string CollapseWhitespace(string text) =>
            string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
