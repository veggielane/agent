using Agent.Channels.Jira;
using Agent.Core.Channels;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraWikiFormatterTests
{
    private readonly JiraWikiFormatter _formatter = new();

    [Theory]
    [InlineData("# Title", "h1. Title")]
    [InlineData("## Sub *title*", "h2. Sub _title_")]
    [InlineData("###### Six", "h6. Six")]
    [InlineData("**bold**", "*bold*")]
    [InlineData("__bold__", "*bold*")]
    [InlineData("*italic*", "_italic_")]
    [InlineData("_italic_", "_italic_")]
    [InlineData("***both***", "_*both*_")]
    [InlineData("~~gone~~", "-gone-")]
    [InlineData("`code`", "{{code}}")]
    [InlineData("call `Foo()` now", "call {{Foo()}} now")]
    [InlineData("[text](https://x.y/z)", "[text|https://x.y/z]")]
    [InlineData("[https://x.y/z](https://x.y/z)", "[https://x.y/z]")]
    [InlineData("<https://x.y/z>", "[https://x.y/z]")]
    [InlineData("see https://x.y/z now", "see [https://x.y/z] now")]
    [InlineData("![alt](https://x.y/i.png)", "!https://x.y/i.png!")]
    [InlineData("---", "----")]
    [InlineData("***", "----")]
    [InlineData("- a\n- b", "* a\n* b")]
    [InlineData("* a\n* b", "* a\n* b")]
    [InlineData("1. a\n2. b", "# a\n# b")]
    [InlineData("- a\n  - b\n    1. c", "* a\n** b\n**# c")]
    [InlineData("1. a\n   - b", "# a\n#* b")]
    [InlineData("> quoted", "{quote}\nquoted\n{quote}")]
    [InlineData("> line one\n> line two", "{quote}\nline one\nline two\n{quote}")]
    [InlineData("```csharp\nvar x = 1;\n```", "{code:c#}\nvar x = 1;\n{code}")]
    [InlineData("```\nplain\n```", "{code}\nplain\n{code}")]
    [InlineData("```js\nlet a;\n```", "{code:javascript}\nlet a;\n{code}")]
    [InlineData("```sh\nls -la\n```", "{code:bash}\nls -la\n{code}")]
    [InlineData("```text\nnothing\n```", "{code}\nnothing\n{code}")]
    [InlineData("    indented\n    code", "{code}\nindented\ncode\n{code}")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "||a||b||\n|1|2|")]
    [InlineData("| a | b |\n|---|---|\n| 1 |  |", "||a||b||\n|1| |")]
    [InlineData("| a |\n|---|\n| x \\| y |", "||a||\n|x &#124; y|")]
    [InlineData("| a |\n|---|\n| [l](https://x.y) |", "||a||\n|[l|https://x.y]|")]
    [InlineData("one\n\ntwo", "one\n\ntwo")]
    [InlineData("line one\nline two", "line one\nline two")]
    [InlineData("hard  \nbreak", "hard\nbreak")]
    [InlineData("a<br>b", "a\nb")]
    [InlineData("**bold** and *italic* and `code`", "*bold* and _italic_ and {{code}}")]
    [InlineData("- **bold** item", "* *bold* item")]
    [InlineData("a &amp; b", "a & b")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void Format_ConvertsMarkdownToWiki(string markdown, string expected)
    {
        Assert.Equal(expected, _formatter.Format(markdown));
    }

    [Fact]
    public void Format_MixedDocument_KeepsStructureAndNeverEmitsFences()
    {
        const string markdown = """
            # Result

            I changed **two** files:

            - `src/A.cs` – fixed the *null* check
            - `src/B.cs`
              - nested note

            ```csharp
            if (x is null) return;
            ```

            > Note: run the tests.

            | File | Status |
            |------|--------|
            | A.cs | done |
            | B.cs | pending |

            ---

            Done. See [the MR](https://gitlab.corp.local/t/r/-/merge_requests/1).
            """;

        var wiki = _formatter.Format(markdown);

        const string expected = """
            h1. Result

            I changed *two* files:

            * {{src/A.cs}} – fixed the _null_ check
            * {{src/B.cs}}
            ** nested note

            {code:c#}
            if (x is null) return;
            {code}

            {quote}
            Note: run the tests.
            {quote}

            ||File||Status||
            |A.cs|done|
            |B.cs|pending|

            ----

            Done. See [the MR|https://gitlab.corp.local/t/r/-/merge_requests/1].
            """;
        Assert.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), wiki);
        Assert.DoesNotContain("```", wiki);
    }

    [Fact]
    public void Format_CodeBlockInsideListAndQuote_IsRendered()
    {
        const string markdown = "- item\n\n  ```\n  code\n  ```\n\n> quote\n>\n> ```js\n> x\n> ```";

        var wiki = _formatter.Format(markdown);

        Assert.Equal("* item\n{code}\ncode\n{code}\n\n{quote}\nquote\n\n{code:javascript}\nx\n{code}\n{quote}", wiki);
    }

    [Fact]
    public void Format_UnclosedFence_StillBecomesCodeMacro()
    {
        var wiki = _formatter.Format("```\nstill code");

        Assert.Equal("{code}\nstill code\n{code}", wiki);
        Assert.DoesNotContain("```", wiki);
    }

    [Fact]
    public void Format_InlineCodeWithBraces_IsEscaped()
    {
        Assert.Equal("{{\\{code\\}}}", _formatter.Format("`{code}`"));
    }

    [Fact]
    public void Channel_IsJira()
    {
        Assert.Equal(Channel.Jira, _formatter.Channel);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("cs", "c#")]
    [InlineData("C#", "c#")]
    [InlineData("TypeScript", "javascript")]
    [InlineData("yml", "yaml")]
    [InlineData("py", "python")]
    [InlineData("cpp", "c++")]
    [InlineData("go", "go")]
    [InlineData("sql title=x", "sql")]
    [InlineData("text", null)]
    [InlineData("we{ird}", "weird")]
    public void MapLanguage_NormalizesFenceInfo(string? info, string? expected)
    {
        Assert.Equal(expected, JiraWikiFormatter.MapLanguage(info));
    }
}
