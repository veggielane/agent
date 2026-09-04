using System.Text.Json;
using Agent.Cli.Backends;
using Agent.Infrastructure.Keycloak.DeviceFlow;
using Agent.Infrastructure.Keycloak.TokenCache;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Agent.Cli.Commands;

public sealed class LoginCommand : AsyncCommand<GlobalSettings>
{
    private readonly IKeycloakDeviceFlowClient _keycloak;
    private readonly ITokenCache _cache;

    public LoginCommand(IKeycloakDeviceFlowClient keycloak, ITokenCache cache)
    {
        _keycloak = keycloak;
        _cache = cache;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings, CancellationToken cancellationToken)
    {
        return await Cli.Run(async () =>
        {
            var authorization = await _keycloak.StartAsync("openid profile email", cancellationToken);
            var url = authorization.VerificationUriComplete ?? authorization.VerificationUri;
            AnsiConsole.MarkupLineInterpolated($"Open [link]{url}[/] and enter code [bold]{authorization.UserCode}[/] if asked.");
            TryOpenBrowser(url);

            var tokens = await AnsiConsole.Status().StartAsync("Waiting for sign-in…", _ => _keycloak.PollAsync(authorization, cancellationToken));
            await _cache.SaveAsync(tokens, cancellationToken);
            AnsiConsole.MarkupLine("[green]Logged in.[/] Tokens are cached for this user.");
        });
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // The URL was printed; opening a browser is a convenience only.
        }
    }
}

public sealed class LogoutCommand : AsyncCommand<GlobalSettings>
{
    private readonly ITokenCache _cache;

    public LogoutCommand(ITokenCache cache) => _cache = cache;

    protected override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings, CancellationToken cancellationToken)
    {
        await _cache.ClearAsync(cancellationToken);
        AnsiConsole.MarkupLine("[green]Logged out.[/]");
        return 0;
    }
}

public sealed class WhoAmICommand : AsyncCommand<GlobalSettings>
{
    private readonly BackendFactory _backends;

    public WhoAmICommand(BackendFactory backends) => _backends = backends;

    protected override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings, CancellationToken cancellationToken)
    {
        var backend = _backends.Create(settings);
        await using var _ = backend as IAsyncDisposable;

        return await Cli.Run(async () =>
        {
            var me = await backend.WhoAmIAsync(cancellationToken);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(me, Cli.JsonOptions));
                return;
            }

            AnsiConsole.MarkupLineInterpolated($"[bold]{me.Username ?? me.Id}[/] ({backend.Description})");
            AnsiConsole.MarkupLineInterpolated($"email: {me.Email ?? "—"}");
            AnsiConsole.MarkupLineInterpolated($"roles: {(me.Roles.Count == 0 ? "none" : string.Join(", ", me.Roles))}");
            AnsiConsole.MarkupLineInterpolated($"groups: {(me.Groups.Count == 0 ? "none" : string.Join(", ", me.Groups))}");
        });
    }
}
