using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Jira;

/// <summary>
/// Finds the repository a Jira issue refers to, in order: a repository URL in the text, the configured
/// repository custom field, the component → repo map, then the project → repo map.
/// </summary>
public sealed class JiraRepositoryResolver : IRepositoryResolver
{
    private static readonly char[] TokenSeparators = [' ', '\n', '\r', '\t', '<', '>', '(', ')', '[', ']', '|', '"', '\'', '{', '}'];
    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', '/'];

    private readonly IJiraClient _client;
    private readonly IOptionsMonitor<JiraOptions> _options;
    private readonly ILogger<JiraRepositoryResolver> _logger;

    public JiraRepositoryResolver(IJiraClient client, IOptionsMonitor<JiraOptions> options, ILogger<JiraRepositoryResolver> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task<RepoTarget?> ResolveAsync(InboundEvent evt, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var fromText = FindRepoUrl(evt.Text, options);
        if (fromText is not null)
        {
            return ToTarget(fromText);
        }

        var key = IssueKeyOf(evt);
        if (key is null)
        {
            return null;
        }

        JiraIssue? issue;
        try
        {
            issue = await _client.GetIssueAsync(key, IssueFields(options), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JiraApiException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not load {Issue} to resolve its repository", key);
            return null;
        }

        return issue is null ? null : ResolveFromIssue(issue, options);
    }

    /// <summary>Pure resolution from issue data: description URL → repository field → components → project.</summary>
    public static RepoTarget? ResolveFromIssue(JiraIssue issue, JiraOptions options)
    {
        var fields = issue.Fields;
        var fromText = FindRepoUrl(fields.Description, options) ?? FindRepoUrl(fields.Summary, options);
        if (fromText is not null)
        {
            return ToTarget(fromText);
        }

        if (!string.IsNullOrWhiteSpace(options.RepositoryField))
        {
            var fieldValue = fields.GetExtraString(options.RepositoryField);
            var fieldUrl = FindUrl(fieldValue, _ => true);
            if (fieldUrl is not null)
            {
                return ToTarget(fieldUrl);
            }
        }

        foreach (var component in fields.Components)
        {
            if (component.Name is not null && TryLookup(options.ComponentRepos, component.Name, out var componentRepo))
            {
                return ToTarget(componentRepo);
            }
        }

        var project = fields.Project?.Key ?? ProjectKeyOf(issue.Key);
        if (project is not null && TryLookup(options.ProjectRepos, project, out var projectRepo))
        {
            return ToTarget(projectRepo);
        }

        return null;
    }

    /// <summary>First http(s) URL in the text whose host looks like the configured git host and whose path has at least group/repo.</summary>
    public static string? FindRepoUrl(string? text, JiraOptions options)
        => FindUrl(text, host => HostMatches(host, options));

    public static RepoTarget ToTarget(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return new RepoTarget(url, path.Length > 0 ? path : null, null);
    }

    internal static string? ProjectKeyOf(string? issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
        {
            return null;
        }

        var dash = issueKey.LastIndexOf('-');
        return dash > 0 ? issueKey[..dash] : null;
    }

    private static string? IssueKeyOf(InboundEvent evt)
    {
        var key = evt.Meta("issue");
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        if (evt.Task is { Source: TaskSource.JiraIssue, SourceRef: { Length: > 0 } sourceRef })
        {
            return sourceRef;
        }

        return evt.Channel == Channel.Jira && !string.IsNullOrWhiteSpace(evt.ConversationId) ? evt.ConversationId : null;
    }

    private static IReadOnlyCollection<string> IssueFields(JiraOptions options)
    {
        var fields = new List<string> { "summary", "description", "components", "project" };
        if (!string.IsNullOrWhiteSpace(options.RepositoryField))
        {
            fields.Add(options.RepositoryField);
        }

        return fields;
    }

    private static bool HostMatches(string host, JiraOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RepoHostHint) && string.Equals(host, options.RepoHostHint.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(options.RepoUrlPattern) && host.Contains(options.RepoUrlPattern.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindUrl(string? text, Func<string, bool> hostFilter)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var token in text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.TrimEnd(TrailingPunctuation);
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !hostFilter(uri.Host))
            {
                continue;
            }

            var normalized = NormalizeRepoUrl(uri);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    /// <summary>Drops query, fragment and GitLab UI suffixes (<c>/-/issues/1</c>) so the result is the clonable repo root.</summary>
    private static string? NormalizeRepoUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        var uiSuffix = path.IndexOf("/-/", StringComparison.Ordinal);
        if (uiSuffix >= 0)
        {
            path = path[..uiSuffix];
        }

        path = path.Trim('/');
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Authority}/{path}";
    }

    private static bool TryLookup(Dictionary<string, string> map, string key, out string value)
    {
        if (map.TryGetValue(key, out var direct) && !string.IsNullOrWhiteSpace(direct))
        {
            value = direct;
            return true;
        }

        foreach (var pair in map)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
