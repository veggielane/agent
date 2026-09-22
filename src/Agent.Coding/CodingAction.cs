namespace Agent.Coding;

public enum CodingActionKind
{
    /// <summary>A file was created or overwritten.</summary>
    Write,

    /// <summary>An existing file was changed in place.</summary>
    Edit,

    /// <summary>A command ran, or was refused, in the sandbox.</summary>
    Run,
}

/// <summary>
/// One thing the coding loop did to the repository or ran in it, reported as it happens so the task event log
/// holds a trail even when the run is cut short. <see cref="Detail"/> is a repository-relative path or a command
/// line with its outcome; file contents and command output never travel through it.
/// </summary>
public sealed record CodingAction(CodingActionKind Kind, string Detail);
