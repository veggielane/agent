using System.ComponentModel;
using System.Globalization;
using System.Text;
using Agent.Core.Authorization;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Jira;

/// <summary>Read-only Jira tools for the answer loop: <c>jira_get_issue</c> and <c>jira_search</c>.</summary>
public sealed class JiraTools : IToolSource
{
    internal const int DescriptionLimit = 4000;
    internal const int CommentLimit = 1000;
    internal const int CommentsShown = 5;
    internal const int MaxSearchResults = 50;

    private static readonly string[] IssueFields =
        ["summary", "description", "status", "assignee", "reporter", "labels", "components", "comment", "updated", "project"];

    private static readonly string[] SearchFields = ["summary", "status"];

    private readonly IJiraClient _client;
    private readonly IOptionsMonitor<JiraOptions> _options;

    public JiraTools(IJiraClient client, IOptionsMonitor<JiraOptions> options)
    {
        _client = client;
        _options = options;
    }

    public IEnumerable<ToolDescriptor> GetTools()
    {
        yield return new ToolDescriptor(
            AIFunctionFactory.Create(
                GetIssueAsync,
                "jira_get_issue",
                "Reads one Jira issue: summary, status, assignee, labels, description and the last comments. Use it when a question refers to an issue key such as PROJ-123."),
            Role.Users,
            ToolScope.All,
            "native");

        yield return new ToolDescriptor(
            AIFunctionFactory.Create(
                SearchAsync,
                "jira_search",
                "Searches Jira with a JQL query and lists key, status and summary for each match."),
            Role.Users,
            ToolScope.All,
            "native");
    }

    public async Task<string> GetIssueAsync(
        [Description("Issue key, e.g. PROJ-123")] string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Error: an issue key is required.";
        }

        key = key.Trim().ToUpperInvariant();
        try
        {
            var issue = await _client.GetIssueAsync(key, IssueFields, cancellationToken).ConfigureAwait(false);
            return issue is null ? $"Issue {key} was not found or is not visible to the agent." : FormatIssue(issue, _options.CurrentValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: could not read {key}: {ex.Message}";
        }
    }

    public async Task<string> SearchAsync(
        [Description("JQL query, e.g. project = PROJ AND status = Open ORDER BY updated DESC")] string jql,
        [Description("Maximum number of issues to return (1-50)")] int max = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jql))
        {
            return "Error: a JQL query is required.";
        }

        max = Math.Clamp(max, 1, MaxSearchResults);
        try
        {
            var result = await _client.SearchAsync(jql.Trim(), SearchFields, 0, max, cancellationToken).ConfigureAwait(false);
            if (result.Issues.Count == 0)
            {
                return "No issues match.";
            }

            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"Showing {result.Issues.Count} of {Math.Max(result.Total, result.Issues.Count)} issues:").Append('\n');
            foreach (var issue in result.Issues)
            {
                sb.Append(issue.Key).Append(" | ").Append(issue.Fields.Status?.Name ?? "?").Append(" | ").Append(OneLine(issue.Fields.Summary)).Append('\n');
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: search failed: {ex.Message}";
        }
    }

    internal static string FormatIssue(JiraIssue issue, JiraOptions options)
    {
        var f = issue.Fields;
        var sb = new StringBuilder();
        sb.Append(issue.Key).Append(": ").Append(OneLine(f.Summary)).Append('\n');
        sb.Append("Status: ").Append(f.Status?.Name ?? "?");
        sb.Append(" | Assignee: ").Append(UserText(f.Assignee) ?? "unassigned");
        sb.Append(" | Reporter: ").Append(UserText(f.Reporter) ?? "?");
        if (f.Labels.Count > 0)
        {
            sb.Append(" | Labels: ").Append(string.Join(", ", f.Labels));
        }

        if (f.Components.Count > 0)
        {
            sb.Append(" | Components: ").Append(string.Join(", ", f.Components.Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n))));
        }

        if (f.Updated is { } updated)
        {
            sb.Append(" | Updated: ").Append(updated.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC");
        }

        sb.Append('\n');
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            sb.Append("URL: ").Append(options.BrowseUrl(issue.Key)).Append('\n');
        }

        sb.Append('\n');
        sb.Append("Description:\n");
        sb.Append(string.IsNullOrWhiteSpace(f.Description) ? "(none)" : Trim(f.Description.Trim(), DescriptionLimit)).Append('\n');

        var comments = f.Comment?.Comments ?? [];
        if (comments.Count > 0)
        {
            var shown = comments.OrderBy(c => c.Created ?? DateTimeOffset.MinValue).TakeLast(CommentsShown).ToList();
            sb.Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"Comments (last {shown.Count} of {Math.Max(f.Comment?.Total ?? 0, comments.Count)}):").Append('\n');
            foreach (var comment in shown)
            {
                var when = comment.Created?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?";
                sb.Append('[').Append(when).Append("] ").Append(UserText(comment.Author) ?? "?").Append(": ").Append(Trim(comment.Body?.Trim() ?? string.Empty, CommentLimit)).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string? UserText(JiraUser? user)
    {
        if (user is null)
        {
            return null;
        }

        var login = user.Login;
        if (login is null)
        {
            return user.DisplayName;
        }

        return string.IsNullOrWhiteSpace(user.DisplayName) || string.Equals(user.DisplayName, login, StringComparison.Ordinal)
            ? login
            : $"{login} ({user.DisplayName})";
    }

    private static string OneLine(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + $"… [truncated {text.Length - max} characters]";
}
