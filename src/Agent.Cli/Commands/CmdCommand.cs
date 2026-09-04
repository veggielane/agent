using System.ComponentModel;
using Agent.Cli.Backends;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Agent.Cli.Commands;

public sealed class CmdSettings : GlobalSettings
{
    [CommandArgument(0, "<name>")]
    [Description("Command name without the prefix, e.g. status.")]
    public string Name { get; init; } = string.Empty;

    [CommandArgument(1, "[args]")]
    [Description("Arguments for the command.")]
    public string[] Args { get; init; } = [];
}

/// <summary>Runs a single !command non-interactively: `agent cmd tasks --all`.</summary>
public sealed class CmdCommand : AsyncCommand<CmdSettings>
{
    private readonly BackendFactory _backends;

    public CmdCommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, CmdSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        var text = "!" + settings.Name.TrimStart('!') + (settings.Args.Length == 0 ? string.Empty : " " + string.Join(' ', settings.Args.Select(Quote)));
        try
        {
            var result = await backend.ChatAsync(text, null, null, cancellationToken);
            Console.WriteLine(result.Markdown);
            return result.IsError ? 1 : 0;
        }
        catch (CliException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 2;
        }
    }

    private static string Quote(string arg) => arg.Contains(' ') && !arg.StartsWith('"') ? $"\"{arg.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : arg;
}
