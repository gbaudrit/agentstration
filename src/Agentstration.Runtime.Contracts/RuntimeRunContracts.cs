using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Contracts;

public sealed record CreateRuntimeRunRequest
{
    public required RuntimeAgentReference Agent { get; init; }
    public required RuntimeRunInput Input { get; init; }
    public RuntimeExecutionOptions Execution { get; init; } = new();
    public RuntimeRunOrigin Origin { get; init; } = RuntimeRunOrigin.Api;
    public string? Initiator { get; init; }
}

public sealed record RuntimeRunPageResponse(IReadOnlyList<RuntimeRun> Value, string? NextLink = null);
