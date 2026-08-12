using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public record ResourceDeclaration<TDefinition>
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public required ResourceMetadata Metadata { get; init; }
    public required TDefinition Definition { get; init; }
}

public sealed record AgentResourceRequest : ResourceDeclaration<AgentProperties>;
public sealed record CreateRevisionRequest(string Environment, string RuntimeProfileName, AgentHostingMode HostingMode);
public sealed record CreateDeploymentRequest(string RevisionName, string Environment, string RuntimeProfileName, AgentHostingMode HostingMode);
public sealed record RouteAndExecuteRequest(string Input);
public sealed record RouteAndExecuteResponse(string AgentName, double Confidence, string Reason, string Output);
public sealed record PagedResponse<T>(IReadOnlyList<T> Value, string? NextLink);
