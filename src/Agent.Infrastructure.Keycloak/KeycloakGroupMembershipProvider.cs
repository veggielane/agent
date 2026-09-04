using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Core.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Infrastructure.Keycloak;

/// <summary>
/// Resolves a caller's Keycloak groups through the Admin REST API using a confidential service client.
/// Returns both group paths (<c>/agent/team</c>) and names (<c>team</c>), plus realm roles as <c>role:{name}</c>,
/// so <c>Authorization.Roles</c> can reference any of them. Throws on transport / server errors so the
/// role resolver fails closed.
/// </summary>
public sealed class KeycloakGroupMembershipProvider : IGroupMembershipProvider
{
    public const string ProviderName = "Keycloak";

    private static readonly TimeSpan TokenExpirySkew = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<KeycloakOptions> _options;
    private readonly ILogger<KeycloakGroupMembershipProvider> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;

    public KeycloakGroupMembershipProvider(
        HttpClient http,
        IOptionsMonitor<KeycloakOptions> options,
        ILogger<KeycloakGroupMembershipProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Name => ProviderName;

    public async Task<IReadOnlyCollection<string>> GetGroupsAsync(CallerIdentity identity, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var user = await FindUserAsync(identity, options, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogDebug("Keycloak: no user found for {Caller}", identity.Key);
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var groups = await GetAsync<List<KeycloakGroup>>(
            options,
            $"users/{Uri.EscapeDataString(user.Id)}/groups?briefRepresentation=true",
            cancellationToken).ConfigureAwait(false);

        foreach (var group in groups ?? [])
        {
            Add(group.Path);
            Add(group.Name);
        }

        if (options.IncludeRealmRoles)
        {
            var roles = await GetAsync<List<KeycloakRole>>(
                options,
                $"users/{Uri.EscapeDataString(user.Id)}/role-mappings/realm",
                cancellationToken).ConfigureAwait(false);

            foreach (var role in roles ?? [])
            {
                if (!string.IsNullOrWhiteSpace(role.Name))
                {
                    Add("role:" + role.Name);
                }
            }
        }

        _logger.LogDebug("Keycloak: {Caller} → user {UserId} with {Count} group identifiers", identity.Key, user.Id, result.Count);
        return result;

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                result.Add(value);
            }
        }
    }

    private async Task<KeycloakUser?> FindUserAsync(CallerIdentity identity, KeycloakOptions options, CancellationToken cancellationToken)
    {
        foreach (var username in new[] { identity.Username, identity.Upn }.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var byName = await GetAsync<List<KeycloakUser>>(options, $"users?username={Uri.EscapeDataString(username!)}&exact=true", cancellationToken).ConfigureAwait(false);
            var match = byName?.FirstOrDefault(u => !string.IsNullOrEmpty(u.Id));
            if (match is not null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(identity.Email))
        {
            var byEmail = await GetAsync<List<KeycloakUser>>(options, $"users?email={Uri.EscapeDataString(identity.Email)}&exact=true", cancellationToken).ConfigureAwait(false);
            var match = byEmail?.FirstOrDefault(u => !string.IsNullOrEmpty(u.Id));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private async Task<T?> GetAsync<T>(KeycloakOptions options, string relative, CancellationToken cancellationToken)
    {
        var uri = new Uri(options.AdminBaseUri, relative);

        for (var attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(options, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                // The cached token was revoked or expired early: fetch a fresh one and retry once.
                InvalidateToken();
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Keycloak admin request GET {uri.AbsolutePath} failed with {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}",
                    null,
                    response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetAccessTokenAsync(KeycloakOptions options, CancellationToken cancellationToken)
    {
        if (TryGetCachedToken(out var cached))
        {
            return cached;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedToken(out cached))
            {
                return cached;
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.Admin.ClientId,
                ["client_secret"] = options.Admin.ClientSecret,
            });

            using var response = await _http.PostAsync(new Uri(options.TokenEndpoint), content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Keycloak token request for client '{options.Admin.ClientId}' failed with {(int)response.StatusCode}: {Truncate(body)}",
                    null,
                    response.StatusCode);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, cancellationToken).ConfigureAwait(false);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
            {
                throw new HttpRequestException("Keycloak token response did not contain an access_token.");
            }

            _accessToken = token.AccessToken;
            _accessTokenExpires = _time.GetUtcNow().AddSeconds(Math.Max(0, token.ExpiresIn)) - TokenExpirySkew;
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool TryGetCachedToken(out string token)
    {
        var current = _accessToken;
        if (current is not null && _time.GetUtcNow() < _accessTokenExpires)
        {
            token = current;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private void InvalidateToken()
    {
        _accessToken = null;
        _accessTokenExpires = DateTimeOffset.MinValue;
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";

    private sealed record KeycloakUser(string Id, string? Username, string? Email);

    private sealed record KeycloakGroup(string? Id, string? Name, string? Path);

    private sealed record KeycloakRole(string? Id, string? Name);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
