namespace AutoPilot.Services
{
    /// <summary>Maps file paths to highlight.js language names for the diff viewer.</summary>
    public static class DiffLanguages
    {
        private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "csharp",
            [".razor"] = "xml",
            [".cshtml"] = "xml",
            [".xml"] = "xml",
            [".html"] = "xml",
            [".htm"] = "xml",
            [".csproj"] = "xml",
            [".props"] = "xml",
            [".targets"] = "xml",
            [".slnx"] = "xml",
            [".config"] = "xml",
            [".svg"] = "xml",
            [".js"] = "javascript",
            [".mjs"] = "javascript",
            [".cjs"] = "javascript",
            [".jsx"] = "javascript",
            [".ts"] = "typescript",
            [".tsx"] = "typescript",
            [".css"] = "css",
            [".scss"] = "scss",
            [".json"] = "json",
            [".yml"] = "yaml",
            [".yaml"] = "yaml",
            [".py"] = "python",
            [".ps1"] = "powershell",
            [".psm1"] = "powershell",
            [".sh"] = "bash",
            [".bash"] = "bash",
            [".sql"] = "sql",
            [".md"] = "markdown",
        };

        /// <summary>Returns the highlight.js language for <paramref name="path"/>, or null when unknown.</summary>
        public static string? FromPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var extension = Path.GetExtension(path);
            return ByExtension.TryGetValue(extension, out var language) ? language : null;
        }
    }
}
