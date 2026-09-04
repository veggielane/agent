using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Infrastructure.Ldap;

/// <summary><see cref="ILdapSearcher"/> over <see cref="LdapConnection"/>: one bind + subtree search per call.</summary>
public sealed class LdapConnectionSearcher : ILdapSearcher
{
    private readonly IOptionsMonitor<LdapOptions> _options;
    private readonly ILogger<LdapConnectionSearcher> _logger;

    public LdapConnectionSearcher(IOptionsMonitor<LdapOptions> options, ILogger<LdapConnectionSearcher> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LdapEntry>> SearchAsync(string baseDn, string filter, string[] attributes, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.Server))
        {
            throw new InvalidOperationException($"{LdapOptions.SectionName}:{nameof(LdapOptions.Server)} is not configured.");
        }

        var identifier = new LdapDirectoryIdentifier(options.Server, options.Port, fullyQualifiedDnsHostName: true, connectionless: false);
        var credential = string.IsNullOrEmpty(options.BindDn) ? null : new NetworkCredential(options.BindDn, options.BindPassword);

        using var connection = new LdapConnection(identifier, credential, credential is null ? AuthType.Anonymous : AuthType.Basic);
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = options.UseSsl;
        connection.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        // Disposing the connection aborts any in-flight bind or search, which is the only way to cancel with this API.
        using var registration = cancellationToken.Register(static state => ((LdapConnection)state!).Dispose(), connection);

        SearchResponse response;
        try
        {
            // Bind is synchronous in System.DirectoryServices.Protocols; keep it off the caller's thread.
            await Task.Run(connection.Bind, cancellationToken).ConfigureAwait(false);

            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, attributes);
            _logger.LogDebug("LDAP search base={BaseDn} filter={Filter}", baseDn, filter);

            response = (SearchResponse)await Task.Factory
                .FromAsync(connection.BeginSendRequest, connection.EndSendRequest, request, PartialResultProcessing.NoPartialResultSupport, state: null)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("LDAP search was cancelled.", ex, cancellationToken);
        }

        var entries = new List<LdapEntry>(response.Entries.Count);
        foreach (SearchResultEntry entry in response.Entries)
        {
            var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryAttribute attribute in entry.Attributes.Values)
            {
                values[attribute.Name] = attribute.GetValues(typeof(string)).OfType<string>().ToArray();
            }

            entries.Add(new LdapEntry(entry.DistinguishedName, values));
        }

        return entries;
    }
}
