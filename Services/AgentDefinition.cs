using GitHub.Copilot;

namespace AutoPilot.Services
{
    public sealed class AgentDefinition
    {
        public required string Name { get; init; }
        public required string DisplayName { get; init; }
        public required string Description { get; init; }
        public string? Model { get; init; }
        public string? ReasoningEffort { get; init; }

        /// <summary>Allowed tool names. <c>null</c> means all tools.</summary>
        public IReadOnlyList<string>? Tools { get; init; }

        public bool Infer { get; init; }
        public string? Writes { get; init; }
        public required string Prompt { get; init; }
        public required string SourcePath { get; init; }

        public CustomAgentConfig ToCustomAgentConfig() => new()
        {
            Name = Name,
            DisplayName = DisplayName,
            Description = Description,
            Prompt = Prompt,
            Tools = Tools is null ? null : [.. Tools],
            Infer = Infer,
            Model = Model,
            ReasoningEffort = ReasoningEffort,
        };
    }
}
