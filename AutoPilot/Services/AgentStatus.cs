using System.Text.RegularExpressions;

namespace AutoPilot.Services
{
    public enum AgentStatusKind { Success, Failure, Warning, Neutral }

    public static class AgentStatus
    {
        private static readonly Regex StatusLine = new(
            @"^\s*(?<wrap>\*\*|__|`)?\s*STATUS\s*:\s*(?<value>[A-Za-z0-9_]+)(?<rest>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> Success = new(StringComparer.OrdinalIgnoreCase)
        { "SUCCESS", "DONE", "PASS", "PASSED", "APPROVE", "APPROVED", "PUSHED", "CREATED" };

        private static readonly HashSet<string> Failure = new(StringComparer.OrdinalIgnoreCase)
        { "FAIL", "FAILED", "REQUEST_CHANGES", "ERROR" };

        private static readonly HashSet<string> Warning = new(StringComparer.OrdinalIgnoreCase)
        { "NEEDS_ATTENTION", "PARTIAL", "BLOCKED", "INCONCLUSIVE", "DECLINED", "DUPLICATE", "NOT_CONFIGURED" };

        public static bool TryExtract(string? text, out string status, out string remainder)
        {
            status = "";
            remainder = text ?? "";
            if (string.IsNullOrEmpty(text))
                return false;

            // Locate the first non-blank line.
            int start = 0;
            int end;
            while (true)
            {
                end = text.IndexOf('\n', start);
                int lineEnd = end < 0 ? text.Length : end;
                if (text.AsSpan(start, lineEnd - start).Trim().Length > 0)
                    break;
                if (end < 0)
                    return false;
                start = end + 1;
            }

            string line = text.Substring(start, (end < 0 ? text.Length : end) - start).TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                return false;

            var m = StatusLine.Match(line);
            if (!m.Success)
                return false;

            string rest = m.Groups["rest"].Value;
            string wrap = m.Groups["wrap"].Value;
            string value = m.Groups["value"].Value;
            if (wrap == "__" && value.Length > 2 && value.EndsWith("__", StringComparison.Ordinal))
            {
                // The closing "__" marker was swallowed by the value token.
                value = value.Substring(0, value.Length - 2);
            }
            else if (wrap.Length > 0)
            {
                string trimmed = rest.TrimStart();
                if (trimmed.StartsWith(wrap, StringComparison.Ordinal))
                    rest = trimmed.Substring(wrap.Length);
            }
            rest = rest.Trim().TrimStart(':', '-', '–', '—', ' ', '\t').Trim();

            string after = end < 0 ? "" : text.Substring(end + 1);
            remainder = rest.Length == 0
                ? after
                : (after.Length == 0 ? rest : rest + "\n" + after);
            status = value.ToUpperInvariant();
            return true;
        }

        public static AgentStatusKind Categorize(string status)
        {
            if (status is null) return AgentStatusKind.Neutral;
            if (Success.Contains(status)) return AgentStatusKind.Success;
            if (Failure.Contains(status)) return AgentStatusKind.Failure;
            if (Warning.Contains(status)) return AgentStatusKind.Warning;
            return AgentStatusKind.Neutral;
        }
    }
}
