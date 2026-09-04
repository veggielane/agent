using System.Security.Claims;
using Agent.Core.Authorization;
using Agent.Host.Api;
using NSubstitute;

namespace Agent.Host.Tests;

public sealed class ApiCallerTests
{
    [Fact]
    public void ExtractGroups_HandlesRepeatedClaims_JsonArrays_AndRealmRoles()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("groups", "/agent/users"),
            new Claim("groups", "[\"/agent/team\",\"/x\"]"),
            new Claim("realm_access", "{\"roles\":[\"dev\",\"ops\"]}"),
        ]);

        var groups = ApiCaller.ExtractGroups(new ClaimsPrincipal(identity), "groups");

        Assert.Contains("/agent/users", groups);
        Assert.Contains("/agent/team", groups);
        Assert.Contains("/x", groups);
        Assert.Contains("role:dev", groups);
        Assert.Contains("role:ops", groups);
    }

    [Fact]
    public void From_BuildsCliIdentityWithRoles()
    {
        var resolver = Substitute.For<IRoleResolver>();
        resolver.RolesFromGroups(Arg.Any<IEnumerable<string>>()).Returns(new HashSet<Role> { Role.Users, Role.Team });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "42"), new Claim("preferred_username", "bob"), new Claim("email", "b@x"), new Claim("groups", "/agent/team")], "test"));

        var caller = ApiCaller.From(principal, resolver);

        Assert.Equal(Core.Channels.Channel.Cli, caller.Channel);
        Assert.Equal("42", caller.ChannelUserId);
        Assert.Equal("bob", caller.Username);
        Assert.Equal("b@x", caller.Email);
        Assert.True(caller.RolesResolved);
        Assert.True(caller.HasRole(Role.Team));
    }
}
