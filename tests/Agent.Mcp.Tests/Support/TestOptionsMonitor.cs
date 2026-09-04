using Microsoft.Extensions.Options;

namespace Agent.Mcp.Tests.Support;

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
