using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class AgentStatusTests
    {
        [Theory]
        [InlineData("STATUS: DONE", "DONE")]
        [InlineData("   STATUS: DONE", "DONE")]
        [InlineData("\n\n  \nSTATUS: NEEDS_ATTENTION", "NEEDS_ATTENTION")]
        [InlineData("status: partial", "PARTIAL")]
        [InlineData("Status: Done", "DONE")]
        [InlineData("STATUS:DONE", "DONE")]
        [InlineData("STATUS   :   DONE", "DONE")]
        [InlineData("**STATUS: SUCCESS**", "SUCCESS")]
        [InlineData("__STATUS: SUCCESS__", "SUCCESS")]
        [InlineData("`STATUS: SUCCESS`", "SUCCESS")]
        [InlineData("STATUS: V2_OK", "V2_OK")]
        public void Extracts_status_with_empty_remainder(string text, string expected)
        {
            Assert.True(AgentStatus.TryExtract(text, out var status, out var remainder));
            Assert.Equal(expected, status);
            Assert.Equal("", remainder);
        }

        [Theory]
        [InlineData("STATUS: FAILED – build broke", "build broke")]
        [InlineData("STATUS: FAILED - build broke", "build broke")]
        [InlineData("STATUS: FAILED: build broke", "build broke")]
        [InlineData("STATUS: FAILED build broke", "build broke")]
        [InlineData("**STATUS: FAILED** – build broke", "build broke")]
        public void Keeps_trailing_text_in_remainder(string text, string expectedRemainder)
        {
            Assert.True(AgentStatus.TryExtract(text, out var status, out var remainder));
            Assert.Equal("FAILED", status);
            Assert.Equal(expectedRemainder, remainder);
        }

        [Fact]
        public void Wrapper_markers_do_not_leak()
        {
            Assert.True(AgentStatus.TryExtract("**STATUS: DONE**\nBody", out _, out var remainder));
            Assert.Equal("Body", remainder);
        }

        [Fact]
        public void Remainder_contains_following_lines()
        {
            Assert.True(AgentStatus.TryExtract("STATUS: DONE\n\nLine 1\nLine 2", out var status, out var remainder));
            Assert.Equal("DONE", status);
            Assert.Equal("\nLine 1\nLine 2", remainder);
        }

        [Fact]
        public void Trailing_text_precedes_following_lines()
        {
            Assert.True(AgentStatus.TryExtract("STATUS: FAILED – boom\nDetails", out _, out var remainder));
            Assert.Equal("boom\nDetails", remainder);
        }

        [Theory]
        [InlineData("Hello world")]
        [InlineData("Intro\nSTATUS: DONE")]
        [InlineData("Note: STATUS: DONE")]
        [InlineData("```\nSTATUS: DONE\n```")]
        [InlineData("```STATUS: DONE")]
        [InlineData("STATUS: ")]
        [InlineData("STATUS DONE")]
        [InlineData("")]
        [InlineData("   \n  ")]
        public void Returns_false_when_no_leading_status(string text)
        {
            Assert.False(AgentStatus.TryExtract(text, out var status, out var remainder));
            Assert.Equal("", status);
            Assert.Equal(text, remainder);
        }

        [Fact]
        public void Null_returns_false_and_empty()
        {
            Assert.False(AgentStatus.TryExtract(null, out var status, out var remainder));
            Assert.Equal("", status);
            Assert.Equal("", remainder);
        }

        [Theory]
        [InlineData("SUCCESS", AgentStatusKind.Success)]
        [InlineData("DONE", AgentStatusKind.Success)]
        [InlineData("PASS", AgentStatusKind.Success)]
        [InlineData("PASSED", AgentStatusKind.Success)]
        [InlineData("APPROVE", AgentStatusKind.Success)]
        [InlineData("APPROVED", AgentStatusKind.Success)]
        [InlineData("PUSHED", AgentStatusKind.Success)]
        [InlineData("CREATED", AgentStatusKind.Success)]
        [InlineData("FAIL", AgentStatusKind.Failure)]
        [InlineData("FAILED", AgentStatusKind.Failure)]
        [InlineData("REQUEST_CHANGES", AgentStatusKind.Failure)]
        [InlineData("ERROR", AgentStatusKind.Failure)]
        [InlineData("NEEDS_ATTENTION", AgentStatusKind.Warning)]
        [InlineData("PARTIAL", AgentStatusKind.Warning)]
        [InlineData("BLOCKED", AgentStatusKind.Warning)]
        [InlineData("INCONCLUSIVE", AgentStatusKind.Warning)]
        [InlineData("DECLINED", AgentStatusKind.Warning)]
        [InlineData("DUPLICATE", AgentStatusKind.Warning)]
        [InlineData("NOT_CONFIGURED", AgentStatusKind.Warning)]
        [InlineData("UNKNOWN", AgentStatusKind.Neutral)]
        [InlineData("", AgentStatusKind.Neutral)]
        [InlineData("done", AgentStatusKind.Success)]
        [InlineData("Failed", AgentStatusKind.Failure)]
        [InlineData("needs_attention", AgentStatusKind.Warning)]
        public void Categorizes(string status, AgentStatusKind expected)
        {
            Assert.Equal(expected, AgentStatus.Categorize(status));
        }
    }
}
