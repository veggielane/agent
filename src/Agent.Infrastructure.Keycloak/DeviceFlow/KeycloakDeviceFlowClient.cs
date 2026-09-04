using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Agent.Infrastructure.Keycloak.DeviceFlow;

public sealed class KeycloakDeviceFlowClient : IKeycloakDeviceFlowClient
{
    public const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowDownIncrement = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<KeycloakOptions> _options;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private OidcDiscovery? _discovery;

    /// <param name="delay">Replaces <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> between polls; tests pass a no-op.</param>
    public KeycloakDeviceFlowClient(
        HttpClient http,
        IOptionsMonitor<KeycloakOptions> options,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
    }

    public async Task<OidcDiscovery> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (_discovery is not null)
        {
            return _discovery;
        }

        await _discoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_discovery is not null)
            {
                return _discovery;
            }

            var options = _options.CurrentValue;
            using var response = await _http.GetAsync(new Uri(options.DiscoveryEndpoint), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;

            var tokenEndpoint = GetString(root, "token_endpoint") ?? options.TokenEndpoint;
            var deviceEndpoint = GetString(root, "device_authorization_endpoint")
                ?? throw new InvalidOperationException($"The realm at {options.Authority} does not advertise a device_authorization_endpoint.");

            _discovery = new OidcDiscovery(
                GetString(root, "issuer") ?? options.AuthorityBase,
                tokenEndpoint,
                deviceEndpoint,
                GetString(root, "authorization_endpoint"),
                GetString(root, "end_session_endpoint"));
            return _discovery;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    public async Task<DeviceAuthorization> StartAsync(string scope = "openid profile email", CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var options = _options.CurrentValue;

        using var response = await PostFormAsync(
            discovery.DeviceAuthorizationEndpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = options.CliClientId,
                ["scope"] = scope,
            },
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToException(response.StatusCode, body);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var now = _time.GetUtcNow();
        var expiresIn = GetInt(root, "expires_in") ?? 600;
        var interval = GetInt(root, "interval") is int i and > 0 ? TimeSpan.FromSeconds(i) : DefaultInterval;

        return new DeviceAuthorization(
            GetString(root, "device_code") ?? throw new InvalidOperationException("Device authorization response has no device_code."),
            GetString(root, "user_code") ?? throw new InvalidOperationException("Device authorization response has no user_code."),
            GetString(root, "verification_uri") ?? throw new InvalidOperationException("Device authorization response has no verification_uri."),
            GetString(root, "verification_uri_complete"),
            now.AddSeconds(expiresIn),
            interval);
    }

    public async Task<TokenSet> PollAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var options = _options.CurrentValue;
        var interval = authorization.Interval > TimeSpan.Zero ? authorization.Interval : DefaultInterval;

        while (true)
        {
            if (_time.GetUtcNow() >= authorization.ExpiresAt)
            {
                throw new DeviceFlowException("expired_token", "The device code expired before the login was approved.");
            }

            await _delay(interval, cancellationToken).ConfigureAwait(false);

            using var response = await PostFormAsync(
                discovery.TokenEndpoint,
                new Dictionary<string, string>
                {
                    ["grant_type"] = DeviceCodeGrant,
                    ["device_code"] = authorization.DeviceCode,
                    ["client_id"] = options.CliClientId,
                },
                cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ParseTokenSet(body);
            }

            var (error, description) = ParseError(body);
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += SlowDownIncrement;
                    continue;
                default:
                    throw ToException(response.StatusCode, body);
            }
        }
    }

    public async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var options = _options.CurrentValue;

        using var response = await PostFormAsync(
            discovery.TokenEndpoint,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = options.CliClientId,
            },
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToException(response.StatusCode, body);
        }

        return ParseTokenSet(body);
    }

    private TokenSet ParseTokenSet(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = GetString(root, "access_token") ?? throw new InvalidOperationException("Token response has no access_token.");
        var expiresIn = GetInt(root, "expires_in") ?? 300;

        return new TokenSet(
            accessToken,
            GetString(root, "refresh_token"),
            _time.GetUtcNow().AddSeconds(expiresIn),
            GetString(root, "id_token"));
    }

    private async Task<HttpResponseMessage> PostFormAsync(string endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        return await _http.PostAsync(new Uri(endpoint), content, cancellationToken).ConfigureAwait(false);
    }

    private static Exception ToException(HttpStatusCode status, string body)
    {
        var (error, description) = ParseError(body);
        if (error is not null)
        {
            return new DeviceFlowException(error, description);
        }

        return new HttpRequestException($"Keycloak responded {(int)status}: {(body.Length > 300 ? body[..300] : body)}", null, status);
    }

    private static (string? Error, string? Description) ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return (GetString(document.RootElement, "error"), GetString(document.RootElement, "error_description"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };
    }
}
