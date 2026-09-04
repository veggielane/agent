using System.Globalization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.Jira;

/// <summary>
/// Polls each configured project with a JQL watermark (<c>updated >= watermark - overlap</c>) and turns bot mentions
/// and the task label into <see cref="InboundEvent"/>s. Watermarks live in <see cref="ICursorStore"/> under
/// <c>jira:{PROJECT}</c>; comments and labels already enqueued are remembered in <see cref="IProcessedEventStore"/>
/// under <c>jira-seen:*</c> so the overlap window never enqueues twice.
/// </summary>
public sealed class JiraPoller : BackgroundService, IEventSource
{
    internal const string CursorPrefix = "jira:";
    internal const string SeenPrefix = "jira-seen:";
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private static readonly string[] BaseFields =
        ["summary", "description", "labels", "status", "project", "components", "comment", "updated", "created", "reporter", "assignee"];

    private readonly IJiraClient _client;
    private readonly IInboundQueue _queue;
    private readonly ICursorStore _cursors;
    private readonly IProcessedEventStore _processed;
    private readonly ITaskStore _tasks;
    private readonly IOptionsMonitor<JiraOptions> _options;
    private readonly ILogger<JiraPoller> _logger;
    private readonly TimeProvider _time;

    private TimeZoneInfo? _zone;
    private string? _zoneSource;
    private bool _zoneWarned;
    private bool _warnedNoProjects;
    private TimeSpan _backoff;
    private DateTimeOffset? _lastPoll;
    private string? _lastError;
    private long _issuesSeen;
    private long _eventsEnqueued;

    public JiraPoller(
        IJiraClient client,
        IInboundQueue queue,
        ICursorStore cursors,
        IProcessedEventStore processed,
        ITaskStore tasks,
        IOptionsMonitor<JiraOptions> options,
        ILogger<JiraPoller> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _queue = queue;
        _cursors = cursors;
        _processed = processed;
        _tasks = tasks;
        _options = options;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Name => "Jira poller";

    public string Status
    {
        get
        {
            var lastPoll = _lastPoll;
            if (lastPoll is null)
            {
                return "not polled yet";
            }

            var status = $"last poll {lastPoll.Value:u}, {Interlocked.Read(ref _issuesSeen)} issues seen, {Interlocked.Read(ref _eventsEnqueued)} events enqueued";
            var error = _lastError;
            if (error is not null)
            {
                status += $", backoff {_backoff.TotalSeconds:0}s, last error: {error}";
            }

            return status;
        }
    }

    /// <summary>The delay the loop will wait before the next poll: the interval, or the current backoff after a transient failure.</summary>
    internal TimeSpan CurrentBackoff => _backoff;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Jira poller starting for projects [{Projects}]", string.Join(", ", _options.CurrentValue.Projects));
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Jira poller stopped");
    }

    /// <summary>Polls every configured project once and returns how long to wait before the next round.</summary>
    internal async Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken)
    {
        JiraOptions options;
        try
        {
            options = _options.CurrentValue;
        }
        catch (OptionsValidationException ex)
        {
            _lastError = ex.Message;
            _lastPoll = _time.GetUtcNow();
            _logger.LogError(ex, "Jira options are invalid; poller idle");
            return TimeSpan.FromSeconds(30);
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds));
        var projects = options.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (projects.Count == 0)
        {
            if (!_warnedNoProjects)
            {
                _logger.LogWarning("Jira:Projects is empty; nothing to poll");
                _warnedNoProjects = true;
            }

            _lastPoll = _time.GetUtcNow();
            return interval;
        }

        string? error = null;
        var transient = false;
        foreach (var project in projects)
        {
            try
            {
                await PollProjectAsync(project, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = $"{project}: {ex.Message}";
                transient |= IsTransient(ex);
                _logger.LogWarning(ex, "Jira poll for project {Project} failed", project);
            }
        }

        _lastPoll = _time.GetUtcNow();
        _lastError = error;

        if (transient)
        {
            var next = _backoff == TimeSpan.Zero ? interval + interval : _backoff + _backoff;
            _backoff = next > MaxBackoff ? MaxBackoff : next;
            return _backoff;
        }

        _backoff = TimeSpan.Zero;
        return interval;
    }

    internal async Task PollProjectAsync(string project, JiraOptions options, CancellationToken cancellationToken)
    {
        var cursorKey = CursorPrefix + project;
        var overlap = TimeSpan.FromMinutes(Math.Max(0, options.OverlapMinutes));
        var now = _time.GetUtcNow();
        var stored = await _cursors.GetAsync(cursorKey, cancellationToken).ConfigureAwait(false);
        var watermark = ParseWatermark(stored) ?? now - overlap;
        var cutoff = watermark - overlap;

        var zone = await ResolveZoneAsync(options, cancellationToken).ConfigureAwait(false);
        var jql = BuildJql(project, cutoff, zone);
        var fields = BuildFields(options);
        var mapper = new JiraEventMapper(options);
        var pageSize = Math.Clamp(options.MaxResultsPerPoll, 1, 1000);

        var startAt = 0;
        var maxUpdated = watermark;
        while (true)
        {
            var page = await _client.SearchAsync(jql, fields, startAt, pageSize, cancellationToken).ConfigureAwait(false);
            if (page.Issues.Count == 0)
            {
                break;
            }

            foreach (var issue in page.Issues)
            {
                await ProcessIssueAsync(issue, mapper, cutoff, options, cancellationToken).ConfigureAwait(false);
                if (issue.Fields.Updated is { } updated && updated > maxUpdated)
                {
                    maxUpdated = updated;
                }
            }

            if (maxUpdated > watermark)
            {
                await _cursors.SetAsync(cursorKey, FormatWatermark(maxUpdated), cancellationToken).ConfigureAwait(false);
                watermark = maxUpdated;
            }

            startAt += page.Issues.Count;
            if (startAt >= page.Total)
            {
                break;
            }
        }

        if (stored is null)
        {
            // First run: persist the starting point so a restart does not widen the window again.
            await _cursors.SetAsync(cursorKey, FormatWatermark(watermark), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessIssueAsync(JiraIssue issue, JiraEventMapper mapper, DateTimeOffset cutoff, JiraOptions options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _issuesSeen);
        if (!mapper.IsRelevant(issue, cutoff))
        {
            return;
        }

        var activeTask = await _tasks.FindActiveBySourceAsync(TaskSource.JiraIssue, issue.Key, cancellationToken).ConfigureAwait(false);
        string? repoUrl = null;
        if (activeTask is null && mapper.HasTaskLabel(issue))
        {
            repoUrl = JiraRepositoryResolver.ResolveFromIssue(issue, options)?.Url;
        }

        foreach (var evt in mapper.Map(issue, cutoff, activeTask, repoUrl))
        {
            if (!await _processed.TryMarkProcessedAsync(Channel.Jira, SeenPrefix + evt.EventId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await _queue.EnqueueAsync(evt, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _eventsEnqueued);
            _logger.LogInformation("Jira {Issue}: enqueued {Kind} {EventId} from {User}", issue.Key, evt.Kind, evt.EventId, evt.Caller.DisplayName);
        }
    }

    /// <summary>JQL timestamps are read in the requesting user's profile time zone, so the cutoff is rendered in that zone.</summary>
    internal static string BuildJql(string project, DateTimeOffset cutoff, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(cutoff, zone);
        return $"project = \"{project}\" AND updated >= \"{local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}\" ORDER BY updated ASC";
    }

    internal static IReadOnlyCollection<string> BuildFields(JiraOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RepositoryField))
        {
            return BaseFields;
        }

        return [.. BaseFields, options.RepositoryField.Trim()];
    }

    internal static string FormatWatermark(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    internal static DateTimeOffset? ParseWatermark(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    private static bool IsTransient(Exception ex) => ex switch
    {
        JiraApiException api => api.IsTransient,
        HttpRequestException => true,
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false,
    };

    private async Task<TimeZoneInfo> ResolveZoneAsync(JiraOptions options, CancellationToken cancellationToken)
    {
        var configured = string.IsNullOrWhiteSpace(options.TimeZone) ? null : options.TimeZone.Trim();
        if (_zone is not null && string.Equals(_zoneSource, configured, StringComparison.Ordinal))
        {
            return _zone;
        }

        var id = configured;
        if (id is null)
        {
            try
            {
                var me = await _client.GetMyselfAsync(cancellationToken).ConfigureAwait(false);
                id = me.TimeZone;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_zoneWarned)
                {
                    _logger.LogWarning(ex, "Could not read the bot's Jira time zone; using UTC for JQL timestamps until it is available");
                    _zoneWarned = true;
                }

                return TimeZoneInfo.Utc;
            }
        }

        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _logger.LogWarning(ex, "Time zone '{Zone}' is unknown on this machine; set Jira:TimeZone to a zone id this host understands. Using UTC", id);
            }
        }

        _zone = zone;
        _zoneSource = configured;
        _logger.LogInformation("Jira JQL timestamps rendered in time zone {Zone}", zone.Id);
        return zone;
    }
}
