using System.Text.RegularExpressions;
using AutoPilot.Services;

namespace AutoPilot.Tests
{
    public class MarkdownRendererAcceptanceTests
    {
        private static string H(string s) => MarkdownRenderer.ToHtml(s);

        private static readonly Regex ActiveUrl = new(
            @"\b(href|src)\s*=\s*[""']?\s*(javascript|vbscript|data)\s*:", RegexOptions.IgnoreCase);

        private static void NoLiveTag(string html, params string[] tags)
        {
            foreach (var t in tags)
                Assert.DoesNotMatch(new Regex("<\\s*" + t + @"\b", RegexOptions.IgnoreCase), html);
        }

        // ---- AC-1
        [Theory]
        [InlineData("# Title", "<h1")]
        [InlineData("## Title", "<h2")]
        [InlineData("### Title", "<h3")]
        [InlineData("**bold**", "<strong>bold</strong>")]
        [InlineData("*it*", "<em>it</em>")]
        [InlineData("- a\n- b", "<ul")]
        [InlineData("1. a\n2. b", "<ol")]
        [InlineData("`code`", "<code>code</code>")]
        [InlineData("> quote", "<blockquote")]
        public void Ac1_BasicConstructs(string md, string expected)
        {
            Assert.Contains(expected, H(md));
        }

        [Fact]
        public void Ac1_ListItems()
        {
            var html = H("- a\n- b\n- c");
            Assert.Equal(3, Regex.Matches(html, "<li").Count);
        }

        [Fact]
        public void Ac1_NestedList()
        {
            var html = H("- a\n  - b\n  - c\n- d");
            Assert.Equal(2, Regex.Matches(html, "<ul").Count);
            Assert.Equal(4, Regex.Matches(html, "<li").Count);
        }

        [Fact]
        public void Ac1_FencedCodeBlock()
        {
            var html = H("```csharp\nvar x = 1 < 2;\n```");
            Assert.Contains("<pre", html);
            Assert.Contains("<code", html);
            Assert.Contains("var x = 1 &lt; 2;", html);
        }

        [Fact]
        public void Ac1_Table()
        {
            var html = H("| A | B |\n|---|---|\n| 1 | 2 |");
            Assert.Contains("<table", html);
            Assert.Contains("<th", html);
            Assert.Contains("<td", html);
            Assert.Contains("1", html);
        }

        [Fact]
        public void Ac1_Link()
        {
            var html = H("[site](https://example.com/a?b=1&c=2)");
            Assert.Contains("href=\"https://example.com/a?b=1&amp;c=2\"", html);
            Assert.Contains(">site</a>", html);
        }

        [Fact]
        public void Ac1_CodeInsideDoesNotFormat()
        {
            var html = H("`**x**`");
            Assert.DoesNotContain("<strong>", html);
        }

        // ---- links remain usable
        [Theory]
        [InlineData("[x](http://example.com)", "http://example.com")]
        [InlineData("[x](https://example.com)", "https://example.com")]
        [InlineData("[x](mailto:a@b.com)", "mailto:a@b.com")]
        [InlineData("[x](/relative/path)", "/relative/path")]
        [InlineData("[x](#frag)", "#frag")]
        [InlineData("[x][r]\n\n[r]: https://example.com/ref", "https://example.com/ref")]
        [InlineData("see https://example.com/auto now", "https://example.com/auto")]
        [InlineData("<https://example.com/angle>", "https://example.com/angle")]
        public void Ac1_SafeLinksUsable(string md, string url)
        {
            Assert.Contains("href=\"" + url + "\"", H(md));
        }

        // ---- AC-7
        [Theory]
        [InlineData("[x](https://example.com)")]
        [InlineData("[x][r]\n\n[r]: https://example.com")]
        [InlineData("https://example.com/bare")]
        [InlineData("<https://example.com/angle>")]
        public void Ac7_LinksOpenInNewTab(string md)
        {
            var html = H(md);
            var anchors = Regex.Matches(html, "<a\\b[^>]*>");
            Assert.NotEmpty(anchors);
            foreach (Match a in anchors)
            {
                Assert.Contains("target=\"_blank\"", a.Value);
                Assert.Matches(new Regex("rel=\"[^\"]*noopener[^\"]*\""), a.Value);
                Assert.Matches(new Regex("rel=\"[^\"]*noreferrer[^\"]*\""), a.Value);
            }
        }

        // ---- AC-2
        [Theory]
        [InlineData("<script>alert(1)</script>")]
        [InlineData("hello <script>alert(1)</script> world")]
        [InlineData("<img src=x onerror=alert(1)>")]
        [InlineData("<iframe src=\"https://evil.com\"></iframe>")]
        [InlineData("<div onclick=\"alert(1)\">hi</div>")]
        [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
        [InlineData("<svg onload=alert(1)>")]
        [InlineData("<SCRIPT>alert(1)</SCRIPT>")]
        [InlineData("<scr<script>ipt>alert(1)</scr</script>ipt>")]
        [InlineData("<style>body{display:none}</style>")]
        [InlineData("<object data=x></object><embed src=x>")]
        public void Ac2_RawHtmlNotEmitted(string md)
        {
            var html = H(md);
            NoLiveTag(html, "script", "img", "iframe", "div", "svg", "style", "object", "embed");
            Assert.DoesNotMatch(new Regex(@"<a\b[^>]*javascript:", RegexOptions.IgnoreCase), html);
            Assert.Contains("&lt;", html);
        }

        [Fact]
        public void Ac2_RawHtmlShownAsText()
        {
            var html = H("<script>alert(1)</script>");
            Assert.Contains("&lt;script&gt;", html);
        }

        [Fact]
        public void Ac2_RawHtmlInsideBlocksEscaped()
        {
            foreach (var md in new[] { "> <script>x</script>", "- <img src=x onerror=alert(1)>", "# <script>x</script>",
                "| a |\n|---|\n| <script>x</script> |", "**<script>x</script>**", "[<img src=x onerror=a()>](https://e.com)" })
            {
                var html = H(md);
                NoLiveTag(html, "script", "img");
            }
        }

        [Theory]
        [InlineData("[x](javascript:alert(1))")]
        [InlineData("[x](JavaScript:alert(1))")]
        [InlineData("[x](JAVASCRIPT:alert(1))")]
        [InlineData("[x](vbscript:msgbox(1))")]
        [InlineData("[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
        [InlineData("[x](javascript&#58;alert(1))")]
        [InlineData("[x](javascript&colon;alert(1))")]
        [InlineData("[x](&#106;avascript:alert(1))")]
        [InlineData("[x](&#x6A;avascript:alert(1))")]
        [InlineData("[x](<javascript:alert(1)>)")]
        [InlineData("[x](java\tscript:alert(1))")]
        [InlineData("[x]( javascript:alert(1))")]
        [InlineData("[x](%6Aavascript:alert(1))")]
        [InlineData("[x][r]\n\n[r]: javascript:alert(1)")]
        [InlineData("[x][r]\n\n[r]: vbscript:x")]
        [InlineData("[x][r]\n\n[r]: data:text/html,hi")]
        [InlineData("[x][r]\n\n[r]: JaVaScRiPt:alert(1)")]
        [InlineData("<javascript:alert(1)>")]
        [InlineData("![x](javascript:alert(1))")]
        [InlineData("![x](data:image/svg+xml;base64,PHN2Zz4=)")]
        [InlineData("![x](vbscript:x)")]
        [InlineData("![x][r]\n\n[r]: javascript:alert(1)")]
        public void Ac2_UnsafeSchemesNotActive(string md)
        {
            var html = H(md);
            Assert.DoesNotMatch(ActiveUrl, html);
            // Entity-decoded check: no href/src attribute whose value, decoded, starts with an unsafe scheme.
            foreach (Match m in Regex.Matches(html, "\\b(?:href|src)=\"([^\"]*)\"", RegexOptions.IgnoreCase))
            {
                var v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                v = Uri.UnescapeDataString(v);
                v = Regex.Replace(v, @"[\s\x00-\x1f]", "");
                Assert.DoesNotMatch(new Regex("^(javascript|vbscript|data):", RegexOptions.IgnoreCase), v);
            }
        }

        [Fact]
        public void Ac2_ImageSafeSchemesStillWork()
        {
            var html = H("![alt](https://example.com/a.png)");
            Assert.Contains("src=\"https://example.com/a.png\"", html);
        }

        [Theory]
        [InlineData("[x](https://e.com\" onclick=\"alert(1))")]
        [InlineData("[x](https://e.com \"title\" onmouseover=\"alert(1)\")")]
        [InlineData("[x](https://e.com \"a\\\" onmouseover=\\\"alert(1)\")")]
        [InlineData("![x](https://e.com/a.png\" onerror=\"alert(1))")]
        [InlineData("![a\" onerror=\"alert(1)](https://e.com/a.png)")]
        [InlineData("[x](https://e.com/\"onmouseover=\"alert(1))")]
        [InlineData("[x](https://e.com \"t\" \"><script>alert(1)</script>)")]
        public void Ac2_AttributeInjection(string md)
        {
            var html = H(md);
            NoLiveTag(html, "script");
            // No injected attribute names inside any tag.
            foreach (Match tag in Regex.Matches(html, "<[a-zA-Z][^>]*>"))
            {
                // strip quoted attribute values, then look for event handlers
                var stripped = Regex.Replace(tag.Value, "\"[^\"]*\"", "\"\"");
                Assert.DoesNotMatch(new Regex(@"\son\w+\s*=", RegexOptions.IgnoreCase), stripped);
            }
        }

        [Fact]
        public void Ac2_CodeContentEscaped()
        {
            var html = H("```html\n<script>alert(1)</script>\n```");
            NoLiveTag(html, "script");
            Assert.Contains("&lt;script&gt;", html);
            html = H("`<img src=x onerror=a()>`");
            NoLiveTag(html, "img");
        }

        [Fact]
        public void Ac2_HtmlCommentsAndCdata()
        {
            var html = H("<!-- x --><![CDATA[<script>a</script>]]>");
            NoLiveTag(html, "script");
        }

        // ---- AC-3
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Ac3_NullOrEmpty(string? md)
        {
            Assert.Equal(string.Empty, MarkdownRenderer.ToHtml(md));
        }

        [Theory]
        [InlineData("```")]
        [InlineData("```csharp\nvar x = 1;")]
        [InlineData("text\n```\ncode never closed")]
        [InlineData("**bold")]
        [InlineData("*it")]
        [InlineData("`code")]
        [InlineData("[link](http://exa")]
        [InlineData("[link](")]
        [InlineData("[link")]
        [InlineData("![img](")]
        [InlineData("| a | b |\n|---")]
        [InlineData("| a | b |\n|---|---|\n| 1")]
        [InlineData("> ")]
        [InlineData("- ")]
        [InlineData("1.")]
        [InlineData("#")]
        [InlineData("<")]
        [InlineData("<scr")]
        [InlineData("<script")]
        [InlineData("&")]
        [InlineData("&#")]
        [InlineData("[x][")]
        [InlineData("[x]: ")]
        [InlineData("\0\u0001\uD800")]
        [InlineData("   \n\n  \t")]
        public void Ac3_PartialInputsDoNotThrow(string md)
        {
            var ex = Record.Exception(() => MarkdownRenderer.ToHtml(md));
            Assert.Null(ex);
            Assert.NotNull(MarkdownRenderer.ToHtml(md));
        }

        [Fact]
        public void Ac3_EveryPrefixOfRichDocumentDoesNotThrowAndStaysSafe()
        {
            var doc = "# Head\n\nSome **bold** and *it* `c` [l](https://e.com) <script>alert(1)</script>\n\n- a\n  - b\n1. x\n\n> q\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```cs\nvar a = 1;\n```\n\n[j](javascript:alert(1)) ![i](data:x)\n";
            for (int i = 0; i <= doc.Length; i++)
            {
                var html = MarkdownRenderer.ToHtml(doc.Substring(0, i));
                NoLiveTag(html, "script");
                Assert.DoesNotMatch(ActiveUrl, html);
            }
        }

        [Fact]
        public void Ac3_UnclosedFenceStillShowsContent()
        {
            var html = H("```\nhello world");
            Assert.Contains("hello world", html);
        }

        [Fact]
        public void Ac3_LargeInputCompletes()
        {
            var md = string.Concat(Enumerable.Repeat("**a** [b](https://e.com) `c`\n\n", 5000));
            var html = H(md);
            Assert.Contains("<strong>", html);
        }

        [Fact]
        public void Ac3_PathologicalNestingCompletes()
        {
            var md = new string('[', 5000) + new string('*', 5000) + new string('>', 2000);
            var ex = Record.Exception(() => H(md));
            Assert.Null(ex);
        }

        // ---- AC-2 plain text
        [Fact]
        public void PlainTextIsParagraphWithEscaping()
        {
            var html = H("a < b & c > d");
            Assert.Contains("<p>", html);
            Assert.Contains("&lt;", html);
            Assert.Contains("&amp;", html);
        }
    }
}
