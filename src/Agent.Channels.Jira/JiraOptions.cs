using System.ComponentModel.DataAnnotations;

namespace Agent.Channels.Jira;

/// <summary>Configuration for the Jira Data Center channel. Bound from the <c>Jira</c> section.</summary>
public sealed class JiraOptions
{
    public const string SectionName = "Jira";

    /// <summary>Jira base URL, e.g. <c>https://jira.corp.local</c> or <c>https://host/jira</c>.</summary>
    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The bot's Jira username. Used for Basic auth and as the default <see cref="BotUsername"/>.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>Personal access token. When set, requests use <c>Authorization: Bearer</c>.</summary>
    public string? Token { get; set; }

    /// <summary>Password for Basic auth. Only used when <see cref="Token"/> is empty.</summary>
    public string? Password { get; set; }

    /// <summary>The username mentioned as <c>[~name]</c>; defaults to <see cref="Username"/>. The bot's own comments are ignored.</summary>
    public string? BotUsername { get; set; }

    public int PollSeconds { get; set; } = 30;

    /// <summary>How far behind the watermark each poll looks. Jira's <c>updated</c> has minute granularity, so keep this at two or more.</summary>
    public int OverlapMinutes { get; set; } = 2;

    /// <summary>Project keys to poll.</summary>
    public string[] Projects { get; set; } = [];

    /// <summary>Label that turns an issue into a coding task.</summary>
    public string TaskLabel { get; set; } = "agent";

    /// <summary>Custom field id (e.g. <c>customfield_12345</c>) holding the repository URL, optional.</summary>
    public string? RepositoryField { get; set; }

    /// <summary>Project key → repository URL.</summary>
    public Dictionary<string, string> ProjectRepos { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Component name → repository URL.</summary>
    public Dictionary<string, string> ComponentRepos { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int MaxResultsPerPoll { get; set; } = 50;

    /// <summary>Maximum number of comments loaded as conversation history.</summary>
    public int HistoryComments { get; set; } = 40;

    /// <summary>Substring a URL's host must contain to count as a repository link (case-insensitive).</summary>
    public string RepoUrlPattern { get; set; } = "gitlab";

    /// <summary>Exact host (e.g. <c>git.corp.local</c>) that also counts as a repository link.</summary>
    public string? RepoHostHint { get; set; }

    /// <summary>
    /// Time zone used to render JQL timestamps. Jira interprets <c>updated >= "yyyy-MM-dd HH:mm"</c> in the
    /// requesting user's profile time zone. Empty = read it from <c>/rest/api/2/myself</c>; falls back to UTC.
    /// </summary>
    public string? TimeZone { get; set; }

    public string EffectiveBotUsername => string.IsNullOrWhiteSpace(BotUsername) ? Username : BotUsername;

    public string BrowseUrl(string issueKey) => $"{BaseUrl.TrimEnd('/')}/browse/{issueKey}";
}
