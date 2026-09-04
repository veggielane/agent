using Agent.Core.Tasks;
using Microsoft.Extensions.Options;

namespace Agent.Channels.GitLab;

/// <summary>HTTPS credentials (<c>oauth2:&lt;token&gt;</c>) for repositories hosted on the configured GitLab instance.</summary>
public sealed class GitLabCredentialProvider : IRepositoryCredentialProvider
{
    private readonly IOptionsMonitor<GitLabOptions> _options;

    public GitLabCredentialProvider(IOptionsMonitor<GitLabOptions> options)
    {
        _options = options;
    }

    public (string Username, string Token)? GetCredentials(string repoUrl)
    {
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.Token)
            || !Uri.TryCreate(repoUrl, UriKind.Absolute, out var repo)
            || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var gitlab))
        {
            return null;
        }

        if (repo.Scheme != Uri.UriSchemeHttps && repo.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        return string.Equals(repo.Host, gitlab.Host, StringComparison.OrdinalIgnoreCase)
            ? ("oauth2", options.Token)
            : null;
    }
}
