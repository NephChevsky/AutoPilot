using System.Text.Json.Serialization;

namespace AutoPilot.Services
{
    /// <summary>Usage figures shared by sessions, models and agents.</summary>
    public class UsageFigures
    {
        /// <summary>Number of model requests.</summary>
        public double Requests { get; set; }

        /// <summary>Consumed AI credits in nano AIU (1e-9 AI credit).</summary>
        public double NanoAiu { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CacheReadTokens { get; set; }

        public long CacheWriteTokens { get; set; }

        public long ReasoningTokens { get; set; }

        [JsonIgnore]
        public double AiCredits => NanoAiu / UsageAccumulator.NanoPerCredit;
    }

    /// <summary>Cost of one model within a session.</summary>
    public sealed class ModelCost : UsageFigures;

    /// <summary>Cost of one agent (aggregated by agent name) within a session.</summary>
    public sealed class AgentCost : UsageFigures
    {
        public string? DisplayName { get; set; }
    }

    /// <summary>Persisted cost record of one Copilot session.</summary>
    public sealed class SessionCost
    {
        public string SessionId { get; set; } = "";

        /// <summary>First user prompt, truncated; may be null.</summary>
        public string? Title { get; set; }

        public DateTimeOffset FirstSeen { get; set; }

        public DateTimeOffset LastUpdated { get; set; }

        public double NanoAiu { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CacheReadTokens { get; set; }

        public long CacheWriteTokens { get; set; }

        public long ReasoningTokens { get; set; }

        /// <summary>Per-model figures keyed by model id.</summary>
        public Dictionary<string, ModelCost> Models { get; set; } = [];

        /// <summary>Per-agent figures keyed by agent name.</summary>
        public Dictionary<string, AgentCost> Agents { get; set; } = [];

        [JsonIgnore]
        public double AiCredits => NanoAiu / UsageAccumulator.NanoPerCredit;
    }

    /// <summary>Sums across all recorded sessions.</summary>
    public sealed record CostTotals(
        int SessionCount,
        double NanoAiu,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long ReasoningTokens)
    {
        public double AiCredits => NanoAiu / UsageAccumulator.NanoPerCredit;
    }

    /// <summary>SDK-independent copy of the session metrics at one point in time. Agents are keyed by agent name.</summary>
    public sealed class UsageSnapshot
    {
        public static UsageSnapshot Zero => new();

        public double NanoAiu { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CacheReadTokens { get; set; }

        public long CacheWriteTokens { get; set; }

        public long ReasoningTokens { get; set; }

        public Dictionary<string, ModelCost> Models { get; set; } = [];

        public Dictionary<string, AgentCost> Agents { get; set; } = [];
    }
}
