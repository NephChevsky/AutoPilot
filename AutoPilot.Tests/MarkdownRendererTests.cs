using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class MarkdownRendererTests
    {
        [Theory]
        [InlineData("# Title", "<h1")]
        [InlineData("## Title", "<h2")]
        [InlineData("**bold**", "<strong>bold</strong>")]
        [InlineData("*it*", "<em>it</em>")]
        [InlineData("- a\n- b", "<ul>")]
        [InlineData("1. a\n2. b", "<ol>")]
        [InlineData("`x`", "<code>x</code>")]
        [InlineData("> quote", "<blockquote>")]
        [InlineData("```cs\nvar a = 1;\n```", "<pre><code class=\"language-cs\">")]
        [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "<table")]
        public void Renders_supported_markdown(string markdown, string expected)
        {
            Assert.Contains(expected, MarkdownRenderer.ToHtml(markdown));
        }

        [Theory]
        [InlineData("<https://example.com/angle>")]
        [InlineData("<a@b.com>")]
        public void Angle_autolinks_open_in_new_tab_with_rel(string markdown)
        {
            var html = MarkdownRenderer.ToHtml(markdown);
            Assert.Contains("target=\"_blank\"", html);
            Assert.Contains("rel=\"noopener noreferrer\"", html);
        }

        [Fact]
        public void Deeply_nested_input_does_not_throw_and_is_encoded()
        {
            var input = new string('[', 5000) + new string('*', 5000) + new string('>', 2000);
            var html = MarkdownRenderer.ToHtml(input);
            Assert.False(string.IsNullOrEmpty(html));
            Assert.DoesNotContain("<script", html);
        }

        [Fact]
        public void Link_opens_in_new_tab_with_rel()
        {
            var html = MarkdownRenderer.ToHtml("[x](https://example.com)");
            Assert.Contains("href=\"https://example.com\"", html);
            Assert.Contains("target=\"_blank\"", html);
            Assert.Contains("rel=\"noopener noreferrer\"", html);
        }

        [Theory]
        [InlineData("[x](/path)")]
        [InlineData("[x](#frag)")]
        [InlineData("[x](mailto:a@b.com)")]
        [InlineData("[x](docs/a.md)")]
        public void Safe_link_urls_are_kept(string markdown)
        {
            Assert.Contains("href=\"", MarkdownRenderer.ToHtml(markdown));
            Assert.DoesNotContain("href=\"\"", MarkdownRenderer.ToHtml(markdown));
        }

        [Theory]
        [InlineData("<script>alert(1)</script>")]
        [InlineData("<img src=x onerror=alert(1)>")]
        [InlineData("text <b onclick=\"x()\">hi</b>")]
        [InlineData("<div>\n<script>alert(1)</script>\n</div>")]
        public void Raw_html_is_escaped(string markdown)
        {
            var html = MarkdownRenderer.ToHtml(markdown);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<b ", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<div", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;", html);
        }

        [Fact]
        public void Unsafe_autolink_is_plain_text()
        {
            var html = MarkdownRenderer.ToHtml("<javascript:alert(1)>");
            Assert.DoesNotContain("<a", html);
            Assert.DoesNotContain("href", html);
        }

        [Theory]
        [InlineData("[x](javascript:alert(1))")]
        [InlineData("[x](JavaScript:alert(1))")]
        [InlineData("[x](vbscript:msgbox(1))")]
        [InlineData("[x](data:text/html;base64,PHNjcmlwdD4=)")]
        [InlineData("[x](java\tscript:alert(1))")]
        [InlineData("![x](javascript:alert(1))")]
        [InlineData("![x](data:image/svg+xml;base64,AAAA)")]
        public void Unsafe_urls_are_neutralized(string markdown)
        {
            var html = MarkdownRenderer.ToHtml(markdown);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Unsafe_link_renders_its_text_without_anchor()
        {
            var html = MarkdownRenderer.ToHtml("[click me](javascript:alert(1))");
            Assert.DoesNotContain("<a", html);
            Assert.DoesNotContain("href", html);
            Assert.Contains("click me", html);
        }

        [Fact]
        public void Unsafe_image_renders_alt_text_without_img()
        {
            var html = MarkdownRenderer.ToHtml("![the alt](javascript:alert(1))");
            Assert.DoesNotContain("<img", html);
            Assert.DoesNotContain("src", html);
            Assert.Contains("the alt", html);
        }

        [Fact]
        public void Unsafe_link_preserves_nested_formatting()
        {
            var html = MarkdownRenderer.ToHtml("[**bold** text](javascript:x)");
            Assert.DoesNotContain("<a", html);
            Assert.Contains("<strong>bold</strong> text", html);
        }

        [Fact]
        public void Generic_attributes_cannot_be_injected()
        {
            var html = MarkdownRenderer.ToHtml("text {onclick=\"x()\"}\n\n# H {onclick=\"x()\"}");
            Assert.DoesNotContain("onclick=\"", html.Replace("&quot;", ""));
            Assert.DoesNotContain(" onclick", html.Split('>')[0]);
        }

        [Theory]
        [InlineData("```\nunclosed")]
        [InlineData("**bold")]
        [InlineData("[link](")]
        [InlineData("| a | b |\n|--")]
        [InlineData("> ")]
        [InlineData("1. ")]
        public void Partial_markdown_does_not_throw(string markdown)
        {
            var html = Record.Exception(() => MarkdownRenderer.ToHtml(markdown));
            Assert.Null(html);
        }

        [Fact]
        public void Unclosed_fence_renders_as_code_block()
        {
            Assert.Contains("<pre>", MarkdownRenderer.ToHtml("```\nunclosed"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Null_or_empty_returns_empty(string? input)
        {
            Assert.Equal(string.Empty, MarkdownRenderer.ToHtml(input));
        }
    }
}
