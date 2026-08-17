using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Flow.Contracts;

public sealed record CreateFlowRequest(string Name, string? Description, string Version, bool Enabled, FlowDefinition Definition, IReadOnlyDictionary<string, string>? Metadata = null)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record UpdateFlowRequest(string? Description, string Version, bool Enabled, FlowDefinition Definition, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record CreateFlowVersionRequest(string Version, bool Activate = true);
public sealed record FlowResponse(string Id, string Name, string? Description, string Version, bool Enabled, string? ActiveVersion, FlowDefinition Definition, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record FlowSummaryResponse(string Id, string Name, string? Description, FlowKind FlowKind, string Version, bool Enabled, string? ActiveVersion, DateTimeOffset UpdatedAt)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record FlowVersionResponse(string FlowId, string Version, string? Description, FlowDefinition Definition, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset PublishedAt, FlowGraphDefinition? Graph = null, string? DefinitionHash = null, string? ReleaseNotes = null)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record FlowPageResponse(IReadOnlyList<FlowSummaryResponse> Value, string? NextLink);
public sealed record CreateFlowRunRequest(JsonElement Input, string? Version = null, string? DeploymentResourceId = "local", FlowRunTrigger Trigger = FlowRunTrigger.Manual, string? CorrelationId = null, IReadOnlyDictionary<string, JsonElement>? Options = null);
public sealed record FlowRunPageResponse(IReadOnlyList<FlowRun> Value, string? NextLink);
public sealed record SubmitInputResponseRequest(JsonElement Value);
public sealed record CreateFlowDraftRequest(string Name, string DisplayName, string? Description = null, IReadOnlyDictionary<string, string>? Tags = null, string Template = "AgentRouting");
public sealed record UpdateFlowDraftRequest(string DisplayName, string? Description, IReadOnlyDictionary<string, string>? Tags, FlowGraphDefinition Definition, string UpdatedBy = "local-user");
public sealed record PublishFlowDraftRequest(string Version, string? ReleaseNotes = null, bool Activate = true);
public sealed record FlowDraftResponse(FlowDraft Value, string ETag);
public sealed record FlowValidationResponse(bool IsValid, IReadOnlyList<FlowValidationIssue> Issues);
public sealed record FlowSourceResponse(string Source, string Format, long Revision);
public sealed record ReplaceFlowSourceRequest(string Source, string Format = "yaml", string UpdatedBy = "local-user");
