using System.Text.RegularExpressions;

namespace AutoPilot.Services
{
    public sealed partial class AgentColors
    {
        public const int PaletteSize = 8;

        private readonly Dictionary<string, int> _slots = [];

        public AgentColors(IEnumerable<string> agentNames)
        {
            var index = 0;
            foreach (var name in agentNames)
            {
                var key = Normalize(name);
                if (key.Length == 0)
                {
                    continue;
                }

                if (_slots.TryAdd(key, index % PaletteSize))
                {
                    index++;
                }
            }
        }

        public int SlotFor(string author)
        {
            var key = Normalize(author);
            if (_slots.TryGetValue(key, out var slot))
            {
                return slot;
            }

            var hash = 2166136261u;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(key))
            {
                hash ^= b;
                hash *= 16777619u;
            }

            return (int)(hash % PaletteSize);
        }

        public string CssClassFor(string author) => "agent-color-" + SlotFor(author);

        private static string Normalize(string name) =>
            string.IsNullOrWhiteSpace(name) ? "" : Spaces().Replace(name.Trim(), " ").ToLowerInvariant();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Spaces();
    }
}
