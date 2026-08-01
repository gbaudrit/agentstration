using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public sealed record ResourceEnvelope<T>(string? Location, IReadOnlyDictionary<string, string>? Tags, T Properties);
public sealed record AgentResourceRequest
{
    public required string Type { get; init; }
    public required string ApiVersion { get; init; }
    public required string Name { get; init; }
    public required string ResourceGroup { get; init; }
    public required string Location { get; init; }
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
    public required AgentProperties Properties { get; init; }
}
public sealed record CreateRevisionRequest(string Environment, string RuntimeProfileId, AgentHostingMode HostingMode);
public sealed record CreateDeploymentRequest(string RevisionId, string Environment, string RuntimeProfileId, AgentHostingMode HostingMode);
public sealed record RouteAndExecuteRequest(string Input);
public sealed record RouteAndExecuteResponse(string AgentId, double Confidence, string Reason, string Output);
public sealed record PagedResponse<T>(IReadOnlyList<T> Value, string? NextLink);
