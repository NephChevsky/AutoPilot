using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class AgentStatusAcceptanceTests
    {
        // ---- AC-1
        [Theory]
        [InlineData("STATUS: DONE\nAll good", "DONE", "All good")]
        [InlineData("STATUS: NEEDS_ATTENTION\r\nLook\r\nmore", "NEEDS_ATTENTION", "Look\r\nmore")]
        [InlineData("\n\n  \nSTATUS: PASS_2\nbody", "PASS_2", "body")]
        public void Extracts_status_and_removes_line(string input, string status, string rest)
        {
            Assert.True(AgentStatus.TryExtract(input, out var s, out var r));
            Assert.Equal(status, s);
            Assert.Equal(rest, r.TrimStart('\r', '\n'));
            Assert.DoesNotContain("STATUS", MarkdownRenderer.ToHtml(r));
        }

        [Fact]
        public void Status_only_message_has_empty_body()
        {
            Assert.True(AgentStatus.TryExtract("STATUS: DONE", out var s, out var r));
            Assert.Equal("DONE", s);
            Assert.Equal("", r.Trim());
        }

        // ---- AC-2
        [Theory]
        [InlineData("   STATUS: DONE\nx", "DONE")]
        [InlineData("status: done\nx", "DONE")]
        [InlineData("Status: Failed\nx", "FAILED")]
        [InlineData("STATUS : DONE\nx", "DONE")]
        [InlineData("STATUS :DONE\nx", "DONE")]
        [InlineData("STATUS:DONE\nx", "DONE")]
        [InlineData("**STATUS: DONE**\nx", "DONE")]
        [InlineData("__STATUS: DONE__\nx", "DONE")]
        [InlineData("`STATUS: DONE`\nx", "DONE")]
        public void Tolerates_variants(string input, string status)
        {
            Assert.True(AgentStatus.TryExtract(input, out var s, out var r));
            Assert.Equal(status, s);
            Assert.DoesNotContain("STATUS", r, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("**", r);
            Assert.DoesNotContain("__", r);
            Assert.DoesNotContain("`", r);
            Assert.Contains("x", r);
        }

        [Theory]
        [InlineData("STATUS: FAILED – build broke", "build broke")]
        [InlineData("STATUS: FAILED - build broke", "build broke")]
        [InlineData("STATUS: FAILED: build broke", "build broke")]
        public void Trailing_text_stays_in_body(string input, string tail)
        {
            Assert.True(AgentStatus.TryExtract(input + "\nnext", out var s, out var r));
            Assert.Equal("FAILED", s);
            var firstLine = r.TrimStart('\r', '\n').Split('\n')[0].TrimEnd('\r');
            Assert.Equal(tail, firstLine);
            Assert.Contains("next", r);
        }

        // ---- AC-3
        [Theory]
        [InlineData("SUCCESS")] [InlineData("DONE")] [InlineData("PASS")] [InlineData("PASSED")]
        [InlineData("APPROVE")] [InlineData("APPROVED")] [InlineData("PUSHED")] [InlineData("CREATED")]
        public void Success(string v)
        {
            Assert.Equal(AgentStatusKind.Success, AgentStatus.Categorize(v));
            Assert.Equal(AgentStatusKind.Success, AgentStatus.Categorize(v.ToLowerInvariant()));
        }

        [Theory]
        [InlineData("FAIL")] [InlineData("FAILED")] [InlineData("REQUEST_CHANGES")] [InlineData("ERROR")]
        public void Failure(string v)
        {
            Assert.Equal(AgentStatusKind.Failure, AgentStatus.Categorize(v));
            Assert.Equal(AgentStatusKind.Failure, AgentStatus.Categorize(v.ToLowerInvariant()));
        }

        [Theory]
        [InlineData("NEEDS_ATTENTION")] [InlineData("PARTIAL")] [InlineData("BLOCKED")]
        [InlineData("INCONCLUSIVE")] [InlineData("DECLINED")] [InlineData("DUPLICATE")] [InlineData("NOT_CONFIGURED")]
        public void Warning(string v)
        {
            Assert.Equal(AgentStatusKind.Warning, AgentStatus.Categorize(v));
            Assert.Equal(AgentStatusKind.Warning, AgentStatus.Categorize(v.ToLowerInvariant()));
        }

        [Theory]
        [InlineData("WHATEVER")] [InlineData("")] [InlineData("DONE_ISH")]
        public void Neutral(string v) => Assert.Equal(AgentStatusKind.Neutral, AgentStatus.Categorize(v));

        [Fact]
        public void Unknown_status_extracted_and_neutral()
        {
            Assert.True(AgentStatus.TryExtract("STATUS: SOMETHING_ELSE\nx", out var s, out _));
            Assert.Equal("SOMETHING_ELSE", s);
            Assert.Equal(AgentStatusKind.Neutral, AgentStatus.Categorize(s));
        }

        // ---- AC-4
        [Theory]
        [InlineData("Hello world")]
        [InlineData("Intro\nSTATUS: DONE")]
        [InlineData("```\nSTATUS: DONE\n```")]
        [InlineData("```text\nSTATUS: DONE\nmore\n```\nafter")]
        [InlineData("")]
        [InlineData("   \n  ")]
        [InlineData("STATUS DONE")]
        [InlineData("STATUS:")]
        [InlineData("STATUSES: DONE")]
        [InlineData("Not STATUS: DONE")]
        public void Non_status_messages_unchanged(string input)
        {
            Assert.False(AgentStatus.TryExtract(input, out var s, out var r));
            Assert.Equal("", s);
            Assert.Equal(input, r);
            Assert.Equal(MarkdownRenderer.ToHtml(input), MarkdownRenderer.ToHtml(r));
        }

        [Fact]
        public void Null_input()
        {
            Assert.False(AgentStatus.TryExtract(null, out var s, out var r));
            Assert.Equal("", s);
            Assert.Equal("", r);
        }

        // ---- AC-5
        [Theory]
        [InlineData("STATUS: <script>alert(1)</script>")]
        [InlineData("STATUS: <b>DONE</b>")]
        [InlineData("STATUS: \"><img src=x onerror=alert(1)>")]
        public void Markup_cannot_become_status(string input)
        {
            if (AgentStatus.TryExtract(input, out var s, out var r))
            {
                Assert.Matches("^[A-Za-z0-9_]*$", s);
                Assert.DoesNotContain("<", s);
            }
            else
            {
                Assert.Equal(input, r);
            }
        }

        [Fact]
        public void Remainder_is_still_sanitised_by_markdown_renderer()
        {
            Assert.True(AgentStatus.TryExtract("STATUS: DONE\n<script>alert(1)</script>", out _, out var r));
            var html = MarkdownRenderer.ToHtml(r);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        }
    }
}
