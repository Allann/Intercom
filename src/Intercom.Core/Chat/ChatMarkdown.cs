using System.Text;
using System.Text.RegularExpressions;

namespace Intercom.Chat;

public abstract record ChatInline(string Text);

public sealed record ChatTextRun(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Strikethrough = false,
    bool Code = false) : ChatInline(Text);

public sealed record ChatLinkRun(string Text, Uri Destination, bool RequiresConfirmation) : ChatInline(Text);

public sealed record ChatMarkdownDocument(IReadOnlyList<ChatInline> Inlines, string PlainText, string SpeechText);

/// <summary>Parses Intercom's deliberately small Chat Markdown dialect into
/// a UI-neutral render model. Raw HTML and non-HTTPS destinations are never
/// executable; unsupported Markdown remains visible as ordinary text.</summary>
public static partial class ChatMarkdown
{
    [GeneratedRegex(@"!\[[^\]]*\]\([^\)]*\)|\[([^\]]+)\]\((https://[^\s\)]+)\)|https://[^\s<>()]+|\*\*([^*\r\n]+)\*\*|~~([^~\r\n]+)~~|`([^`\r\n]+)`|\*([^*\r\n]+)\*", RegexOptions.IgnoreCase)]
    private static partial Regex InlinePattern();

    public static ChatMarkdownDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var runs = new List<ChatInline>();
        var plain = new StringBuilder();
        var inCodeBlock = false;
        var emittedLine = false;
        foreach (var originalLine in source.Replace("\r", "").Split('\n'))
        {
            if (originalLine.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inCodeBlock = !inCodeBlock; continue; }
            if (emittedLine) AddText("\n");
            emittedLine = true;
            if (inCodeBlock) { AddFormatted(originalLine, code: true); continue; }

            var line = originalLine;
            if (line.StartsWith("> ", StringComparison.Ordinal)) { AddText("│ "); line = line[2..]; }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            { AddText("• "); line = line[2..]; }
            ParseInline(line);
        }

        var text = plain.ToString();
        var speech = string.Concat(runs.Select(run => run switch
        {
            ChatLinkRun link when !link.RequiresConfirmation => link.Destination.Host,
            _ => run.Text,
        })).Replace("│ ", "").Replace("• ", "");
        return new ChatMarkdownDocument(runs, text, NormalizeForSpeech(speech));

        void ParseInline(string line)
        {
            var position = 0;
            foreach (Match match in InlinePattern().Matches(line))
            {
                AddText(line[position..match.Index]);
                var value = match.Value;
                if (value.StartsWith("![", StringComparison.Ordinal)) AddText(value);
                else if (match.Groups[1].Success)
                {
                    var label = match.Groups[1].Value;
                    var destination = new Uri(match.Groups[2].Value, UriKind.Absolute);
                    runs.Add(new ChatLinkRun(label, destination, RequiresConfirmation: true)); plain.Append(label);
                }
                else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(value, UriKind.Absolute);
                    runs.Add(new ChatLinkRun(value, uri, RequiresConfirmation: false)); plain.Append(value);
                }
                else if (match.Groups[3].Success) AddFormatted(match.Groups[3].Value, bold: true);
                else if (match.Groups[4].Success) AddFormatted(match.Groups[4].Value, strike: true);
                else if (match.Groups[5].Success) AddFormatted(match.Groups[5].Value, code: true);
                else if (match.Groups[6].Success) AddFormatted(match.Groups[6].Value, italic: true);
                position = match.Index + match.Length;
            }
            AddText(line[position..]);
        }

        void AddText(string text)
        {
            if (text.Length == 0) return;
            runs.Add(new ChatTextRun(text));
            plain.Append(text);
        }

        void AddFormatted(string text, bool bold = false, bool italic = false, bool strike = false, bool code = false)
        {
            runs.Add(new ChatTextRun(text, bold, italic, strike, code));
            plain.Append(text);
        }
    }

    static string NormalizeForSpeech(string text) =>
        Regex.Replace(text.Replace("\r", ""), @"[ \t]*\n[ \t]*", " ").Trim();
}
