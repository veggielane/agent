using WireMock;
using WireMock.Server;

namespace Agent.Infrastructure.Tests.Support;

public static class WireMockExtensions
{
    /// <summary>The requests the server has received so far, oldest first.</summary>
    public static IReadOnlyList<IRequestMessage> Requests(this WireMockServer server)
        => server.LogEntries.Select(e => e.RequestMessage).Where(r => r is not null).Select(r => r!).ToList();
}
