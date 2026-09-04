using System.Diagnostics;

namespace Agent.Mcp.Tests.Support;

internal static class Wait
{
    /// <summary>Polls until the condition holds; fails the test after the timeout instead of hanging.</summary>
    public static Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
        => UntilAsync(() => Task.FromResult(condition()), timeout, because);

    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.Elapsed > limit)
            {
                Assert.Fail($"Condition not met within {limit.TotalSeconds:0.#}s{(because is null ? string.Empty : ": " + because)}");
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
