using System.Globalization;

namespace Agent.Channels.GitLab;

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
}
