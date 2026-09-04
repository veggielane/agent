using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agent.Infrastructure.Keycloak;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agent.Infrastructure.Tests.Keycloak;

public sealed class ClaimsGroupExtractorTests
{
    private static readonly KeycloakOptions Options = new() { Authority = "https://sso/realms/corp" };

    [Fact]
    public void Extract_FromJsonPayload_ReturnsGroupsAndPrefixedRoles()
    {
        using var payload = JsonDocument.Parse("""
            {
              "sub": "u-1",
              "groups": ["/agent/team", "/agent/users", "/agent/team"],
              "realm_access": { "roles": ["developer", "offline_access"] },
              "resource_access": { "agent-api": { "roles": ["api-user"] } }
            }
            """);

        var groups = ClaimsGroupExtractor.Extract(payload.RootElement, Options);

        Assert.Equal(["/agent/team", "/agent/users", "role:developer", "role:offline_access"], groups);
    }

    [Fact]
    public void Extract_FromJsonPayload_HonoursConfiguredPaths()
    {
        var options = new KeycloakOptions { Authority = "https://sso/realms/corp", GroupClaim = "memberOf", RolesClaimPath = "resource_access.agent-api.roles" };
        using var payload = JsonDocument.Parse("""{ "memberOf": "CN=Team,DC=corp", "resource_access": { "agent-api": { "roles": ["api-user"] } } }""");

        var groups = ClaimsGroupExtractor.Extract(payload.RootElement, options);

        Assert.Equal(["CN=Team,DC=corp", "role:api-user"], groups);
    }

    [Fact]
    public void Extract_FromJsonPayload_MissingClaims_ReturnsEmpty()
    {
        using var payload = JsonDocument.Parse("""{ "sub": "u-1" }""");

        Assert.Empty(ClaimsGroupExtractor.Extract(payload.RootElement, Options));
    }

    [Fact]
    public void Extract_FromPrincipalWithRepeatedAndJsonClaims_ReturnsGroupsAndPrefixedRoles()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("groups", "/agent/team"),
            new Claim("groups", "/agent/users"),
            new Claim("realm_access", """{"roles":["developer","offline_access"]}""", "JSON"),
            new Claim("sub", "u-1"),
        ]);

        var groups = ClaimsGroupExtractor.Extract(new ClaimsPrincipal(identity), Options);

        Assert.Equal(["/agent/team", "/agent/users", "role:developer", "role:offline_access"], groups);
    }

    [Fact]
    public void Extract_FromPrincipalWithArrayValuedClaim_SplitsIt()
    {
        var identity = new ClaimsIdentity([new Claim("groups", """["/agent/team","/agent/admin"]""", "JSON_ARRAY")]);

        var groups = ClaimsGroupExtractor.Extract(new ClaimsPrincipal(identity), Options);

        Assert.Equal(["/agent/team", "/agent/admin"], groups);
    }

    [Fact]
    public void Extract_FromPrincipalBuiltFromRealJwt_ReturnsGroupsAndPrefixedRoles()
    {
        var handler = new JsonWebTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://sso/realms/corp",
            Audience = "agent-api",
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "u-1",
                ["preferred_username"] = "alice",
                ["groups"] = new[] { "/agent/team", "/agent/users" },
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "developer" } },
            },
        });

        var jwt = handler.ReadJsonWebToken(token);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "Bearer"));

        var groups = ClaimsGroupExtractor.Extract(principal, Options);

        Assert.Equal(["/agent/team", "/agent/users", "role:developer"], groups);
    }
}
