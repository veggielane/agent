namespace Agent.Core.Channels;

/// <summary>Where a request came from and where the reply goes.</summary>
public enum Channel
{
    Cli,
    Mattermost,
    Jira,
    GitLab,
}
