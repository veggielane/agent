using System.ComponentModel;
using System.Text.Json;
using Agent.Cli.Backends;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Agent.Cli.Commands;

public sealed class TaskCreateSettings : GlobalSettings
{
    [CommandArgument(0, "<instruction>")]
    public string Instruction { get; init; } = string.Empty;

    [CommandOption("-r|--repo <URL>")]
    [Description("GitLab repository URL.")]
    public string Repo { get; init; } = string.Empty;

    [CommandOption("--title <TITLE>")]
    public string? Title { get; init; }

    [CommandOption("--branch <BRANCH>")]
    [Description("Base branch (default: the repository's default branch).")]
    public string? Branch { get; init; }
}

public sealed class TaskListSettings : GlobalSettings
{
    [CommandOption("--all")]
    [Description("Everyone's tasks (Team role).")]
    public bool All { get; init; }

    [CommandOption("--limit <N>")]
    public int Limit { get; init; } = 20;
}

public class TaskIdSettings : GlobalSettings
{
    [CommandArgument(0, "<id>")]
    public int Id { get; init; }
}

public sealed class TaskLogSettings : TaskIdSettings
{
    [CommandOption("-f|--follow")]
    [Description("Keep polling for new events until the task finishes.")]
    public bool Follow { get; init; }
}

public sealed class TaskCreateCommand : AsyncCommand<TaskCreateSettings>
{
    private readonly BackendFactory _backends;

    public TaskCreateCommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, TaskCreateSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        if (string.IsNullOrWhiteSpace(settings.Repo))
        {
            AnsiConsole.MarkupLine("[red]--repo is required[/]");
            return 1;
        }

        return await Cli.Run(async () =>
        {
            var task = await backend.CreateTaskAsync(settings.Repo, settings.Instruction, settings.Title, settings.Branch, cancellationToken);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(task, Cli.JsonOptions));
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"Created task [bold]#{task.Id}[/] ({task.Status}). Follow it with [grey]agent task log {task.Id} --follow[/].");
            }
        });
    }
}

public sealed class TaskListCommand : AsyncCommand<TaskListSettings>
{
    private readonly BackendFactory _backends;

    public TaskListCommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, TaskListSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        return await Cli.Run(async () =>
        {
            var tasks = await backend.ListTasksAsync(settings.All, settings.Limit, cancellationToken);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(tasks, Cli.JsonOptions));
                return;
            }

            if (tasks.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]no tasks[/]");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumns("#", "status", "source", "title", "MR", "updated");
            foreach (var t in tasks)
            {
                table.AddRow(
                    t.Id.ToString(),
                    Markup.Escape(t.Status),
                    Markup.Escape(t.SourceRef),
                    Markup.Escape(Shorten(t.Title ?? string.Empty, 50)),
                    Markup.Escape(t.MergeRequestUrl ?? "—"),
                    t.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
        });
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public sealed class TaskShowCommand : AsyncCommand<TaskIdSettings>
{
    private readonly BackendFactory _backends;

    public TaskShowCommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, TaskIdSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        return await Cli.Run(async () =>
        {
            var task = await backend.GetTaskAsync(settings.Id, cancellationToken) ?? throw new CliException($"No task #{settings.Id}.");
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(task, Cli.JsonOptions));
                return;
            }

            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("id", task.Id.ToString());
            grid.AddRow("status", Markup.Escape(task.Status));
            grid.AddRow("source", Markup.Escape($"{task.Source} {task.SourceRef}"));
            grid.AddRow("requester", Markup.Escape(task.RequesterName));
            grid.AddRow("repo", Markup.Escape(task.RepoUrl ?? "—"));
            grid.AddRow("branch", Markup.Escape(task.WorkBranch ?? "—"));
            grid.AddRow("merge request", Markup.Escape(task.MergeRequestUrl ?? "—"));
            grid.AddRow("turns / tokens", $"{task.Turns} / {task.TokensUsed}");
            grid.AddRow("summary", Markup.Escape(task.Summary ?? "—"));
            grid.AddRow("error", Markup.Escape(task.Error ?? "—"));
            AnsiConsole.Write(grid);
        });
    }
}

public sealed class TaskLogCommand : AsyncCommand<TaskLogSettings>
{
    private readonly BackendFactory _backends;

    public TaskLogCommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, TaskLogSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        return await Cli.Run(async () =>
        {
            var seen = 0;
            while (true)
            {
                var events = await backend.GetTaskEventsAsync(settings.Id, cancellationToken);
                foreach (var e in events.Skip(seen))
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]{e.At.ToLocalTime():HH:mm:ss}[/] [bold]{e.Type}[/] {e.Message}");
                }

                seen = events.Count;
                if (!settings.Follow)
                {
                    return;
                }

                var task = await backend.GetTaskAsync(settings.Id, cancellationToken);
                if (task is null || task.Status is "Done" or "Closed" or "Failed" or "Cancelled" or "AwaitingReview" or "NeedsInput")
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]task is {task?.Status ?? "gone"}[/]");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        });
    }
}

public sealed class TaskCancelCommand : AsyncCommand<TaskIdSettings>
{
    private readonly BackendFactory _backends;

    public TaskCancelCommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, TaskIdSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        return await Cli.Run(async () =>
        {
            var task = await backend.CancelTaskAsync(settings.Id, cancellationToken) ?? throw new CliException($"No task #{settings.Id}.");
            AnsiConsole.MarkupLineInterpolated($"Task #{task.Id} is now {task.Status}.");
        });
    }
}
