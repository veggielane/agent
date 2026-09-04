namespace Agent.Coding;

/// <summary>A tool call violated workspace policy: path escape, protected path, or a disallowed command.</summary>
public sealed class CodingPolicyException : Exception
{
    public CodingPolicyException(string message)
        : base(message)
    {
    }
}

/// <summary>A git command run by the orchestrator returned a non-zero exit code.</summary>
public sealed class GitException : Exception
{
    public GitException(string command, int exitCode, string output)
        : base($"git {command} failed with exit code {exitCode}: {output.Trim()}")
    {
        Command = command;
        ExitCode = exitCode;
        Output = output;
    }

    public string Command { get; }

    public int ExitCode { get; }

    public string Output { get; }
}
