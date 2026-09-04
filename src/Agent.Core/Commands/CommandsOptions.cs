namespace Agent.Core.Commands;

public sealed class CommandsOptions
{
    public const string SectionName = "Commands";

    public string Prefix { get; set; } = "!";

    /// <summary>Directory with YAML prompt-template commands. Relative to the working directory.</summary>
    public string Directory { get; set; } = "commands";

    /// <summary>Reply "unknown command" when the prefix matched but no command did.</summary>
    public bool ReplyToUnknown { get; set; } = true;

    /// <summary>How many history messages a YAML command's {{context}} receives.</summary>
    public int ContextMessages { get; set; } = 40;
}
