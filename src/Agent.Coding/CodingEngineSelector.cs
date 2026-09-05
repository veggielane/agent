using Agent.Coding.OpenCode;
using Microsoft.Extensions.Options;

namespace Agent.Coding;

public enum CodingEngineKind
{
    /// <summary>The built-in loop: our own tools, budgets and policy.</summary>
    Native,

    /// <summary>Hand the task to the opencode CLI running inside the sandbox.</summary>
    OpenCode,
}

/// <summary>Picks the engine per run from <c>Coding:Engine</c>, so the choice hot-reloads.</summary>
public sealed class CodingEngineSelector : ICodingEngine
{
    private readonly IOptionsMonitor<CodingOptions> _options;
    private readonly CodingEngine _native;
    private readonly OpenCodeCodingEngine _openCode;

    public CodingEngineSelector(IOptionsMonitor<CodingOptions> options, CodingEngine native, OpenCodeCodingEngine openCode)
    {
        _options = options;
        _native = native;
        _openCode = openCode;
    }

    public CodingEngineKind Current => _options.CurrentValue.Engine;

    public Task<CodingResult> RunAsync(CodingRun run, IProgress<string>? progress, CancellationToken cancellationToken)
        => Current == CodingEngineKind.OpenCode
            ? _openCode.RunAsync(run, progress, cancellationToken)
            : _native.RunAsync(run, progress, cancellationToken);
}
