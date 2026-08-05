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
}
