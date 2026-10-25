using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class AgentColorsTests
    {
        [Fact]
        public void Catalog_agents_get_distinct_slots_by_position()
        {
            var colors = new AgentColors(["Alpha", "Beta", "Gamma"]);

            Assert.Equal(0, colors.SlotFor("Alpha"));
            Assert.Equal(1, colors.SlotFor("Beta"));
            Assert.Equal(2, colors.SlotFor("Gamma"));
            Assert.Equal("agent-color-1", colors.CssClassFor("Beta"));
        }

        [Fact]
        public void Match_is_case_insensitive_and_ignores_extra_spaces()
        {
            var colors = new AgentColors(["Alpha", "Beta Tester"]);

            Assert.Equal(1, colors.SlotFor("  beta   TESTER "));
        }

        [Fact]
        public void Slots_wrap_around_palette()
        {
            var names = Enumerable.Range(0, AgentColors.PaletteSize + 2).Select(i => "Agent" + i).ToList();
            var colors = new AgentColors(names);

            Assert.Equal(0, colors.SlotFor("Agent" + AgentColors.PaletteSize));
            Assert.Equal(1, colors.SlotFor("Agent" + (AgentColors.PaletteSize + 1)));
        }

        [Fact]
        public void Duplicates_and_blank_names_are_ignored()
        {
            var colors = new AgentColors(["Alpha", "", "  ", "ALPHA", "Beta"]);

            Assert.Equal(0, colors.SlotFor("Alpha"));
            Assert.Equal(1, colors.SlotFor("Beta"));
        }

        [Fact]
        public void Unknown_authors_use_stable_hash_in_range()
        {
            var a = new AgentColors([]);
            var b = new AgentColors(["Alpha"]);

            foreach (var name in new[] { "explore", "Sub-agent", "task", "x" })
            {
                var slot = a.SlotFor(name);
                Assert.InRange(slot, 0, AgentColors.PaletteSize - 1);
                Assert.Equal(slot, b.SlotFor(name));
                Assert.Equal(slot, a.SlotFor(" " + name.ToUpperInvariant() + " "));
            }

            Assert.Equal(4, a.SlotFor("explore"));
        }

        [Fact]
        public void Null_or_empty_author_does_not_throw()
        {
            var colors = new AgentColors(["Alpha"]);

            Assert.InRange(colors.SlotFor(null!), 0, AgentColors.PaletteSize - 1);
            Assert.InRange(colors.SlotFor(""), 0, AgentColors.PaletteSize - 1);
        }
    }
}
