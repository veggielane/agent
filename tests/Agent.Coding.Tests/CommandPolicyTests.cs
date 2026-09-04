namespace Agent.Coding.Tests;

public sealed class CommandPolicyTests
{
    private readonly CommandPolicy _policy = new(CodingOptions.DefaultAllowedExecutables);

    [Theory]
    [InlineData("dotnet build", "dotnet", new[] { "build" })]
    [InlineData("dotnet test --no-build", "dotnet", new[] { "test", "--no-build" })]
    [InlineData("npm run lint", "npm", new[] { "run", "lint" })]
    [InlineData("git status --porcelain", "git", new[] { "status", "--porcelain" })]
    [InlineData("dotnet.exe --version", "dotnet.exe", new[] { "--version" })]
    public void Parse_AllowedCommand_ReturnsExecutableAndArguments(string commandLine, string exe, string[] args)
    {
        var parsed = _policy.Parse(commandLine);

        Assert.Equal(exe, parsed.Executable);
        Assert.Equal(args, parsed.Arguments);
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("powershell -c Get-Process")]
    [InlineData("bash -c ls")]
    [InlineData("curl https://example.com")]
    public void Parse_DisallowedExecutable_Throws(string commandLine)
    {
        var ex = Assert.Throws<CodingPolicyException>(() => _policy.Parse(commandLine));

        Assert.Contains("not an allowed executable", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet build | tee out.txt")]
    [InlineData("dotnet build && dotnet test")]
    [InlineData("dotnet build; rm x")]
    [InlineData("dotnet build > out.txt")]
    [InlineData("dotnet build < in.txt")]
    [InlineData("dotnet build `whoami`")]
    [InlineData("dotnet build $(whoami)")]
    [InlineData("dotnet build || true")]
    public void Parse_ShellOperators_Throws(string commandLine)
    {
        var ex = Assert.Throws<CodingPolicyException>(() => _policy.Parse(commandLine));

        Assert.Contains("Shell operators", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MultiLine_Throws()
    {
        Assert.Throws<CodingPolicyException>(() => _policy.Parse("dotnet build\nrm -rf x"));
    }

    [Theory]
    [InlineData("./node_modules/.bin/dotnet build")]
    [InlineData(@"C:\tools\git.exe status")]
    [InlineData("/usr/bin/git status")]
    public void Parse_ExecutablePath_Throws(string commandLine)
    {
        var ex = Assert.Throws<CodingPolicyException>(() => _policy.Parse(commandLine));

        Assert.Contains("bare executable name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_QuotedArguments_KeepSpacesAndOperators()
    {
        var parsed = _policy.Parse("dotnet test --filter \"Name~Foo|Bar\" 'my dir/x.csproj'");

        Assert.Equal(["test", "--filter", "Name~Foo|Bar", "my dir/x.csproj"], parsed.Arguments);
    }

    [Fact]
    public void Parse_UnterminatedQuote_Throws()
    {
        Assert.Throws<CodingPolicyException>(() => _policy.Parse("dotnet test --filter \"Name"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_Empty_Throws(string? commandLine)
    {
        Assert.Throws<CodingPolicyException>(() => _policy.Parse(commandLine));
    }

    [Fact]
    public void Parse_PerRepoAdditions_AreAllowed()
    {
        var policy = new CommandPolicy(CodingOptions.DefaultAllowedExecutables.Concat(["gradle"]));

        Assert.Equal("gradle", policy.Parse("gradle test").Executable);
    }

    [Theory]
    [InlineData("git push origin main")]
    [InlineData("git config user.name x")]
    [InlineData("git commit -m x")]
    [InlineData("git checkout main")]
    [InlineData("git -c core.hooksPath=x status")]
    [InlineData("git -C .. status")]
    [InlineData("git --git-dir=../other status")]
    public void Parse_GitMutatingSubcommands_AreRejected(string commandLine)
    {
        Assert.Throws<CodingPolicyException>(() => _policy.Parse(commandLine));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git --no-pager diff")]
    [InlineData("git log --oneline -5")]
    [InlineData("git add -A")]
    [InlineData("git --version")]
    public void Parse_GitReadOnlySubcommands_AreAllowed(string commandLine)
    {
        Assert.Equal("git", _policy.Parse(commandLine).Executable);
    }

    [Fact]
    public void Tokenize_HandlesQuotesAndBackslashes()
    {
        var tokens = CommandPolicy.Tokenize(@"dotnet build src\App\App.csproj ""a b"" c");

        Assert.Equal(["dotnet", "build", @"src\App\App.csproj", "a b", "c"], tokens.Select(t => t.Text));
        Assert.True(tokens[3].Quoted);
        Assert.False(tokens[2].Quoted);
    }
}
