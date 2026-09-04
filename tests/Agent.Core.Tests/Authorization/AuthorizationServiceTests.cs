using Agent.Core.Authorization;
using Agent.Core.Tests.Support;

namespace Agent.Core.Tests.Authorization;

public sealed class AuthorizationServiceTests
{
    [Fact]
    public async Task Authorize_Allow_WritesAuditAndReturnsResolvedCaller()
    {
        using var host = TestHost.Create();
        var result = await host.Get<IAuthorizationService>().AuthorizeAsync(TestHost.Caller("bob"), Role.Team, "task.create", TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.True(result.Caller.RolesResolved);
        var entry = Assert.Single(host.Audit.Entries);
        Assert.Equal("allow", entry.Outcome);
        Assert.Equal("task.create", entry.Action);
        Assert.Equal("bob", entry.CallerId);
    }

    [Fact]
    public async Task Authorize_Deny_ExplainsMissingRole()
    {
        using var host = TestHost.Create();
        var result = await host.Get<IAuthorizationService>().AuthorizeAsync(TestHost.Caller("alice"), Role.Admin, "command:reload", TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Contains("requires role Admin", result.Reason);
        Assert.Equal("deny", host.Audit.Entries.Single().Outcome);
    }
}
