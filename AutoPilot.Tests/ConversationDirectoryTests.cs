using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class ConversationDirectoryTests
    {
        private static readonly string Base = Path.Combine(Path.GetTempPath(), "cdtests");
        private static readonly string Main = Path.Combine(Base, "AutoPilot");
        private static readonly string Root = Path.Combine(Base, "AutoPilot.worktrees");

        private static bool Check(string? dir) => AgentChatSession.IsConversationDirectory(dir, Main, Root);

        [Fact]
        public void Main_directory_is_accepted() => Assert.True(Check(Main));

        [Fact]
        public void Main_directory_with_trailing_separator_is_accepted() => Assert.True(Check(Main + Path.DirectorySeparatorChar));

        [Fact]
        public void Main_directory_is_case_insensitive() => Assert.True(Check(Main.ToUpperInvariant()));

        [Fact]
        public void Worktree_subdirectory_is_accepted()
        {
            Assert.True(Check(Path.Combine(Root, "abc")));
            Assert.True(Check(Path.Combine(Root, "abc") + Path.DirectorySeparatorChar));
            Assert.True(Check(Path.Combine(Root, "ABC").Replace(Root, Root.ToUpperInvariant())));
        }

        [Fact]
        public void Worktrees_root_itself_is_rejected() => Assert.False(Check(Root));

        [Fact]
        public void Unrelated_directory_is_rejected() => Assert.False(Check(Path.Combine(Base, "Other")));

        [Fact]
        public void Sibling_with_common_prefix_is_rejected()
        {
            Assert.False(Check(Main + "X"));
            Assert.False(Check(Path.Combine(Base, "AutoPilot.worktrees2", "x")));
            Assert.False(Check(Path.Combine(Main, "sub")));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("bad\0path")]
        public void Null_empty_or_malformed_is_rejected_without_throwing(string? dir) => Assert.False(Check(dir));
    }
}
