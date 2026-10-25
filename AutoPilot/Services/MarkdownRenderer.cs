using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AutoPilot.Services
{
    /// <summary>
    /// Converts agent-authored Markdown into safe HTML. Raw HTML is escaped, unsafe URL schemes are neutralized
    /// and links open in a new tab.
    /// </summary>
    public static class MarkdownRenderer
    {
        // Deliberately not UseAdvancedExtensions(): it includes GenericAttributes, which would let agent text
        // inject arbitrary attributes (e.g. onclick) via "{...}" syntax.
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .DisableHtml()
            .UsePipeTables()
            .UseEmphasisExtras()
            .UseAutoLinks()
            .UseListExtras()
            .UseTaskLists()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();

        private static readonly string[] LinkSchemes = ["http", "https", "mailto"];
        private static readonly string[] ImageSchemes = ["http", "https"];

        public static string ToHtml(string? markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return string.Empty;
            }

            try
            {
                var document = Markdown.Parse(markdown, Pipeline);

                foreach (var auto in document.Descendants<AutolinkInline>().ToList())
                {
                    if (IsSafeUrl(auto.Url, LinkSchemes))
                    {
                        // Markdig's autolink renderer ignores attributes, so use an equivalent LinkInline.
                        var href = auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                            ? "mailto:" + auto.Url
                            : auto.Url;
                        var link = new LinkInline(href, string.Empty);
                        link.AppendChild(new LiteralInline(auto.Url));
                        auto.ReplaceBy(link);
                    }
                    else
                    {
                        // Show the unsafe autolink as plain (escaped) text instead of an anchor.
                        auto.ReplaceBy(new LiteralInline(auto.Url));
                    }
                }

                foreach (var link in document.Descendants<LinkInline>().ToList())
                {
                    if (!IsSafeUrl(link.Url, link.IsImage ? ImageSchemes : LinkSchemes))
                    {
                        if (link.IsImage)
                        {
                            var alt = new System.Text.StringBuilder();
                            foreach (var literal in link.Descendants<LiteralInline>())
                            {
                                alt.Append(literal.Content.ToString());
                            }

                            link.ReplaceBy(new LiteralInline(alt.ToString()));
                        }
                        else
                        {
                            // Keep the link text (including nested formatting) as ordinary inline content.
                            foreach (var child in link.ToList())
                            {
                                child.Remove();
                                link.InsertBefore(child);
                            }

                            link.Remove();
                        }

                        continue;
                    }

                    if (!link.IsImage)
                    {
                        var attributes = link.GetAttributes();
                        attributes.AddPropertyIfNotExist("target", "_blank");
                        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
                    }
                }

                return Markdown.ToHtml(document, Pipeline);
            }
            catch (Exception)
            {
                // Pathological input (e.g. excessive nesting): fall back to safely encoded plain text.
                var encoded = System.Net.WebUtility.HtmlEncode(markdown)
                    .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br />\n");
                return "<p>" + encoded + "</p>\n";
            }
        }

        private static bool IsSafeUrl(string? url, string[] allowedSchemes)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return true;
            }

            // Browsers ignore whitespace/control characters inside the scheme ("java\tscript:").
            var cleaned = new string(url.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());

            var end = cleaned.IndexOfAny([':', '/', '?', '#']);
            if (end < 0 || cleaned[end] != ':')
            {
                return true; // relative URL or fragment
            }

            return allowedSchemes.Contains(cleaned[..end], StringComparer.OrdinalIgnoreCase);
        }
    }
}
