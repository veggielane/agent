using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Agent.Core.Tests.Authorization;

public sealed class RoleResolverTests
{
    [Fact]
    public async Task Resolve_MapsGroupsToHierarchicalRoles()
    {
        using var host = TestHost.Create();
        var resolver = host.Get<IRoleResolver>();

        var alice = await resolver.ResolveAsync(TestHost.Caller("alice"), TestContext.Current.CancellationToken);
        Assert.Equal([Role.Users], alice.Roles.OrderBy(r => r));

        var bob = await resolver.ResolveAsync(TestHost.Caller("bob"), TestContext.Current.CancellationToken);
        Assert.Equal([Role.Users, Role.Team], bob.Roles.OrderBy(r => r));

        var root = await resolver.ResolveAsync(TestHost.Caller("root"), TestContext.Current.CancellationToken);
        Assert.True(root.HasRole(Role.Admin) && root.HasRole(Role.Users));
        Assert.True(root.RolesResolved);

        var nobody = await resolver.ResolveAsync(TestHost.Caller("nobody"), TestContext.Current.CancellationToken);
        Assert.Empty(nobody.Roles);
        Assert.True(nobody.RolesResolved);
    }

    [Fact]
    public void RolesFromGroups_IsCaseInsensitive()
    {
        using var host = TestHost.Create();
        var roles = host.Get<IRoleResolver>().RolesFromGroups(["G-TEAM"]);
        Assert.Contains(Role.Team, roles);
        Assert.Contains(Role.Users, roles);
        Assert.DoesNotContain(Role.Admin, roles);
    }

    [Fact]
    public async Task Resolve_UsesCache_WhenEnabled()
    {
        var provider = Substitute.For<IGroupMembershipProvider>();
        provider.Name.Returns("Fake");
        provider.GetGroupsAsync(Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyCollection<string>>(["g-team"]));

        using var host = TestHost.Create(
            s => s.AddSingleton(provider),
            new Dictionary<string, string?> { ["Authorization:Provider"] = "Fake", ["Authorization:CacheMinutes"] = "5" });
        var resolver = host.Get<IRoleResolver>();

        var first = await resolver.ResolveAsync(TestHost.Caller("x"), TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(TestHost.Caller("x"), TestContext.Current.CancellationToken);

        Assert.True(first.HasRole(Role.Team) && second.HasRole(Role.Team));
        await provider.Received(1).GetGroupsAsync(Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());

        resolver.Invalidate(TestHost.Caller("x"));
        await resolver.ResolveAsync(TestHost.Caller("x"), TestContext.Current.CancellationToken);
        await provider.Received(2).GetGroupsAsync(Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_ProviderFailure_FailsClosed()
    {
        var provider = Substitute.For<IGroupMembershipProvider>();
        provider.Name.Returns("Fake");
        provider.GetGroupsAsync(Arg.Any<CallerIdentity>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyCollection<string>>>(_ => throw new HttpRequestException("down"));

        using var host = TestHost.Create(s => s.AddSingleton(provider), new Dictionary<string, string?> { ["Authorization:Provider"] = "Fake" });
        var identity = await host.Get<IRoleResolver>().ResolveAsync(TestHost.Caller("x"), TestContext.Current.CancellationToken);

        Assert.Empty(identity.Roles);
    }

    [Fact]
    public async Task Resolve_UnknownProviderName_FallsBackToFirstRegistered()
    {
        using var host = TestHost.Create(extraConfig: new Dictionary<string, string?> { ["Authorization:Provider"] = "Missing" });
        var identity = await host.Get<IRoleResolver>().ResolveAsync(TestHost.Caller("bob"), TestContext.Current.CancellationToken);
        Assert.True(identity.HasRole(Role.Team));
    }

    [Fact]
    public async Task Resolve_AlreadyResolved_IsUntouched()
    {
        using var host = TestHost.Create();
        var admin = CallerIdentity.Local("someone", Role.Admin);
        var same = await host.Get<IRoleResolver>().ResolveAsync(admin, TestContext.Current.CancellationToken);
        Assert.Same(admin, same);
    }

    [Fact]
    public void Expand_And_Highest()
    {
        Assert.Equal([Role.Users, Role.Team, Role.Admin], new[] { Role.Admin }.Expand().OrderBy(r => r));
        Assert.Equal(Role.Team, new[] { Role.Team }.Expand().Highest());
        Assert.Empty(Array.Empty<Role>().Expand());
        Assert.True(RoleExtensions.TryParseRole("ADMIN", out var role) && role == Role.Admin);
        Assert.False(RoleExtensions.TryParseRole("root", out _));
        Assert.False(RoleExtensions.TryParseRole("0", out _));
    }

    [Fact]
    public void CallerIdentity_KeyAndDisplayName()
    {
        var c = new CallerIdentity(Channel.GitLab, "17", "dev", "dev@x");
        Assert.Equal("GitLab:17", c.Key);
        Assert.Equal("dev", c.DisplayName);
        Assert.Equal("17", new CallerIdentity(Channel.GitLab, "17").DisplayName);
    }
}
