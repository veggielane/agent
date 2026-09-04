namespace Agent.Core.Authorization;

public sealed record AuthorizationResult(bool Allowed, Role Required, CallerIdentity Caller, string? Reason)
{
    public static AuthorizationResult Allow(Role required, CallerIdentity caller) => new(true, required, caller, null);

    public static AuthorizationResult Deny(Role required, CallerIdentity caller, string reason) => new(false, required, caller, reason);
}

/// <summary>Single gate for every request, command, tool, and task instruction. Every decision is audited.</summary>
public interface IAuthorizationService
{
    /// <param name="action">Free text for the audit log, e.g. "ask", "task.create", "command:tasks", "tool:jira_get_issue".</param>
    Task<AuthorizationResult> AuthorizeAsync(CallerIdentity caller, Role required, string action, CancellationToken cancellationToken);
}
