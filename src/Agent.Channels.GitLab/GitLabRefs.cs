using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Agent.Channels.GitLab;

/// <summary>
/// Somewhere work can be started: a repository, plus the issue or merge request inside it when the reference named
/// one. <see cref="Iid"/> is meaningless while <see cref="IsRepository"/> is true.
/// </summary>
public sealed record GitLabTarget(string Project, GitLabNoteableType? Type, long Iid)
{
    public bool IsRepository => Type is null;

    /// <summary>The reference in the form used as SourceRef, or the plain project path for a repository.</summary>
    public string Reference => Type is null ? Project : GitLabRefs.Build(Type.Value, Project, Iid);
}

/// <summary>Builds and parses the <c>group/project#12</c> / <c>group/project!5</c> references used as SourceRef and ConversationId.</summary>
public static class GitLabRefs
{
    public static string Issue(string projectPath, long iid) => $"{projectPath}#{iid.ToString(CultureInfo.InvariantCulture)}";

    public static string MergeRequest(string projectPath, long iid) => $"{projectPath}!{iid.ToString(CultureInfo.InvariantCulture)}";

    public static string Build(GitLabNoteableType type, string projectPath, long iid)
        => type == GitLabNoteableType.MergeRequest ? MergeRequest(projectPath, iid) : Issue(projectPath, iid);

    /// <summary>Parses <c>path#iid</c> or <c>path!iid</c>. The path may also be a numeric project id.</summary>
    public static bool TryParse(string? reference, out string project, out long iid, out GitLabNoteableType type)
    {
        project = string.Empty;
        iid = 0;
        type = GitLabNoteableType.Issue;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var separator = reference.LastIndexOfAny(['#', '!']);
        if (separator <= 0 || separator == reference.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(reference.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out iid) || iid <= 0)
        {
            return false;
        }

        project = reference[..separator].Trim();
        type = reference[separator] == '!' ? GitLabNoteableType.MergeRequest : GitLabNoteableType.Issue;
        return project.Length > 0;
    }

    /// <summary>
    /// Parses anything a person may type to point at work: <c>group/repo#12</c>, <c>group/repo!5</c>, an issue or
    /// merge request web URL, or a bare repository (path, web URL or clone URL). Query strings, anchors and the
    /// angle brackets some chat clients wrap links in are ignored. Web URLs of a GitLab served from a sub-path need
    /// that prefix removed first, which is why callers pass their configured base URL to
    /// <see cref="TryParseTarget(string?, string?, out GitLabTarget?)"/>.
    /// </summary>
    public static bool TryParseTarget(string? text, [NotNullWhen(true)] out GitLabTarget? target)
        => TryParseTarget(text, null, out target);

    /// <inheritdoc cref="TryParseTarget(string?, out GitLabTarget?)"/>
    /// <param name="baseUrl">The configured GitLab root; when the text starts with it, the prefix is dropped.</param>
    public static bool TryParseTarget(string? text, string? baseUrl, [NotNullWhen(true)] out GitLabTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().Trim('<', '>');
        var path = value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            // AbsolutePath drops the query and the #note_123 anchor GitLab appends when linking a comment. That
            // matters before anything else looks at '#', which in a plain reference means "issue".
            path = Uri.UnescapeDataString(uri.AbsolutePath);
            path = StripRoot(path, baseUrl);
        }

        if (TryParse(path, out var project, out var iid, out var type))
        {
            target = new GitLabTarget(project.Trim('/'), type, iid);
            return true;
        }

        var trimmed = path.Trim('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length - 1; i++)
        {
            if (!TryParseSection(segments[i], out var section)
                || !long.TryParse(segments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIid)
                || parsedIid <= 0)
            {
                continue;
            }

            // GitLab separates the project path from the section with a literal "-" segment.
            var end = string.Equals(segments[i - 1], "-", StringComparison.Ordinal) ? i - 1 : i;
            if (end < 2)
            {
                return false;
            }

            target = new GitLabTarget(string.Join('/', segments[..end]), section, parsedIid);
            return true;
        }

        // No section: a bare repository. A project path is always at least namespace/project.
        if (segments.Length < 2)
        {
            return false;
        }

        target = new GitLabTarget(string.Join('/', segments), null, 0);
        return true;
    }

    /// <summary>Removes the sub-path a GitLab may be served from, so /gitlab/team/repo/-/issues/3 is team/repo#3.</summary>
    private static string StripRoot(string path, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var root)
            || root.AbsolutePath.Trim('/') is not { Length: > 0 } prefix)
        {
            return path;
        }

        var trimmed = path.TrimStart('/');
        return trimmed.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? trimmed[(prefix.Length + 1)..] : path;
    }

    private static bool TryParseSection(string segment, out GitLabNoteableType type)
    {
        if (string.Equals(segment, "issues", StringComparison.OrdinalIgnoreCase))
        {
            type = GitLabNoteableType.Issue;
            return true;
        }

        if (string.Equals(segment, "merge_requests", StringComparison.OrdinalIgnoreCase))
        {
            type = GitLabNoteableType.MergeRequest;
            return true;
        }

        type = default;
        return false;
    }
}
