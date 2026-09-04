namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostMessageSplitterTests
{
    [Fact]
    public void Split_ShortText_ReturnsSingleChunk()
    {
        var chunks = MattermostMessageSplitter.Split("short reply", 100);

        Assert.Equal(["short reply"], chunks);
    }

    [Fact]
    public void Split_EmptyText_ReturnsNothing()
    {
        Assert.Empty(MattermostMessageSplitter.Split(string.Empty, 100));
    }

    [Fact]
    public void Split_LongText_SplitsAtLineBreaksWithinLimit()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i:D2} of the reply").ToArray();
        var text = string.Join('\n', lines);

        var chunks = MattermostMessageSplitter.Split(text, 100);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 100, $"chunk of {c.Length} chars exceeds the limit"));
        Assert.Equal(lines, chunks.SelectMany(c => c.Split('\n')).ToArray());
    }

    [Fact]
    public void Split_CodeFenceSpanningCut_ClosesAndReopensFence()
    {
        var code = Enumerable.Range(1, 30).Select(i => $"var value{i} = {i};").ToArray();
        var text = "Here is the code:\n```csharp\n" + string.Join('\n', code) + "\n```\nDone.";

        var chunks = MattermostMessageSplitter.Split(text, 120);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 120));
        foreach (var chunk in chunks)
        {
            var fenceLines = chunk.Split('\n').Count(l => l.StartsWith("```", StringComparison.Ordinal));
            Assert.True(fenceLines % 2 == 0, $"unbalanced fence in chunk: {chunk}");
        }

        Assert.EndsWith("\n```", chunks[0], StringComparison.Ordinal);
        Assert.StartsWith("```csharp\n", chunks[1], StringComparison.Ordinal);
        Assert.EndsWith("Done.", chunks[^1], StringComparison.Ordinal);

        var codeLines = chunks.SelectMany(c => c.Split('\n')).Where(l => l.StartsWith("var ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(code, codeLines);
    }

    [Fact]
    public void Split_OverflowingLineRightAfterOpener_DoesNotCreateEmptyBlock()
    {
        // The opener fits, the first code line does not once the closing fence is reserved.
        var text = "```\n" + new string('x', 90) + "\n```";

        var chunks = MattermostMessageSplitter.Split(text, 96);

        Assert.All(chunks, c => Assert.True(c.Length <= 96));
        Assert.DoesNotContain("```\n```", chunks);
        Assert.All(chunks, c => Assert.True(c.Split('\n').Count(l => l == "```") % 2 == 0));
        var body = string.Concat(chunks.SelectMany(c => c.Split('\n')).Where(l => l != "```"));
        Assert.Equal(new string('x', 90), body);
    }

    [Fact]
    public void Split_FenceOpenedAtEndOfChunk_MovesOpenerToNextChunk()
    {
        var intro = new string('i', 80);
        var text = intro + "\n```csharp\nvar a = 1;\n```";

        var chunks = MattermostMessageSplitter.Split(text, 96);

        Assert.Equal([intro, "```csharp\nvar a = 1;\n```"], chunks);
    }

    [Fact]
    public void Split_SingleLineLongerThanLimit_IsHardSplit()
    {
        var text = new string('a', 250);

        var chunks = MattermostMessageSplitter.Split(text, 100);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 100));
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void Split_LongLineInsideFence_KeepsFenceBalanced()
    {
        var text = "```\n" + new string('b', 200) + "\n```";

        var chunks = MattermostMessageSplitter.Split(text, 100);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Length <= 100));
        Assert.All(chunks, c => Assert.True(c.Split('\n').Count(l => l == "```") % 2 == 0, $"unbalanced: {c}"));
        var body = string.Concat(chunks.SelectMany(c => c.Split('\n')).Where(l => l != "```"));
        Assert.Equal(new string('b', 200), body);
    }

    [Fact]
    public void Split_TooSmallLimit_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MattermostMessageSplitter.Split("x", 2));
    }
}
