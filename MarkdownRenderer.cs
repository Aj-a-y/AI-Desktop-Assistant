using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopAssistant;

internal static partial class MarkdownRenderer
{
    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(41, 36, 36))
        };

        var lines = markdown.Replace("\r", string.Empty).Split('\n');
        var paragraphLines = new List<string>();
        var inCode = false;
        var code = new List<string>();

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0) return;
            var text = string.Join(" ", paragraphLines).Trim();
            paragraphLines.Clear();
            if (text.Length == 0) return;
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12), LineHeight = 21 };
            AddInlineMarkdown(paragraph.Inlines, text);
            document.Blocks.Add(paragraph);
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
            {
                FlushParagraph();
                if (inCode)
                {
                    document.Blocks.Add(CreateCodeBlock(code));
                    code.Clear();
                }
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                code.Add(line);
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                var level = heading.Groups[1].Length;
                var paragraph = new Paragraph { Margin = new Thickness(0, 5, 0, 9), LineHeight = 24 };
                var run = new Run(heading.Groups[2].Value) { FontSize = level == 1 ? 22 : level == 2 ? 19 : 16, FontWeight = FontWeights.SemiBold };
                paragraph.Inlines.Add(run);
                document.Blocks.Add(paragraph);
                continue;
            }

            // Present list-like search results as readable prose instead of a point-by-point answer.
            var listItem = ListPrefixRegex().Match(line);
            if (listItem.Success)
            {
                var item = listItem.Groups[1].Value.Trim();
                paragraphLines.Add(item.Length > 0 && !char.IsPunctuation(item[^1]) ? item + "." : item);
            }
            else paragraphLines.Add(line);
        }

        if (inCode) document.Blocks.Add(CreateCodeBlock(code));
        FlushParagraph();
        return document;
    }

    private static Paragraph CreateCodeBlock(IEnumerable<string> lines)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 12),
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromRgb(35, 43, 48)),
            Foreground = new SolidColorBrush(Color.FromRgb(221, 238, 231)),
            FontFamily = new FontFamily("Cascadia Code"),
            FontSize = 12
        };
        paragraph.Inlines.Add(new Run(string.Join(Environment.NewLine, lines)));
        return paragraph;
    }

    private static void AddInlineMarkdown(InlineCollection inlines, string text)
    {
        var position = 0;
        foreach (Match match in InlineRegex().Matches(text))
        {
            if (match.Index > position) inlines.Add(new Run(text[position..match.Index]));
            var token = match.Value;
            if (match.Groups[1].Success)
            {
                var link = new Hyperlink(new Run(match.Groups[1].Value))
                {
                    NavigateUri = Uri.TryCreate(match.Groups[2].Value, UriKind.Absolute, out var uri) ? uri : null,
                    Foreground = new SolidColorBrush(Color.FromRgb(36, 103, 112)),
                    TextDecorations = TextDecorations.Underline
                };
                link.RequestNavigate += (_, args) =>
                {
                    if (args.Uri.Scheme is "http" or "https")
                        Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
                };
                inlines.Add(link);
            }
            else if (token.StartsWith("**") || token.StartsWith("__"))
                inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith('*') || token.StartsWith('_'))
                inlines.Add(new Italic(new Run(token[1..^1])));
            else
                inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Code"),
                    Background = new SolidColorBrush(Color.FromRgb(233, 239, 235))
                });
            position = match.Index + match.Length;
        }
        if (position < text.Length) inlines.Add(new Run(text[position..]));
    }

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(?:[-*+]\s+|\d+[.)]\s+)(.*)$")]
    private static partial Regex ListPrefixRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)|\*\*[^*]+\*\*|__[^_]+__|\*[^*]+\*|_[^_]+_|`[^`]+`")]
    private static partial Regex InlineRegex();
}
