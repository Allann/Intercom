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
        return new Parser().Parse(source);
    }

    sealed class Parser
    {
        readonly List<ChatInline> _runs = [];
        readonly StringBuilder _plain = new();
        bool _inCodeBlock;
        bool _emittedLine;

        internal ChatMarkdownDocument Parse(string source)
        {
            foreach (var line in source.Replace("\r", "").Split('\n')) ParseLine(line);
            var text = _plain.ToString();
            var speech = string.Concat(_runs.Select(SpeechFor)).Replace("│ ", "").Replace("• ", "");
            return new ChatMarkdownDocument(_runs, text, NormalizeForSpeech(speech));
        }

        void ParseLine(string originalLine)
        {
            if (originalLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                _inCodeBlock = !_inCodeBlock;
                return;
            }
            if (_emittedLine) AddText("\n");
            _emittedLine = true;
            if (_inCodeBlock) { AddFormatted(originalLine, code: true); return; }
            ParseInline(AddPrefix(originalLine));
        }

        string AddPrefix(string line)
        {
            if (line.StartsWith("> ", StringComparison.Ordinal)) { AddText("│ "); return line[2..]; }
            if (line.StartsWith("- ", StringComparison.Ordinal)) { AddText("• "); return line[2..]; }
            if (line.StartsWith("* ", StringComparison.Ordinal)) { AddText("• "); return line[2..]; }
            return line;
        }

        void ParseInline(string line)
        {
            var position = 0;
            foreach (Match match in InlinePattern().Matches(line))
            {
                AddText(line[position..match.Index]);
                AddMatch(match);
                position = match.Index + match.Length;
            }
            AddText(line[position..]);
        }

        void AddMatch(Match match)
        {
            var value = match.Value;
            if (value.StartsWith("![", StringComparison.Ordinal)) { AddText(value); return; }
            if (match.Groups[1].Success) { AddConfirmedLink(match); return; }
            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { AddSharedLink(value); return; }
            AddFormattedMatch(match);
        }

        void AddFormattedMatch(Match match)
        {
            if (match.Groups[3].Success) { AddFormatted(match.Groups[3].Value, bold: true); return; }
            if (match.Groups[4].Success) { AddFormatted(match.Groups[4].Value, strike: true); return; }
            if (match.Groups[5].Success) { AddFormatted(match.Groups[5].Value, code: true); return; }
            AddFormatted(match.Groups[6].Value, italic: true);
        }

        void AddConfirmedLink(Match match)
        {
            var label = match.Groups[1].Value;
            _runs.Add(new ChatLinkRun(label, new Uri(match.Groups[2].Value, UriKind.Absolute), true));
            _plain.Append(label);
        }

        void AddSharedLink(string value)
        {
            _runs.Add(new ChatLinkRun(value, new Uri(value, UriKind.Absolute), false));
            _plain.Append(value);
        }

        void AddText(string text)
        {
            if (text.Length == 0) return;
            _runs.Add(new ChatTextRun(text));
            _plain.Append(text);
        }

        void AddFormatted(string text, bool bold = false, bool italic = false, bool strike = false, bool code = false)
        {
            _runs.Add(new ChatTextRun(text, bold, italic, strike, code));
            _plain.Append(text);
        }

        static string SpeechFor(ChatInline run) => run is ChatLinkRun { RequiresConfirmation: false } link
            ? link.Destination.Host : run.Text;
    }

    static string NormalizeForSpeech(string text) =>
        Regex.Replace(text.Replace("\r", ""), @"[ \t]*\n[ \t]*", " ").Trim();
}
