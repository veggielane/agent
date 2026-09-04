using Agent.Core.Authorization;
using Agent.Core.Events;

namespace Agent.Core.Pipeline;

/// <summary>Ambient per-request data so tools and audit can see who is calling without plumbing.</summary>
public sealed record RequestScope(CallerIdentity Caller, InboundEvent? Event);

public static class RequestContext
{
    private static readonly AsyncLocal<RequestScope?> Scope = new();

    public static RequestScope? Current => Scope.Value;

    public static IDisposable Begin(CallerIdentity caller, InboundEvent? evt = null)
    {
        var previous = Scope.Value;
        Scope.Value = new RequestScope(caller, evt);
        return new Restore(previous);
    }

    private sealed class Restore(RequestScope? previous) : IDisposable
    {
        public void Dispose() => Scope.Value = previous;
    }
}
