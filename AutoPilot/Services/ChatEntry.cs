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
    }
}
