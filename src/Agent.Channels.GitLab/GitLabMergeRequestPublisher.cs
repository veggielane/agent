using System.Globalization;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.GitLab;

/// <summary>Opens the merge request for a task's branch, or refreshes the one already open for it.</summary>
public sealed class GitLabMergeRequestPublisher : IMergeRequestPublisher
{
    private const string DraftPrefix = "Draft: ";

    private readonly IGitLabClient _client;
    private readonly IOptionsMonitor<GitLabOptions> _options;
    private readonly ILogger<GitLabMergeRequestPublisher> _logger;

    public GitLabMergeRequestPublisher(IGitLabClient client, IOptionsMonitor<GitLabOptions> options, ILogger<GitLabMergeRequestPublisher> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task<MergeRequestInfo> EnsureMergeRequestAsync(MergeRequestSpec spec, CancellationToken cancellationToken)
    {
        var labels = spec.Labels ?? _options.CurrentValue.EffectiveMrLabels;
        var title = ApplyDraft(spec.Title, spec.Draft);

        var open = await _client.ListMergeRequestsAsync(spec.ProjectId, spec.SourceBranch, "opened", cancellationToken).ConfigureAwait(false);
        var existing = open.FirstOrDefault(mr => string.Equals(mr.TargetBranch, spec.TargetBranch, StringComparison.Ordinal)) ?? open.FirstOrDefault();
        if (existing is not null)
        {
            var updated = await _client.UpdateMergeRequestAsync(spec.ProjectId, existing.Iid, new UpdateMergeRequestRequest
            {
                Title = title,
                Description = spec.Description,
                Labels = labels,
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated merge request !{Iid} for {Branch} in {Project}", updated.Iid, spec.SourceBranch, spec.ProjectId);
            return ToInfo(updated);
        }

        List<long>? reviewers = null;
        if (!string.IsNullOrWhiteSpace(spec.ReviewerUsername))
        {
            var reviewer = await _client.GetUserByUsernameAsync(spec.ReviewerUsername, cancellationToken).ConfigureAwait(false);
            if (reviewer is not null)
            {
                reviewers = [reviewer.Id];
            }
            else
            {
                _logger.LogInformation("Reviewer {Username} not found on GitLab; opening the merge request without a reviewer", spec.ReviewerUsername);
            }
        }

        var created = await _client.CreateMergeRequestAsync(spec.ProjectId, new CreateMergeRequestRequest
        {
            SourceBranch = spec.SourceBranch,
            TargetBranch = spec.TargetBranch,
            Title = title,
            Description = spec.Description,
            ReviewerIds = reviewers,
            Labels = labels,
            RemoveSourceBranch = true,
        }, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Opened merge request !{Iid} for {Branch} in {Project}", created.Iid, spec.SourceBranch, spec.ProjectId);
        return ToInfo(created);
    }

    public async Task<MergeRequestInfo?> GetMergeRequestAsync(string projectId, string iid, CancellationToken cancellationToken)
    {
        if (!long.TryParse(iid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        var mr = await _client.GetMergeRequestAsync(projectId, parsed, cancellationToken).ConfigureAwait(false);
        return mr is null ? null : ToInfo(mr);
    }

    public async Task CommentAsync(string projectId, string iid, string markdown, CancellationToken cancellationToken)
    {
        if (!long.TryParse(iid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"'{iid}' is not a merge request iid.", nameof(iid));
        }

        await _client.CreateMergeRequestNoteAsync(projectId, parsed, markdown, cancellationToken).ConfigureAwait(false);
    }

    internal static string ApplyDraft(string title, bool draft)
    {
        var trimmed = title.Trim();
        if (!draft)
        {
            return trimmed;
        }

        return trimmed.StartsWith("Draft:", StringComparison.OrdinalIgnoreCase) ? trimmed : DraftPrefix + trimmed;
    }

    private static MergeRequestInfo ToInfo(GitLabMergeRequest mr)
        => new(mr.Iid.ToString(CultureInfo.InvariantCulture), mr.WebUrl ?? string.Empty, mr.State, mr.SourceBranch);
}
