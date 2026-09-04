using System.ComponentModel;
using Spectre.Console.Cli;

namespace Agent.Cli.Commands;

public class GlobalSettings : CommandSettings
{
    [CommandOption("--server <URL>")]
    [Description("Agent host URL (default: Agent:Server in appsettings or AGENT_SERVER).")]
    public string? Server { get; init; }

    [CommandOption("--local")]
    [Description("Run the agent in-process instead of calling the host.")]
    public bool Local { get; init; }

    [CommandOption("--model <MODEL>")]
    [Description("Model to use for this request (honoured by the host only when Llm:AllowClientModelOverride is true).")]
    public string? Model { get; init; }

    [CommandOption("--json")]
    [Description("Print raw JSON instead of formatted output.")]
    public bool Json { get; init; }
}
