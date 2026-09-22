using System.ComponentModel.DataAnnotations;

namespace Agent.Channels.GitLab;

/// <summary>Bound from the <c>GitLab</c> section; read through <c>IOptionsMonitor</c> so edits hot-reload.</summary>
public sealed class GitLabOptions
{
    public const string SectionName = "GitLab";

    private static readonly string[] DefaultMrLabels = ["agent"];

    /// <summary>GitLab root URL, e.g. https://gitlab.internal (a relative URL root such as /gitlab is allowed).</summary>
    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Personal or group access token of the bot user; sent as the PRIVATE-TOKEN header.</summary>
    [Required]
    public string Token { get; set; } = string.Empty;

    /// <summary>Username of the bot; stripped from mentions and used to tell the bot's notes from users'.</summary>
    public string BotUsername { get; set; } = string.Empty;

    [Range(1, 3600)]
    public int PollSeconds { get; set; } = 30;

    /// <summary>How far behind the watermark the labelled-issue query looks, to survive clock skew and indexing lag.</summary>
    [Range(0, 1440)]
    public int OverlapMinutes { get; set; } = 2;

    /// <summary>Group paths or ids whose labelled issues become tasks (subgroups included).</summary>
    public string[] Groups { get; set; } = [];

    /// <summary>
    /// Group or project paths (<c>platform</c>, <c>platform/billing</c>) that coding tasks may target, on this GitLab
    /// host only. Applies to every way a task gets a repository: labels, assignments, mentions, Jira mappings,
    /// <c>!fix</c>, the API and follow-ups. Empty means no restriction beyond what the bot token can reach.
    /// </summary>
    public string[] AllowedProjects { get; set; } = [];

    public string TaskLabel { get; set; } = "agent";

    /// <summary>Whether assigning an issue to the bot starts a task.</summary>
    public bool TaskOnAssign { get; set; } = true;

    /// <summary>Whether a mention on a merge request the agent did not open becomes a follow-up instead of a question.</summary>
    public bool FollowUpsOnForeignMrs { get; set; }

    /// <summary>
    /// Labels added to merge requests the agent opens. Null or empty means the default (<c>agent</c>); the
    /// configuration binder appends to array defaults, which is why the default is not stored here.
    /// </summary>
    public string[]? MrLabels { get; set; }

    public string AckEmoji { get; set; } = "eyes";

    public string DoneEmoji { get; set; } = "white_check_mark";

    public string FailedEmoji { get; set; } = "x";

    /// <summary>Upper bound on notes loaded as conversation history.</summary>
    [Range(1, 500)]
    public int HistoryNotes { get; set; } = 40;

    /// <summary>
    /// Whether the poller watches the head pipeline of merge requests it opened and re-runs the coding loop when it
    /// fails. Turn off where CI is flaky or expensive enough that a human should always look first.
    /// </summary>
    public bool WatchPipelines { get; set; } = true;

    /// <summary>
    /// How many failed pipelines the agent tries to fix for one merge request before leaving it to a human. Zero
    /// reports the failure and never re-queues.
    /// </summary>
    [Range(0, 10)]
    public int MaxPipelineFixAttempts { get; set; } = 2;

    /// <summary>Characters of each failed job's log tail quoted into the follow-up instruction.</summary>
    [Range(200, 100_000)]
    public int PipelineTraceChars { get; set; } = 4000;

    public IReadOnlyList<string> EffectiveMrLabels => MrLabels is { Length: > 0 } ? MrLabels : DefaultMrLabels;
}
