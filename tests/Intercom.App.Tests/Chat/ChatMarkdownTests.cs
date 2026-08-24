using Intercom.Chat;
using Xunit;

namespace Intercom.App.Tests.Chat;

public sealed class ChatMarkdownTests
{
    [Fact]
    public void Parse_ProducesSafeFormattingLinksAndSemanticSpeech()
    {
        const string source = "**Dinner** at [our place](https://family.example/menu) or http://unsafe.example";

        var document = ChatMarkdown.Parse(source);

        Assert.Contains(document.Inlines, inline => inline is ChatTextRun { Text: "Dinner", Bold: true });
        var link = Assert.Single(document.Inlines.OfType<ChatLinkRun>());
        Assert.Equal("our place", link.Text);
        Assert.Equal(new Uri("https://family.example/menu"), link.Destination);
        Assert.True(link.RequiresConfirmation);
        Assert.DoesNotContain(document.Inlines, inline => inline is ChatLinkRun { Text: "http://unsafe.example" });
        Assert.Equal("Dinner at our place or http://unsafe.example", document.SpeechText);
    }

    [Fact]
    public void Parse_RejectsRawHtmlHeadingsTablesAndMarkdownImages()
    {
        const string source = "# title\n<img src=x>\n![alt](https://family.example/a.png)\n|a|b|";

        var document = ChatMarkdown.Parse(source);

        Assert.Empty(document.Inlines.OfType<ChatLinkRun>());
        Assert.Contains("# title", document.PlainText);
        Assert.Contains("<img src=x>", document.PlainText);
        Assert.Contains("![alt]", document.PlainText);
    }

    [Fact]
    public void Parse_RendersTheApprovedBlockSubsetWithoutControlMarkers()
    {
        const string source = "> quote\n- first\n1. second\n```\nvar answer = 42;\n```";

        var document = ChatMarkdown.Parse(source);

        Assert.Contains("│ quote", document.PlainText);
        Assert.Contains("• first", document.PlainText);
        Assert.Contains("1. second", document.PlainText);
        Assert.Contains(document.Inlines, inline => inline is ChatTextRun { Text: "var answer = 42;", Code: true });
        Assert.DoesNotContain("```", document.PlainText);
    }

    [Fact]
    public void Parse_SpeaksBareHttpsLinkAsHostname()
    {
        var document = ChatMarkdown.Parse("See https://family.example/a/long/path?q=1 now");

        Assert.Equal("See family.example now", document.SpeechText);
    }

    [Fact]
    public void Parse_RecognisesEveryInlineAndListVariant()
    {
        var document = ChatMarkdown.Parse("* bullet\n**bold** ~~strike~~ `code` *italic*");

        Assert.StartsWith("• bullet\n", document.PlainText);
        Assert.Contains(document.Inlines, value => value is ChatTextRun { Text: "bold", Bold: true });
        Assert.Contains(document.Inlines, value => value is ChatTextRun { Text: "strike", Strikethrough: true });
        Assert.Contains(document.Inlines, value => value is ChatTextRun { Text: "code", Code: true });
        Assert.Contains(document.Inlines, value => value is ChatTextRun { Text: "italic", Italic: true });
        Assert.DoesNotContain(document.Inlines, value => value.Text.Length == 0);
        Assert.Equal("bullet bold strike code italic", document.SpeechText);
    }

    [Fact]
    public void Parse_EmptySource_ReturnsEmptyDocument()
    {
        var document = ChatMarkdown.Parse(string.Empty);

        Assert.Empty(document.Inlines);
        Assert.Empty(document.PlainText);
        Assert.Empty(document.SpeechText);
    }

    [Fact]
    public void Parse_AllChangedBranches_PreserveExactPlainAndSpeechText()
    {
        const string source = "> quote\r\n![alt](https://images.example/a.png) [label](https://safe.example/a) https://shared.example/a **bold**\n```\ncode\n```";

        var document = ChatMarkdown.Parse(source);

        Assert.Equal("│ quote\n![alt](https://images.example/a.png) label https://shared.example/a bold\ncode", document.PlainText);
        Assert.Equal("quote ![alt](https://images.example/a.png) label shared.example bold code", document.SpeechText);
        Assert.Collection(document.Inlines,
            run => Assert.Equal("│ ", Assert.IsType<ChatTextRun>(run).Text),
            run => Assert.Equal("quote", Assert.IsType<ChatTextRun>(run).Text),
            run => Assert.Equal("\n", Assert.IsType<ChatTextRun>(run).Text),
            run => Assert.Equal("![alt](https://images.example/a.png)", Assert.IsType<ChatTextRun>(run).Text),
            run => Assert.Equal(" ", Assert.IsType<ChatTextRun>(run).Text),
            run => Assert.True(Assert.IsType<ChatLinkRun>(run).RequiresConfirmation),
            run => Assert.Equal(" ", Assert.IsType<ChatTextRun>(run).Text),
            run => Assert.False(Assert.IsType<ChatLinkRun>(run).RequiresConfirmation),
            run => Assert.Equal(" ", Assert.IsType<ChatTextRun>(run).Text),
            run => Assert.True(Assert.IsType<ChatTextRun>(run).Bold),
            run => Assert.Equal("\n", Assert.IsType<ChatTextRun>(run).Text),
            run => Assert.True(Assert.IsType<ChatTextRun>(run).Code));
    }
}
