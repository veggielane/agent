using Agent.Cli.Backends;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Agent.Cli.Commands;

public sealed class ChatSettings : GlobalSettings
{
}

/// <summary>Interactive REPL. `!commands` work exactly as in Mattermost; `/quit` leaves, `/new` starts a fresh conversation.</summary>
public sealed class ChatCommand : AsyncCommand<ChatSettings>
{
    private readonly BackendFactory _backends;

    public ChatCommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, ChatSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        var conversationId = Guid.NewGuid().ToString("N");
        AnsiConsole.MarkupLineInterpolated($"[grey]agent chat — {backend.Description}. Type a question, a !command, /new, or /quit.[/]");

        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Markup("[bold green]you>[/] ");
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line is "/quit" or "/exit" or "/q")
            {
                break;
            }

            if (line == "/new")
            {
                conversationId = Guid.NewGuid().ToString("N");
                AnsiConsole.MarkupLine("[grey]new conversation[/]");
                continue;
            }

            try
            {
                AnsiConsole.Markup("[bold blue]agent>[/] ");
                await foreach (var chunk in backend.StreamAsync(line, conversationId, settings.Model, cancellationToken))
                {
                    AnsiConsole.Write(chunk);
                }

                AnsiConsole.WriteLine();
            }
            catch (CliException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error: {ex.Message}[/]");
            }
        }

        return 0;
    }
}
