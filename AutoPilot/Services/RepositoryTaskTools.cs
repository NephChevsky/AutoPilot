using System.ComponentModel;
using System.Text;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AutoPilot.Services
{
    /// <summary>
    /// Host-implemented orchestrator tools <c>list_repositories</c> and <c>create_repository_task</c>. One instance per conversation.
    /// Creating a task always asks the user for confirmation inside the tool, so the agent cannot skip it.
    /// </summary>
    /// <param name="tasks">The task store.</param>
    /// <param name="getRepositories">Returns the discovered repositories.</param>
    /// <param name="current">Repository of the conversation (default target of new tasks).</param>
    /// <param name="sessionId">Id of the requesting conversation.</param>
    /// <param name="createdBy">Name recorded as the creator of the tasks.</param>
    /// <param name="confirm">Asks the user a question with choices (freeform allowed) and returns the answer and whether it was freeform text.</param>
    public sealed class RepositoryTaskTools(
        TaskStore tasks,
        Func<IReadOnlyList<RepositoryInfo>> getRepositories,
        RepositoryInfo current,
        string sessionId,
        string createdBy,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<(string Answer, bool WasFreeform)>> confirm)
    {
        public const int MaxTitleLength = 200;
        public const int MaxDescriptionLength = 20_000;
        public const string CreateChoice = "Create task";
        public const string DeclineChoice = "Don't create";

        public IList<AIFunctionDeclaration> CreateTools() =>
        [
            CopilotTool.DefineTool(
                () => ListRepositories(),
                new CopilotToolOptions { SkipPermission = true },
                new AIFunctionFactoryOptions
                {
                    Name = "list_repositories",
                    Description = "Lists the repositories AutoPilot knows about and marks the one of the current conversation. Use the names with create_repository_task.",
                }),
            CopilotTool.DefineTool(
                ([Description("Short task title (max 200 characters).")] string title,
                 [Description("Self-contained task description (max 20,000 characters). Optional.")] string? description = null,
                 [Description("Name of the target repository (see list_repositories). Defaults to the current conversation's repository.")] string? repository = null,
                 CancellationToken cancellationToken = default) => CreateTaskAsync(title, description, repository, cancellationToken),
                new CopilotToolOptions { SkipPermission = true },
                new AIFunctionFactoryOptions
                {
                    Name = "create_repository_task",
                    Description = "Proposes a task for a repository (it appears in the Tasks panel, unsent). The user is asked to confirm each task; nothing is created without their confirmation. Returns STATUS: CREATED, DECLINED (with optional user feedback), DUPLICATE or FAILED.",
                }),
        ];

        /// <summary>Lists the repositories, marking the conversation's current one.</summary>
        public string ListRepositories()
        {
            IReadOnlyList<RepositoryInfo> repositories;
            try
            {
                repositories = getRepositories();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"STATUS: FAILED\nCould not list the repositories: {ex.Message}";
            }

            var builder = new StringBuilder("Repositories:");
            foreach (var repository in repositories)
            {
                var isCurrent = IsSame(repository, current);
                builder.Append("\n- ").Append(repository.Name).Append(isCurrent ? " (current)" : "");
            }

            if (repositories.Count == 0)
            {
                builder.Append("\n(none found)");
            }

            return builder.ToString();
        }

        /// <summary>Validates, asks the user to confirm and creates the task. Never throws except for cancellation of <paramref name="cancellationToken"/>.</summary>
        public async Task<string> CreateTaskAsync(string? title, string? description, string? repository, CancellationToken cancellationToken = default)
        {
            var trimmedTitle = CollapseWhitespace(title);
            if (trimmedTitle.Length == 0)
            {
                return Failed("The title must not be empty.");
            }

            if (trimmedTitle.Length > MaxTitleLength)
            {
                return Failed($"The title is too long ({trimmedTitle.Length} characters; maximum {MaxTitleLength}).");
            }

            if (description is { Length: > MaxDescriptionLength })
            {
                return Failed($"The description is too long ({description.Length} characters; maximum {MaxDescriptionLength}).");
            }

            RepositoryInfo target;
            try
            {
                var repositories = getRepositories();
                if (string.IsNullOrWhiteSpace(repository))
                {
                    target = current;
                }
                else
                {
                    var name = repository.Trim();
                    var match = repositories.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        var names = repositories.Count == 0 ? "(none)" : string.Join(", ", repositories.Select(r => r.Name));
                        return Failed($"Unknown repository '{name}'. Valid repositories: {names}");
                    }

                    target = match;
                }

                if (tasks.FindPendingByTitle(target.Path, trimmedTitle) is { } existing)
                {
                    return Duplicate(existing);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(ex.Message);
            }

            var question = $"Create task '{EscapeMarkdown(trimmedTitle)}' on repository '{EscapeMarkdown(target.Name)}'?";
            if (!string.IsNullOrWhiteSpace(description))
            {
                var quoted = description.Trim().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => "> " + l);
                question += "\n\n" + string.Join("\n", quoted);
            }

            (string Answer, bool WasFreeform) reply;
            try
            {
                reply = await confirm(question, [CreateChoice, DeclineChoice], cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed($"The confirmation was not answered ({ex.Message}); nothing was created.");
            }

            if (reply.WasFreeform || !string.Equals(reply.Answer?.Trim(), CreateChoice, StringComparison.Ordinal))
            {
                var declined = new StringBuilder("STATUS: DECLINED\nThe user did not confirm; nothing was created.");
                if (reply.WasFreeform && !string.IsNullOrWhiteSpace(reply.Answer))
                {
                    declined.Append("\nFeedback: ").Append(reply.Answer.Trim());
                }

                return declined.ToString();
            }

            try
            {
                var (task, created) = await tasks.CreateIfNoPendingDuplicateAsync(target, trimmedTitle, description, createdBy, sessionId, cancellationToken);
                if (!created)
                {
                    return Duplicate(task);
                }

                return $"STATUS: CREATED\nId: {task.Id}\nRepository: {task.RepositoryName}\nTitle: {task.Title}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed($"The task could not be saved: {ex.Message}");
            }
        }

        private static string Failed(string message) => $"STATUS: FAILED\n{message}\nNothing was created.";

        private static string Duplicate(RepositoryTask existing) =>
            $"STATUS: DUPLICATE\nId: {existing.Id}\nRepository: {existing.RepositoryName}\nTitle: {existing.Title}\nA pending task with this title already exists; nothing was created.";

        private static string CollapseWhitespace(string? value) =>
            string.IsNullOrEmpty(value) ? "" : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static string EscapeMarkdown(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if ("\\`*_{}[]()#+-.!|<>~&$".Contains(c))
                {
                    builder.Append('\\');
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private static bool IsSame(RepositoryInfo a, RepositoryInfo b) =>
            string.Equals(a.Path.TrimEnd('\\', '/'), b.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
