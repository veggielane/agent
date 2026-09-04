using Microsoft.Extensions.Options;

namespace Agent.Infrastructure.Tests.Support;

public static class TestOptions
{
    public static IOptionsMonitor<T> Monitor<T>(T value)
        where T : class
        => new StaticMonitor<T>(value);

    private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        public StaticMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
