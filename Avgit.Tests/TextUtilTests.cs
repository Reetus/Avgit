using Avgit.Core.Diff;

namespace Avgit.Tests;

public class TextUtilTests
{
    [Fact]
    public void ReadNextLine_reports_endings_and_keeps_lone_carriage_returns()
    {
        const string text = "a\r\nb\nc\rd\ne";
        var cursor = 0;

        Assert.Equal("a", TextUtil.ReadNextLine(text, ref cursor, out var ending));
        Assert.Equal("\r\n", ending);
        Assert.Equal("b", TextUtil.ReadNextLine(text, ref cursor, out ending));
        Assert.Equal("\n", ending);
        Assert.Equal("c\rd", TextUtil.ReadNextLine(text, ref cursor, out ending));
        Assert.Equal("\n", ending);
        Assert.Equal("e", TextUtil.ReadNextLine(text, ref cursor, out ending));
        Assert.Equal(string.Empty, ending);
        Assert.Equal(text.Length, cursor);

        Assert.Equal(string.Empty, TextUtil.ReadNextLine(text, ref cursor, out ending));
        Assert.Equal(string.Empty, ending);
        Assert.Equal(text.Length, cursor);
    }

    [Fact]
    public void ReadNextLine_handles_empty_lines()
    {
        const string text = "\n\r\nx";
        var cursor = 0;
        Assert.Equal(string.Empty, TextUtil.ReadNextLine(text, ref cursor, out var ending));
        Assert.Equal("\n", ending);
        Assert.Equal(string.Empty, TextUtil.ReadNextLine(text, ref cursor, out ending));
        Assert.Equal("\r\n", ending);
        Assert.Equal("x", TextUtil.ReadNextLine(text, ref cursor, out _));
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a" })]
    [InlineData("a\r\nb\r\n", new[] { "a", "b" })]
    [InlineData("a\n\nb", new[] { "a", "", "b" })]
    [InlineData("\n", new[] { "" })]
    public void SplitLines_has_no_trailing_empty_element(string text, string[] expected)
        => Assert.Equal(expected, TextUtil.SplitLines(text));

    [Theory]
    [InlineData("\tx", 4, "    x")]
    [InlineData("ab\tx", 4, "ab  x")]
    [InlineData("abcd\tx", 4, "abcd    x")]
    [InlineData("a\tb\tc", 2, "a b c")]
    [InlineData("ab\n\tx", 4, "ab\n    x")]
    [InlineData("no tabs", 4, "no tabs")]
    [InlineData("\t", 8, "        ")]
    public void ConvertTabsToSpaces_uses_column_tab_stops(string text, int tabSize, string expected)
        => Assert.Equal(expected, TextUtil.ConvertTabsToSpaces(text, tabSize));

    [Fact]
    public void StripBidiControls_removes_every_control()
    {
        var controls = "‪‫‬‭‮⁦⁧⁨⁩‎‏؜";
        Assert.Equal("ab", TextUtil.StripBidiControls("a" + controls + "b"));
        Assert.Equal("plain א text", TextUtil.StripBidiControls("plain א text"));
        const string untouched = "no controls";
        Assert.Same(untouched, TextUtil.StripBidiControls(untouched));
    }

    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("with space.txt", "with space.txt")]
    [InlineData("tab\there", "\"tab\\there\"")]
    [InlineData("new\nline", "\"new\\nline\"")]
    [InlineData("quote\"d", "\"quote\\\"d\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("café.txt", "\"caf\\303\\251.txt\"")]
    [InlineData("bell\a", "\"bell\\a\"")]
    [InlineData("esc\u001b", "\"esc\\033\"")]
    [InlineData("del\u007f", "\"del\\177\"")]
    public void QuoteGitPath_matches_core_quotePath(string path, string expected)
        => Assert.Equal(expected, TextUtil.QuoteGitPath(path));

    [Theory]
    [InlineData("plain.txt")]
    [InlineData("café.txt")]
    [InlineData("日本/a\tb.txt")]
    [InlineData("quote\"d\\path")]
    [InlineData("ctrl\a\b\f\n\r\v\u0001\u001f")]
    [InlineData("emoji \U0001F600.txt")]
    [InlineData("x b/y.txt")]
    public void Quoting_round_trips(string path)
        => Assert.Equal(path, TextUtil.UnquoteGitPath(TextUtil.QuoteGitPath(path)));

    [Theory]
    [InlineData("\"a\\tb\"", "a\tb")]
    [InlineData("\"\\a\\b\\f\\n\\r\\v\"", "\a\b\f\n\r\v")]
    [InlineData("\"\\360\\237\\230\\200\"", "\U0001F600")]
    [InlineData("\"unterminated", "\"unterminated")]
    [InlineData("\"", "\"")]
    [InlineData("", "")]
    public void UnquoteGitPath_decodes_escapes(string quoted, string expected)
        => Assert.Equal(expected, TextUtil.UnquoteGitPath(quoted));
}
