using YamlDotNet.Serialization;

namespace Agent.Core.Commands.Yaml;

/// <summary>Schema of a <c>commands/*.yaml</c> file.</summary>
public sealed class PromptCommandDefinition
{
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "aliases")]
    public List<string> Aliases { get; set; } = [];

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>users | team | admin</summary>
    [YamlMember(Alias = "role")]
    public string? Role { get; set; }

    [YamlMember(Alias = "channels")]
    public List<string>? Channels { get; set; }

    /// <summary>AnswerModel | CodingModel | a concrete model name. Empty = default answer model.</summary>
    [YamlMember(Alias = "model")]
    public string? Model { get; set; }

    /// <summary>Tool names the prompt may use. Empty = every tool the caller may use; ["none"] = no tools.</summary>
    [YamlMember(Alias = "tools")]
    public List<string>? Tools { get; set; }

    [YamlMember(Alias = "prompt")]
    public string? Prompt { get; set; }

    /// <summary>ask (default) or task.</summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    [YamlMember(Alias = "hidden")]
    public bool Hidden { get; set; }

    /// <summary>Optional extra system instructions for this command.</summary>
    [YamlMember(Alias = "system")]
    public string? System { get; set; }
}
