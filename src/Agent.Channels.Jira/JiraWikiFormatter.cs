using System.Text;
using System.Text.RegularExpressions;
using Agent.Core.Channels;
using Agent.Core.Formatting;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Agent.Channels.Jira;

/// <summary>Converts the agent's markdown into Jira wiki markup by walking Markdig's syntax tree.</summary>
public sealed partial class JiraWikiFormatter : IResponseFormatter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    public Channel Channel => Channel.Jira;

    public string Format(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), Pipeline);
        return new Renderer().Render(document);
    }

    /// <summary>Maps fence info strings onto the languages Jira's {code} macro knows; unknown ones pass through lower-cased.</summary>
    internal static string? MapLanguage(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        var lang = info.Trim().Split(' ', 2)[0].ToLowerInvariant();
        lang = LanguageSanitizer().Replace(lang, string.Empty);
        return lang switch
        {
            "" => null,
            "cs" or "csharp" or "c#" => "c#",
            "js" or "javascript" or "jsx" or "ts" or "typescript" or "tsx" => "javascript",
            "sh" or "shell" or "bash" or "zsh" or "console" => "bash",
            "yml" or "yaml" => "yaml",
            "py" or "python" => "python",
            "cpp" or "c++" or "cc" or "cxx" => "c++",
            "rb" or "ruby" => "ruby",
            "text" or "txt" or "plain" or "plaintext" or "none" => null,
            _ => lang,
        };
    }

    [GeneratedRegex(@"[^a-z0-9#+.\-]")]
    private static partial Regex LanguageSanitizer();

    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    private sealed class Renderer
    {
        private int _tableDepth;

        public string Render(MarkdownDocument document)
            => string.Join("\n\n", RenderBlocks(document, string.Empty)).Trim();

        private List<string> RenderBlocks(ContainerBlock container, string listPrefix)
        {
            var parts = new List<string>();
            foreach (var block in container)
            {
                var text = RenderBlock(block, listPrefix);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }

            return parts;
        }

        private string? RenderBlock(Block block, string listPrefix) => block switch
        {
            HeadingBlock heading => $"h{Math.Clamp(heading.Level, 1, 6)}. {RenderInlines(heading.Inline)}",
            ParagraphBlock paragraph => RenderInlines(paragraph.Inline),
            FencedCodeBlock fenced => RenderCode(MapLanguage(fenced.Info), Lines(fenced)),
            CodeBlock code => RenderCode(null, Lines(code)),
            QuoteBlock quote => "{quote}\n" + string.Join("\n\n", RenderBlocks(quote, listPrefix)) + "\n{quote}",
            ListBlock list => RenderList(list, listPrefix),
            ThematicBreakBlock => "----",
            Table table => RenderTable(table),
            HtmlBlock html => Lines(html),
            LinkReferenceDefinitionGroup => null,
            LeafBlock leaf => leaf.Inline is null ? Lines(leaf) : RenderInlines(leaf.Inline),
            ContainerBlock other => string.Join("\n\n", RenderBlocks(other, listPrefix)),
            _ => null,
        };

        private static string RenderCode(string? language, string code)
        {
            var open = language is null ? "{code}" : "{code:" + language + "}";
            return open + "\n" + code.TrimEnd('\n') + "\n{code}";
        }

        private string RenderList(ListBlock list, string listPrefix)
        {
            var marker = listPrefix + (list.IsOrdered ? "#" : "*");
            var lines = new List<string>();
            foreach (var item in list.OfType<ListItemBlock>())
            {
                var first = true;
                foreach (var child in item)
                {
                    switch (child)
                    {
                        case ListBlock nested:
                            if (first)
                            {
                                lines.Add(marker + " ");
                            }

                            lines.Add(RenderList(nested, marker));
                            break;
                        case ParagraphBlock paragraph:
                            var text = RenderInlines(paragraph.Inline);
                            lines.Add(first ? $"{marker} {text}" : text);
                            break;
                        default:
                            var rendered = RenderBlock(child, marker);
                            if (rendered is null)
                            {
                                continue;
                            }

                            if (first)
                            {
                                lines.Add(marker + " ");
                            }

                            lines.Add(rendered);
                            break;
                    }

                    first = false;
                }

                if (first)
                {
                    lines.Add(marker + " ");
                }
            }

            return string.Join("\n", lines);
        }

        private string RenderTable(Table table)
        {
            _tableDepth++;
            try
            {
                var lines = new List<string>();
                foreach (var row in table.OfType<TableRow>())
                {
                    var separator = row.IsHeader ? "||" : "|";
                    var sb = new StringBuilder(separator);
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        var content = string.Join(" ", RenderBlocks(cell, string.Empty)).Replace('\n', ' ').Trim();
                        sb.Append(content.Length == 0 ? " " : content).Append(separator);
                    }

                    lines.Add(sb.ToString());
                }

                return string.Join("\n", lines);
            }
            finally
            {
                _tableDepth--;
            }
        }

        private string RenderInlines(ContainerInline? container)
        {
            if (container is null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var inline in container)
            {
                RenderInline(inline, sb);
            }

            return sb.ToString();
        }

        private void RenderInline(Inline inline, StringBuilder sb)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(EscapeLiteral(literal.Content.ToString()));
                    break;
                case EmphasisInline emphasis:
                    RenderEmphasis(emphasis, sb);
                    break;
                case CodeInline code:
                    sb.Append("{{").Append(EscapeLiteral(code.Content.Replace("{", "\\{", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal))).Append("}}");
                    break;
                case LinkInline link:
                    RenderLink(link, sb);
                    break;
                case AutolinkInline autolink:
                    sb.Append('[').Append(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url).Append(']');
                    break;
                case LineBreakInline:
                    sb.Append('\n');
                    break;
                case HtmlInline html:
                    sb.Append(BreakTag().IsMatch(html.Tag) ? "\n" : html.Tag);
                    break;
                case HtmlEntityInline entity:
                    sb.Append(EscapeLiteral(entity.Transcoded.ToString()));
                    break;
                case DelimiterInline delimiter:
                    sb.Append(delimiter.ToLiteral());
                    break;
                case ContainerInline container:
                    foreach (var child in container)
                    {
                        RenderInline(child, sb);
                    }

                    break;
                default:
                    break;
            }
        }

        private void RenderEmphasis(EmphasisInline emphasis, StringBuilder sb)
        {
            var marker = (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
            {
                ('*' or '_', >= 2) => "*",
                ('*' or '_', _) => "_",
                ('~', >= 2) => "-",
                ('~', _) => "~",
                ('^', _) => "^",
                ('+', _) => "+",
                _ => string.Empty,
            };

            sb.Append(marker);
            foreach (var child in emphasis)
            {
                RenderInline(child, sb);
            }

            sb.Append(marker);
        }

        private void RenderLink(LinkInline link, StringBuilder sb)
        {
            var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? string.Empty;
            if (link.IsImage)
            {
                sb.Append('!').Append(url).Append('!');
                return;
            }

            var textBuilder = new StringBuilder();
            foreach (var child in link)
            {
                RenderInline(child, textBuilder);
            }

            var text = textBuilder.ToString().Trim();
            if (text.Length == 0 || string.Equals(text, url, StringComparison.Ordinal))
            {
                sb.Append('[').Append(url).Append(']');
            }
            else
            {
                sb.Append('[').Append(text).Append('|').Append(url).Append(']');
            }
        }

        private string EscapeLiteral(string text)
            => _tableDepth > 0 ? text.Replace("|", "&#124;", StringComparison.Ordinal) : text;

        private static string Lines(LeafBlock block)
        {
            var lines = block.Lines;
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(lines.Lines[i].Slice.ToString());
            }

            return sb.ToString();
        }
    }
}
