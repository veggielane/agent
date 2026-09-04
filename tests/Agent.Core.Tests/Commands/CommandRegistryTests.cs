using Agent.Core.Commands;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Commands;

public sealed class CommandRegistryTests
{
    [Fact]
    public void Registry_ContainsBuiltIns_AndSampleCommands()
    {
        using var host = TestHost.Create(s => s.AddCommandHandlers<SampleCommands>());
        var registry = host.Get<ICommandRegistry>();

        Assert.True(registry.TryGet("help", out _));
        Assert.True(registry.TryGet("greet", out var greet));
        Assert.Equal("SampleCommands", greet.Source);
        Assert.True(registry.TryGet("TO", out var alias));
        Assert.Equal("teamonly", alias.Name);
        Assert.Empty(registry.Problems);
    }

    [Fact]
    public void Registry_ReportsDuplicates_AndKeepsFirst()
    {
        using var host = TestHost.Create(s =>
        {
            s.AddCommandHandlers<SampleCommands>();
            s.AddCommandHandlers<DuplicateCommands>();
        });
        var registry = host.Get<ICommandRegistry>();

        Assert.Single(registry.Problems);
        Assert.Contains("greet", registry.Problems[0]);
        Assert.True(registry.TryGet("greet", out var greet));
        Assert.Equal("SampleCommands", greet.Source);
    }

    [Fact]
    public void Registry_ReportsDefinitionErrors_WithoutFailing()
    {
        using var host = TestHost.Create(s => s.AddCommandHandlers<NoRoleCommands>());
        var registry = host.Get<ICommandRegistry>();

        Assert.True(registry.TryGet("ping", out _));
        Assert.Contains(registry.Problems, p => p.Contains("norole", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reload_PicksUpNewYamlFiles()
    {
        using var host = TestHost.Create();
        var registry = host.Get<ICommandRegistry>();
        Assert.False(registry.TryGet("yammy", out _));

        File.WriteAllText(Path.Combine(host.CommandsDir, "yammy.yaml"), "name: yammy\ndescription: test\nrole: users\nprompt: hi {{args}}\n");
        registry.Reload();

        Assert.True(registry.TryGet("yammy", out var cmd));
        Assert.Equal("yammy.yaml", cmd.Source);
    }
}
