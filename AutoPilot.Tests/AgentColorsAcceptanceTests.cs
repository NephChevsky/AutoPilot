using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class AgentColorsAcceptanceTests
    {
        private static readonly string[] Names =
            ["Architect", "Coder", "Tester", "Reviewer", "Planner", "Writer", "Analyst", "Ops", "Ninth", "Tenth"];

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutoPilot.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }

        [Fact]
        public void Palette_size_is_at_least_8()
        {
            Assert.True(AgentColors.PaletteSize >= 8);
        }

        [Fact]
        public void First_eight_catalog_names_get_distinct_slots_by_position()
        {
            var c = new AgentColors(Names);
            for (int i = 0; i < 8; i++) Assert.Equal(i % AgentColors.PaletteSize, c.SlotFor(Names[i]));
            Assert.Equal(8, Names.Take(8).Select(c.SlotFor).Distinct().Count());
        }

        [Fact]
        public void Wraps_around_after_palette()
        {
            var c = new AgentColors(Names);
            Assert.Equal(c.SlotFor(Names[0]), c.SlotFor(Names[AgentColors.PaletteSize]));
            Assert.Equal(c.SlotFor(Names[1]), c.SlotFor(Names[AgentColors.PaletteSize + 1]));
        }

        [Theory]
        [InlineData("coder")]
        [InlineData("CODER")]
        [InlineData("  Coder  ")]
        [InlineData("\tcOdEr\t")]
        public void Catalog_match_is_case_and_space_insensitive(string author)
        {
            var c = new AgentColors(Names);
            Assert.Equal(1, c.SlotFor(author));
            Assert.Equal("agent-color-1", c.CssClassFor(author));
        }

        [Fact]
        public void Independent_instances_agree()
        {
            var a = new AgentColors(Names);
            var b = new AgentColors(Names.ToArray());
            foreach (var n in Names.Concat(["explore", "Sub-agent", "zzz"]))
                Assert.Equal(a.SlotFor(n), b.SlotFor(n));
        }

        [Theory]
        [InlineData("explore")]
        [InlineData("Sub-agent")]
        [InlineData("task")]
        [InlineData("something else entirely")]
        public void Unknown_names_are_deterministic_and_in_range(string name)
        {
            var c = new AgentColors(Names);
            var s = c.SlotFor(name);
            Assert.InRange(s, 0, AgentColors.PaletteSize - 1);
            Assert.Equal(s, c.SlotFor(name));
            Assert.Equal(s, new AgentColors([]).SlotFor(name));
            Assert.Equal(s, c.SlotFor("  " + name.ToUpperInvariant() + " ") is var t ? t : s);
        }

        [Fact]
        public void Unknown_names_spread_over_multiple_slots()
        {
            var c = new AgentColors([]);
            var slots = Enumerable.Range(0, 50).Select(i => c.SlotFor("agent-" + i)).Distinct().Count();
            Assert.True(slots >= 4, $"only {slots} distinct slots");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Null_or_empty_author_does_not_throw(string? author)
        {
            var c = new AgentColors(Names);
            var s = c.SlotFor(author!);
            Assert.InRange(s, 0, AgentColors.PaletteSize - 1);
            Assert.Equal("agent-color-" + s, c.CssClassFor(author!));
        }

        [Fact]
        public void Empty_catalog_does_not_throw()
        {
            var c = new AgentColors([]);
            Assert.InRange(c.SlotFor("x"), 0, AgentColors.PaletteSize - 1);
        }

        [Fact]
        public void CssClass_format()
        {
            var c = new AgentColors(Names);
            foreach (var n in Names)
                Assert.Equal("agent-color-" + c.SlotFor(n), c.CssClassFor(n));
        }

        [Fact]
        public void Css_defines_all_palette_classes()
        {
            var css = File.ReadAllText(Path.Combine(Root(), "AutoPilot", "Components", "Chat", "ChatBot.razor.css"));
            for (int i = 0; i < AgentColors.PaletteSize; i++)
                Assert.Contains($"agent-color-{i}", css);
        }

        [Fact]
        public void Razor_applies_color_class_to_assistant_and_tool_entries()
        {
            var razor = File.ReadAllText(Path.Combine(Root(), "AutoPilot", "Components", "Chat", "ChatBot.razor"));
            Assert.Matches(@"entry assistant[^""]*CssClassFor", razor);
            Assert.Matches(@"entry tool[^""]*CssClassFor", razor);
        }
    }
}
