namespace AutoPilot.Services
{
    public enum ChatEntryKind
    {
        User,
        Assistant,
        Tool,
        Activity,
        Error,
    }

    public sealed class ChatEntry
    {
        public required ChatEntryKind Kind { get; init; }
        public required string Author { get; init; }
        public string Text { get; set; } = "";

        /// <summary>Message id or tool call id used to correlate streaming/completion events.</summary>
        public string? Key { get; init; }

        /// <summary>True when produced by a sub-agent rather than the orchestrator.</summary>
        public bool IsSubagent { get; init; }

        /// <summary>Tool outcome; <c>null</c> while running.</summary>
        public bool? Succeeded { get; set; }

        /// <summary>One-line argument summary for tool entries.</summary>
        public string? ToolSummary { get; set; }

        /// <summary>Pretty-printed JSON arguments for tool entries.</summary>
        public string? ToolArguments { get; set; }

        /// <summary>True when <see cref="ToolArguments"/> was cut to the size cap.</summary>
        public bool ToolArgumentsTruncated { get; set; }

        /// <summary>Result text (capped) once a tool completed successfully.</summary>
        public string? ToolResult { get; set; }

        /// <summary>True when <see cref="ToolResult"/> was cut to the size cap.</summary>
        public bool ToolResultTruncated { get; set; }

        /// <summary>Error message when a tool failed.</summary>
        public string? ToolError { get; set; }
    }
}
