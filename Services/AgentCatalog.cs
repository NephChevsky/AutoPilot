using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AutoPilot.Services
{
    /// <summary>Loads agent definitions from markdown files with a YAML front matter header.</summary>
    public sealed class AgentCatalog(IOptions<AutoPilotSettings> options, IHostEnvironment environment)
    {
        private const string FrontMatterDelimiter = "---";

        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public string AgentsDirectory =>
            Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.AgentsDirectory));

        public IReadOnlyList<AgentDefinition> LoadAgents()
        {
            var directory = AgentsDirectory;
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"Agents directory '{directory}' does not exist.");
            }

            var agents = Directory.EnumerateFiles(directory, "*.md")
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(Parse)
                .ToList();

            var duplicate = agents.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException($"Duplicate agent name '{duplicate.Key}' in '{directory}'.");
            }

            return agents;
        }

        public static AgentDefinition Parse(string path)
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != FrontMatterDelimiter)
            {
                throw new FormatException($"Agent file '{path}' must start with a '---' YAML front matter block.");
            }

            var end = Array.FindIndex(lines, 1, l => l.Trim() == FrontMatterDelimiter);
            if (end < 0)
            {
                throw new FormatException($"Agent file '{path}' has an unterminated YAML front matter block.");
            }

            var yaml = string.Join('\n', lines[1..end]);
            var frontMatter = Deserializer.Deserialize<FrontMatter?>(yaml) ?? new FrontMatter();
            var prompt = string.Join('\n', lines[(end + 1)..]).Trim();
            var name = string.IsNullOrWhiteSpace(frontMatter.Name) ? Path.GetFileNameWithoutExtension(path) : frontMatter.Name.Trim();

            return new AgentDefinition
            {
                Name = name,
                DisplayName = string.IsNullOrWhiteSpace(frontMatter.DisplayName) ? name : frontMatter.DisplayName.Trim(),
                Description = frontMatter.Description?.Trim() ?? "",
                Model = NullIfEmpty(frontMatter.Model),
                ReasoningEffort = NullIfEmpty(frontMatter.ReasoningEffort),
                Tools = frontMatter.Tools,
                Infer = frontMatter.Infer ?? true,
                Writes = NullIfEmpty(frontMatter.Writes),
                Prompt = prompt,
                SourcePath = path,
            };
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private sealed class FrontMatter
        {
            public string? Name { get; set; }
            public string? DisplayName { get; set; }
            public string? Description { get; set; }
            public string? Model { get; set; }
            public string? ReasoningEffort { get; set; }
            public List<string>? Tools { get; set; }
            public bool? Infer { get; set; }
            public string? Writes { get; set; }
        }
    }
}
