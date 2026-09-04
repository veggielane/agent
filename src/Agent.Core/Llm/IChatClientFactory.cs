using Microsoft.Extensions.AI;

namespace Agent.Core.Llm;

public interface IChatClientFactory
{
    /// <summary>Returns a client (with function invocation middleware) for the resolved model.</summary>
    IChatClient Create(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null);

    string ResolveModel(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null);
}
