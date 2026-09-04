using System.ComponentModel;
using System.Text.Json;
using Agent.Cli.Backends;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Agent.Cli.Commands;

public sealed class AskSettings : GlobalSettings
{
    [CommandArgument(0, "<text>")]
    [Description("The question (or a !command).")]
    public string Text { get; init; } = string.Empty;

    [CommandOption("--no-stream")]
    [Description("Wait for the full answer instead of streaming.")]
    public bool NoStream { get; init; }
}

public sealed class AskCommand : AsyncCommand<AskSettings>
{
    private readonly BackendFactory _backends;

    public AskCommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, AskSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        try
        {
            if (settings.Json || settings.NoStream || Console.IsOutputRedirected)
            {
                var result = await backend.ChatAsync(settings.Text, null, settings.Model, cancellationToken);
                if (settings.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(result, Cli.JsonOptions));
                }
                else
                {
                    Console.WriteLine(result.Markdown);
                }

                return result.IsError ? 1 : 0;
            }

            var conversationId = Guid.NewGuid().ToString("N");
            await foreach (var chunk in backend.StreamAsync(settings.Text, conversationId, settings.Model, cancellationToken))
            {
                AnsiConsole.Write(chunk);
            }

            AnsiConsole.WriteLine();
            return 0;
        }
        catch (CliException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }
}
