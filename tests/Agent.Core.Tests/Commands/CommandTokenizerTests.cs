using Agent.Core.Commands;

namespace Agent.Core.Tests.Commands;

public sealed class CommandTokenizerTests
{
    [Fact]
    public void Tokenize_PlainWords_SplitsOnWhitespace()
    {
        var tokens = CommandTokenizer.Tokenize("  a   b\tc ");
        Assert.Equal(["a", "b", "c"], tokens.Select(t => t.Value));
        Assert.Equal(2, tokens[0].Start);
    }

    [Fact]
    public void Tokenize_DoubleQuotes_KeepsSpacesAndMarksQuoted()
    {
        var tokens = CommandTokenizer.Tokenize("say \"hello world\" --x=1");
        Assert.Equal(["say", "hello world", "--x=1"], tokens.Select(t => t.Value));
        Assert.True(tokens[1].Quoted);
        Assert.True(tokens[2].IsLongOption);
    }

    [Fact]
    public void Tokenize_SingleQuotesAndEscapes_Work()
    {
        var tokens = CommandTokenizer.Tokenize("'it''s' a\\\"b");
        Assert.Equal("its", tokens[0].Value);
        Assert.Equal("a\"b", tokens[1].Value);
    }

    [Fact]
    public void Tokenize_Empty_ReturnsNoTokens()
    {
        Assert.Empty(CommandTokenizer.Tokenize(string.Empty));
        Assert.Empty(CommandTokenizer.Tokenize("   "));
    }

    [Fact]
    public void Tokenize_ShortOption_Detected()
    {
        var tokens = CommandTokenizer.Tokenize("-t 3 -- x");
        Assert.True(tokens[0].IsShortOption);
        Assert.False(tokens[2].IsShortOption);
        Assert.False(tokens[2].IsLongOption);
    }
}
