using System.Net;

namespace Agent.Channels.GitLab;

/// <summary>A non-success response from the GitLab API (404s on lookups are returned as null instead).</summary>
public sealed class GitLabApiException : Exception
{
    public GitLabApiException(HttpStatusCode statusCode, string? body, Uri? requestUri)
        : base($"GitLab API returned {(int)statusCode} {statusCode} for {requestUri?.PathAndQuery ?? "request"}{(string.IsNullOrWhiteSpace(body) ? string.Empty : ": " + body)}")
    {
        StatusCode = statusCode;
        Body = body;
        RequestUri = requestUri;
    }

    public HttpStatusCode StatusCode { get; }

    public string? Body { get; }

    public Uri? RequestUri { get; }
}
