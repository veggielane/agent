using System.Text.Json;
using Agent.Cli.Backends;
using Spectre.Console;

namespace Agent.Cli.Commands;

internal static class Cli
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Runs an action and maps known failures to exit codes and friendly messages.</summary>
    public static async Task<int> Run(Func<Task> action)
    {
        try
        {
            await action();
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
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Cannot reach the agent host: {ex.Message}[/]");
            return 3;
        }
    }
}
