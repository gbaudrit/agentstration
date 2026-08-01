using Agentstration.Flow;

namespace Agentstration.Flow.Contracts;

public sealed record CreateFlowRequest(string Name, string? Description, FlowKind Kind, string Version, bool Enabled, FlowSpec Spec, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record UpdateFlowRequest(string? Description, FlowKind Kind, string Version, bool Enabled, FlowSpec Spec, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record CreateFlowVersionRequest(string Version, bool Activate = true);
public sealed record FlowResponse(string Id, string Name, string? Description, FlowKind Kind, string Version, bool Enabled, string? ActiveVersion, FlowSpec Spec, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record FlowSummaryResponse(string Id, string Name, string? Description, FlowKind Kind, string Version, bool Enabled, string? ActiveVersion, DateTimeOffset UpdatedAt);
public sealed record FlowVersionResponse(string FlowId, string Version, string? Description, FlowKind Kind, FlowSpec Spec, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset PublishedAt);
public sealed record FlowPageResponse(IReadOnlyList<FlowSummaryResponse> Value, string? NextLink);
