namespace AutoPilot.Services
{
    public sealed class AutoPilotSettings
    {
        public const string SectionName = "Settings";

        /// <summary>Repository the agents work on. Defaults to the process working directory.</summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>Folder where the Copilot runtime stores its data (sessions, config, state). Empty uses the runtime default (~/.copilot).</summary>
        public string CopilotDirectory { get; set; } = "";

        /// <summary>Folder containing the agent *.md files, relative to the content root.</summary>
        public string AgentsDirectory { get; set; } = "Agents";

        /// <summary>Name of the agent that starts every conversation.</summary>
        public string OrchestratorAgent { get; set; } = "orchestrator";

        /// <summary>Shell commands executed, in order, by the run_verification tool.</summary>
        public List<string> VerificationCommands { get; set; } = [];

        public int VerificationTimeoutSeconds { get; set; } = 900;

        public string ResolveWorkingDirectory() =>
            string.IsNullOrWhiteSpace(WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(WorkingDirectory);

        /// <summary>
        /// Path of the usage ledger JSON file. Empty uses <c>&lt;CopilotDirectory&gt;\usage\session-costs.json</c>, or
        /// <c>&lt;current directory&gt;\Copilot\usage\session-costs.json</c> when no CopilotDirectory is set.
        /// </summary>
        public string UsageFile { get; set; } = "";

        /// <summary>Resolves the usage ledger file path (see <see cref="UsageFile"/>).</summary>
        public string ResolveUsageFile()
        {
            if (!string.IsNullOrWhiteSpace(UsageFile))
            {
                return Path.GetFullPath(UsageFile);
            }

            var baseDirectory = ResolveCopilotDirectory() ?? Path.Combine(Directory.GetCurrentDirectory(), "Copilot");
            return Path.Combine(baseDirectory, "usage", "session-costs.json");
        }

        public string? ResolveCopilotDirectory() =>
            string.IsNullOrWhiteSpace(CopilotDirectory)
                ? null
                : Path.GetFullPath(CopilotDirectory);
    }
}
