using GitHub.Copilot;

namespace AutoPilot.Services
{
    /// <summary>
    /// Copilot's built-in sub-agents that AutoPilot sessions must not dispatch.
    /// This is a denylist because the SDK session config has no built-in allowlist; a custom agent
    /// sharing one of these names remains available per SDK semantics.
    /// </summary>
    public static class BuiltInAgents
    {
        public static IReadOnlyList<string> Excluded { get; } =
        [
            "explore",
            "task",
            "general-purpose",
            "code-review",
            "research",
            "security-review",
            "rubber-duck",
        ];

        /// <summary>Sets <see cref="SessionConfigBase.ExcludedBuiltInAgents"/> on the config and returns it.</summary>
        public static T Apply<T>(T config) where T : SessionConfigBase
        {
            config.ExcludedBuiltInAgents = [.. Excluded];
            return config;
        }
    }
}
