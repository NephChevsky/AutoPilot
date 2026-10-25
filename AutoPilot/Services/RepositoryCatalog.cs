using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AutoPilot.Services
{
    /// <summary>A git repository found directly under the working directory.</summary>
    /// <param name="Name">Folder name of the repository.</param>
    /// <param name="Path">Full path of the repository's main directory.</param>
    public sealed record RepositoryInfo(string Name, string Path);

    /// <summary>
    /// Discovers the git repositories under <see cref="AutoPilotSettings.WorkingDirectory"/> and hands out one shared
    /// <see cref="ConversationWorktrees"/> per repository (so the per-repository git lock is shared across circuits).
    /// </summary>
    public sealed class RepositoryCatalog(IOptions<AutoPilotSettings> options, ILoggerFactory? loggerFactory = null)
    {
        private readonly ConcurrentDictionary<string, ConversationWorktrees> _worktrees = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Re-scans the working directory and returns its repositories, sorted by name. Never throws.</summary>
        public IReadOnlyList<RepositoryInfo> GetRepositories() => Discover(options.Value.ResolveWorkingDirectory());

        /// <summary>
        /// Immediate child folders of <paramref name="root"/> containing a <c>.git</c> entry (folder or file), sorted by name
        /// (case-insensitive). A missing or inaccessible root yields an empty list.
        /// </summary>
        public static IReadOnlyList<RepositoryInfo> Discover(string root)
        {
            var result = new List<RepositoryInfo>();
            try
            {
                if (!Directory.Exists(root))
                {
                    return result;
                }

                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        var gitEntry = Path.Combine(directory, ".git");
                        if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                        {
                            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            result.Add(new RepositoryInfo(Path.GetFileName(full), full));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Skip folders that cannot be inspected.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }

            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return result;
        }

        /// <summary>Worktree manager of <paramref name="repository"/>; the same instance is returned for the same repository path.</summary>
        public ConversationWorktrees GetWorktrees(RepositoryInfo repository)
        {
            ArgumentNullException.ThrowIfNull(repository);
            var settings = options.Value;
            return _worktrees.GetOrAdd(repository.Path, path => new ConversationWorktrees(
                path,
                settings.ResolveWorktreesDirectory(path),
                settings.BranchPrefix,
                loggerFactory?.CreateLogger<ConversationWorktrees>()));
        }
    }
}
