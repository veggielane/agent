using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Events;
using Agent.Core.Observability;
using Agent.Core.Pipeline;
using Agent.Core.Tasks;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Observability;

/// <summary>Records activities from the agent's source for the duration of a test.</summary>
public sealed class ActivityRecorder : IDisposable
{
    private readonly ActivityListener _listener;

    public ActivityRecorder(string sourceName = AgentTelemetry.ActivitySourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (Activities)
                {
                    Activities.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public List<Activity> Activities { get; } = [];

    public Activity? Named(string name)
    {
        lock (Activities)
        {
            return Activities.FirstOrDefault(a => a.OperationName == name);
        }
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>Records measurements from the agent's meter for the duration of a test.</summary>
public sealed class MetricRecorder : IDisposable
{
    private readonly MeterListener _listener;

    public MetricRecorder(params string[] instruments)
    {
        var wanted = new HashSet<string>(instruments, StringComparer.Ordinal);
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentTelemetry.MeterName && (wanted.Count == 0 || wanted.Contains(instrument.Name)))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.Start();
    }

    public List<(string Instrument, double Value, Dictionary<string, string?> Tags)> Measurements { get; } = [];

    public IEnumerable<(string Instrument, double Value, Dictionary<string, string?> Tags)> For(string instrument)
    {
        lock (Measurements)
        {
            return Measurements.Where(m => m.Instrument == instrument).ToList();
        }
    }

    public double Total(string instrument) => For(instrument).Sum(m => m.Value);

    private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value?.ToString();
        }

        lock (Measurements)
        {
            Measurements.Add((instrument, value, copy));
        }
    }

    public void Dispose() => _listener.Dispose();
}

public sealed class TelemetryTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Question_ProducesAnEventSpanWithAnAnswerChild()
    {
        using var recorder = new ActivityRecorder();
        using var host = TestHost.Create();
        host.Llm.Client.Reply("answered");

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("hello"), Ct);

        var evt = recorder.Named("agent.event");
        Assert.NotNull(evt);
        Assert.Equal("Mattermost", evt.GetTagItem("agent.channel"));
        Assert.Equal("answer", evt.GetTagItem("agent.outcome"));
        Assert.Equal("alice", evt.GetTagItem("agent.caller"));
        Assert.Equal(ActivityStatusCode.Unset, evt.Status);

        var answer = recorder.Named("agent.answer");
        Assert.NotNull(answer);
        Assert.Equal(evt.SpanId, answer.ParentSpanId);
        Assert.Equal("test-model", answer.GetTagItem("gen_ai.request.model"));
    }

    [Fact]
    public async Task Question_RecordsEventCountDurationAndTokens()
    {
        using var metrics = new MetricRecorder();
        using var host = TestHost.Create();
        host.Llm.Client.Reply("answered", tokens: 40);

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("hello"), Ct);

        var events = metrics.For("agent.events").ToList();
        Assert.Single(events);
        Assert.Equal("Mattermost", events[0].Tags["channel"]);
        Assert.Equal("answer", events[0].Tags["outcome"]);
        Assert.Single(metrics.For("agent.event.duration"));

        // 40 total tokens is split 20 in and 20 out by the scripted client.
        Assert.Equal(40, metrics.Total("agent.tokens"));
        Assert.Contains(metrics.For("agent.tokens"), m => m.Tags["direction"] == "input" && m.Tags["purpose"] == "Answer" && m.Tags["channel"] == "Mattermost");
    }

    [Fact]
    public async Task FailedEvent_MarksTheSpanAndTagsTheOutcome()
    {
        using var recorder = new ActivityRecorder();
        using var metrics = new MetricRecorder();
        using var host = TestHost.Create();
        host.Llm.Client.Reply((_, _) => throw new HttpRequestException("llm down"));

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("hello"), Ct);

        var evt = recorder.Named("agent.event");
        Assert.NotNull(evt);
        Assert.Equal(ActivityStatusCode.Error, evt.Status);
        Assert.Equal("System.Net.Http.HttpRequestException", evt.GetTagItem("error.type"));
        Assert.Equal("error", evt.GetTagItem("agent.outcome"));
        Assert.NotNull(evt.GetTagItem("agent.error.reference"));
        Assert.Equal("error", metrics.For("agent.events").Single().Tags["outcome"]);
    }

    [Fact]
    public async Task DuplicateEvent_CountsAsSkippedAndProducesNoSpan()
    {
        using var recorder = new ActivityRecorder();
        using var metrics = new MetricRecorder();
        using var host = TestHost.Create();
        host.Llm.Client.Reply("once");
        var evt = host.Event("hello", eventId: "same");

        await host.Get<IInboundProcessor>().ProcessAsync(evt, Ct);
        await host.Get<IInboundProcessor>().ProcessAsync(evt, Ct);

        Assert.Single(recorder.Activities.Where(a => a.OperationName == "agent.event"));
        Assert.Equal(1, metrics.Total("agent.events.skipped"));
    }

    [Fact]
    public async Task Command_ProducesASpanAndCountsTheOutcome()
    {
        using var recorder = new ActivityRecorder();
        using var metrics = new MetricRecorder();
        using var host = TestHost.Create(s => s.AddCommandHandlers<SampleCommands>());

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("!greet world"), Ct);

        var command = recorder.Named("agent.command");
        Assert.NotNull(command);
        Assert.Equal("greet", command.GetTagItem("agent.command"));
        Assert.Equal("ok", command.GetTagItem("agent.outcome"));

        var measurement = metrics.For("agent.commands").Single();
        Assert.Equal("greet", measurement.Tags["command"]);
        Assert.Equal("ok", measurement.Tags["outcome"]);
        Assert.Single(metrics.For("agent.command.duration"));
    }

    [Fact]
    public async Task UnknownCommand_IsCountedSeparately()
    {
        using var metrics = new MetricRecorder();
        using var host = TestHost.Create();

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("!nope"), Ct);

        Assert.Equal("unknown", metrics.For("agent.commands").Single().Tags["outcome"]);
    }

    [Fact]
    public async Task Authorization_CountsAllowAndDeny()
    {
        using var metrics = new MetricRecorder();
        using var host = TestHost.Create(s => s.AddCommandHandlers<SampleCommands>());

        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("!teamonly", "bob"), Ct);
        await host.Get<IInboundProcessor>().ProcessAsync(host.Event("!teamonly", "alice"), Ct);

        var decisions = metrics.For("agent.authorizations").ToList();
        Assert.Contains(decisions, d => d.Tags["outcome"] == "allow" && d.Tags["action"] == "command:teamonly");
        Assert.Contains(decisions, d => d.Tags["outcome"] == "deny" && d.Tags["required"] == "Team");
    }

    [Fact]
    public async Task Tool_ProducesASpanAndDuration()
    {
        using var recorder = new ActivityRecorder();
        using var metrics = new MetricRecorder();
        var audit = new Core.Audit.InMemoryAuditSink();
        var function = new Core.Tools.GovernedAIFunction(
            Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "result", "inner"),
            "srv__echo",
            "mcp",
            new Core.Tools.ToolGovernance(),
            audit,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        using var scope = RequestContext.Begin(CallerIdentity.Local("alice", Role.Users));
        await function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(), Ct);

        var span = recorder.Named("agent.tool");
        Assert.NotNull(span);
        Assert.Equal("srv__echo", span.GetTagItem("agent.tool"));
        Assert.Equal("mcp", span.GetTagItem("agent.tool.source"));

        var measurement = metrics.For("agent.tools").Single();
        Assert.Equal("ok", measurement.Tags["outcome"]);
        Assert.Single(metrics.For("agent.tool.duration"));
    }

    [Fact]
    public async Task Task_CountsCreationAndCompletion()
    {
        using var metrics = new MetricRecorder();
        using var host = TestHost.Create();
        var service = host.Get<ITaskService>();

        var task = await service.CreateAsync(new TaskRequest
        {
            Source = TaskSource.GitLabIssue,
            SourceRef = "t/r#1",
            Requester = CallerIdentity.Local("bob", Role.Team),
            Instruction = "do it",
            RepoUrl = "https://gitlab.internal/t/r.git",
            ConversationId = "t/r#1",
            NotifyChannel = Channel.GitLab,
        }, Ct);

        await service.CloseAsync(task.Id, merged: true, Ct);

        var tasks = metrics.For("agent.tasks").ToList();
        Assert.Contains(tasks, t => t.Tags["transition"] == "created" && t.Tags["source"] == "GitLabIssue");
        Assert.Contains(tasks, t => t.Tags["transition"] == "merged" && t.Tags["status"] == "Done");
        Assert.Equal("Done", metrics.For("agent.task.duration").Single().Tags["outcome"]);
    }

    [Fact]
    public void QueueDepth_IsObservable()
    {
        using var host = TestHost.Create();
        var queue = host.Get<IInboundQueue>();

        // Registering the gauge is part of building the queue; reading it back proves the wiring.
        var values = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "agent.queue.depth")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => values.Add(value));
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.NotEmpty(values);
        Assert.Equal(queue.Depth, values.Sum());
    }

    [Fact]
    public void TelemetryNames_AreStable()
    {
        // These are the identifiers dashboards and alerts are built on: changing one is a breaking change.
        Assert.Equal("Agent", AgentTelemetry.ActivitySourceName);
        Assert.Equal("Agent", AgentTelemetry.MeterName);
        Assert.Equal("Agent.Llm", AgentTelemetry.LlmActivitySourceName);
        Assert.Equal("agent.events", AgentTelemetry.Events.Name);
        Assert.Equal("agent.tokens", AgentTelemetry.Tokens.Name);
        Assert.Equal("agent.tools", AgentTelemetry.Tools.Name);
        Assert.Equal("agent.tasks", AgentTelemetry.Tasks.Name);
        Assert.Equal("agent.polls", AgentTelemetry.Polls.Name);
        Assert.Equal("agent.sandbox.commands", AgentTelemetry.SandboxCommands.Name);
        Assert.NotEmpty(AgentTelemetry.Version);
    }
}
