using Agent.Core.Audit;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Authorization;

public sealed class AuthorizationService : IAuthorizationService
{
    private readonly IRoleResolver _roles;
    private readonly IAuditSink _audit;
    private readonly ILogger<AuthorizationService> _logger;

    public AuthorizationService(IRoleResolver roles, IAuditSink audit, ILogger<AuthorizationService> logger)
    {
        _roles = roles;
        _audit = audit;
        _logger = logger;
    }

    public async Task<AuthorizationResult> AuthorizeAsync(CallerIdentity caller, Role required, string action, CancellationToken cancellationToken)
    {
        var resolved = await _roles.ResolveAsync(caller, cancellationToken).ConfigureAwait(false);
        var allowed = resolved.Roles.Satisfies(required);

        var result = allowed
            ? AuthorizationResult.Allow(required, resolved)
            : AuthorizationResult.Deny(required, resolved, $"requires role {required}; caller has [{string.Join(",", resolved.Roles)}]");

        await _audit.WriteAsync(new AuditEntry(
            DateTimeOffset.UtcNow,
            resolved.Channel,
            resolved.ChannelUserId,
            resolved.DisplayName,
            action,
            allowed ? "allow" : "deny",
            result.Reason,
            resolved.Roles), cancellationToken).ConfigureAwait(false);

        if (!allowed)
        {
            _logger.LogInformation("Denied {Action} for {Caller}: {Reason}", action, resolved.Key, result.Reason);
        }

        return result;
    }
}
