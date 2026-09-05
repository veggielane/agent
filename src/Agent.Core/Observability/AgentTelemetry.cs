using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Agent.Core.Observability;

/// <summary>
/// The agent's <see cref="ActivitySource"/> and <see cref="Meter"/>, plus every instrument it records to.
/// This layer only <em>emits</em>: nothing here depends on the OpenTelemetry SDK, so the host decides
/// whether anything is exported. With no listener attached the calls are close to free.
/// </summary>
public static class AgentTelemetry
{
    public const string ActivitySourceName = "Agent";

    public const string MeterName = "Agent";

    /// <summary>Activity source for the LLM client, kept separate so gen_ai spans can be sampled on their own.</summary>
    public const string LlmActivitySourceName = "Agent.Llm";

    public static readonly string Version =
        typeof(AgentTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AgentTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static readonly ActivitySource Source = new(ActivitySourceName, Version);

    private static readonly Meter Meter = new(MeterName, Version);

    // ---- inbound pipeline -------------------------------------------------

    /// <summary>One per inbound event that reached the pipeline, tagged with how it was classified and how it ended.</summary>
    public static readonly Counter<long> Events = Meter.CreateCounter<long>(
        "agent.events", "{event}", "Inbound events processed.");

    public static readonly Histogram<double> EventDuration = Meter.CreateHistogram<double>(
        "agent.event.duration", "s", "Time to process one inbound event end to end.");

    public static readonly Counter<long> EventsSkipped = Meter.CreateCounter<long>(
        "agent.events.skipped", "{event}", "Inbound events dropped as already processed.");

    // ---- authorization and commands ---------------------------------------

    public static readonly Counter<long> Authorizations = Meter.CreateCounter<long>(
        "agent.authorizations", "{decision}", "Authorization decisions, allowed and denied.");

    public static readonly Counter<long> Commands = Meter.CreateCounter<long>(
        "agent.commands", "{invocation}", "!command invocations.");

    public static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "agent.command.duration", "s", "Time to run one !command.");

    // ---- model and tools --------------------------------------------------

    /// <summary>
    /// Token usage tagged by purpose and channel. The gen_ai metrics from the chat client cover per-model
    /// usage; this one exists because cost attribution per channel is the question operators actually ask.
    /// </summary>
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "agent.tokens", "{token}", "Tokens consumed, by purpose, channel and direction.");

    public static readonly Counter<long> Tools = Meter.CreateCounter<long>(
        "agent.tools", "{invocation}", "Tool invocations made by the model.");

    public static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>(
        "agent.tool.duration", "s", "Time spent inside one tool invocation.");

    // ---- coding tasks -----------------------------------------------------

    public static readonly Counter<long> Tasks = Meter.CreateCounter<long>(
        "agent.tasks", "{task}", "Coding tasks by lifecycle transition.");

    public static readonly Histogram<double> TaskDuration = Meter.CreateHistogram<double>(
        "agent.task.duration", "s", "Time from picking up a coding task to publishing or failing it.");

    public static readonly Counter<long> SandboxCommands = Meter.CreateCounter<long>(
        "agent.sandbox.commands", "{command}", "Commands executed in a sandbox, by mode and outcome.");

    public static readonly Histogram<double> SandboxCommandDuration = Meter.CreateHistogram<double>(
        "agent.sandbox.command.duration", "s", "Time spent running one sandboxed command.");

    // ---- channels ---------------------------------------------------------

    /// <summary>Poll cycles per channel. A healthy poller that finds nothing still increments this, so silence means broken.</summary>
    public static readonly Counter<long> Polls = Meter.CreateCounter<long>(
        "agent.polls", "{cycle}", "Poll cycles run by a channel, by outcome.");

    private static readonly List<Func<int>> QueueDepthProviders = [];
    private static readonly Lock QueueDepthLock = new();

    static AgentTelemetry()
    {
        Meter.CreateObservableGauge<long>(
            "agent.queue.depth",
            () =>
            {
                lock (QueueDepthLock)
                {
                    long total = 0;
                    foreach (var provider in QueueDepthProviders)
                    {
                        try
                        {
                            total += provider();
                        }
                        catch (Exception)
                        {
                            // A broken provider must not stop the meter from reporting.
                        }
                    }

                    return total;
                }
            },
            "{event}",
            "Inbound events waiting to be processed.");
    }

    /// <summary>Registers a source for the queue-depth gauge. Called once per queue at start-up.</summary>
    public static void TrackQueueDepth(Func<int> depth)
    {
        lock (QueueDepthLock)
        {
            QueueDepthProviders.Add(depth);
        }
    }

    /// <summary>
    /// Records token usage tagged by purpose and channel. The chat client's own gen_ai metrics cover
    /// per-model usage; this adds the dimension operators ask about, which is cost per channel.
    /// </summary>
    public static void RecordTokens(Microsoft.Extensions.AI.UsageDetails? usage, string purpose, string channel, string model)
    {
        if (usage is null)
        {
            return;
        }

        if (usage.InputTokenCount is > 0 and var input)
        {
            Tokens.Add(input, new TagList { { "purpose", purpose }, { "channel", channel }, { "model", model }, { "direction", "input" } });
        }

        if (usage.OutputTokenCount is > 0 and var output)
        {
            Tokens.Add(output, new TagList { { "purpose", purpose }, { "channel", channel }, { "model", model }, { "direction", "output" } });
        }
    }

    /// <summary>Marks the activity failed and records the exception type, without the message (it may contain content).</summary>
    public static void Failed(this Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity.SetTag("error.type", exception.GetType().FullName);
    }

    public static Activity? Tag(this Activity? activity, string key, object? value)
    {
        activity?.SetTag(key, value);
        return activity;
    }
}
