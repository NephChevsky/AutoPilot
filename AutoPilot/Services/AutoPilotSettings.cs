namespace AutoPilot.Services
{
    public sealed class AutoPilotSettings
    {
        public const string SectionName = "Settings";

        /// <summary>Parent folder containing the git repositories the agents work on (each immediate child folder with a <c>.git</c> entry is a repository). Defaults to the process working directory.</summary>
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

        /// <summary>Folder holding the conversation worktrees, in one subfolder per repository (<c>&lt;WorktreesDirectory&gt;\&lt;repository&gt;\&lt;sessionId&gt;</c>). Empty uses, for each repository, the sibling folder <c>&lt;repository&gt;.worktrees</c> (outside the repository).</summary>
        public string WorktreesDirectory { get; set; } = "";

        /// <summary>Prefix of the branch created for each conversation; the session id is appended.</summary>
        public string BranchPrefix { get; set; } = "autopilot/";

        /// <summary>Name of the git remote the commit_and_push tool pushes to.</summary>
        public string GitRemote { get; set; } = "origin";

        /// <summary>Resolves the worktrees root as if the working directory itself were a repository; a configured <see cref="WorktreesDirectory"/> is returned as-is. Prefer <see cref="ResolveWorktreesDirectory(string)"/>.</summary>
        public string ResolveWorktreesDirectory() =>
            string.IsNullOrWhiteSpace(WorktreesDirectory)
                ? ResolveWorktreesDirectory(ResolveWorkingDirectory())
                : Path.GetFullPath(WorktreesDirectory);

        /// <summary>
        /// Resolves the folder holding the conversation worktrees of one repository: <c>&lt;repository&gt;.worktrees</c> by default,
        /// or <c>&lt;WorktreesDirectory&gt;\&lt;repository folder name&gt;</c> when <see cref="WorktreesDirectory"/> is configured.
        /// </summary>
        public string ResolveWorktreesDirectory(string repositoryPath)
        {
            var repository = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(WorktreesDirectory)
                ? repository + ".worktrees"
                : Path.Combine(Path.GetFullPath(WorktreesDirectory), Path.GetFileName(repository));
        }

        public string ResolveWorkingDirectory() =>
            string.IsNullOrWhiteSpace(WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(WorkingDirectory);

        /// <summary>
        /// Folder where the Copilot runtime keeps its session files (<c>&lt;base&gt;\session-state</c>), where base is the
        /// configured <see cref="CopilotDirectory"/>, else <c>%USERPROFILE%\.copilot</c>.
        /// </summary>
        public string ResolveSessionStateDirectory() =>
            Path.Combine(
                ResolveCopilotDirectory() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot"),
                "session-state");

        /// <summary>
        /// Path of the tasks JSON file. Empty uses <c>&lt;CopilotDirectory&gt;\tasks\tasks.json</c>, or
        /// <c>&lt;current directory&gt;\Copilot\tasks\tasks.json</c> when no CopilotDirectory is set.
        /// </summary>
        public string TasksFile { get; set; } = "";

        /// <summary>Resolves the tasks file path (see <see cref="TasksFile"/>).</summary>
        public string ResolveTasksFile()
        {
            if (!string.IsNullOrWhiteSpace(TasksFile))
            {
                return Path.GetFullPath(TasksFile);
            }

            var baseDirectory = ResolveCopilotDirectory() ?? Path.Combine(Directory.GetCurrentDirectory(), "Copilot");
            return Path.Combine(baseDirectory, "tasks", "tasks.json");
        }

        public string? ResolveCopilotDirectory() =>
            string.IsNullOrWhiteSpace(CopilotDirectory)
                ? null
                : Path.GetFullPath(CopilotDirectory);
    }
}
