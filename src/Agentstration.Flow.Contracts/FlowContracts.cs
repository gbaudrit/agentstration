using System.Text.Json;
using Agentstration.Flow;

namespace Agentstration.Flow.Contracts;

public sealed record CreateFlowRequest(string Name, string? Description, FlowKind Kind, string Version, bool Enabled, FlowSpec Spec, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record UpdateFlowRequest(string? Description, FlowKind Kind, string Version, bool Enabled, FlowSpec Spec, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record CreateFlowVersionRequest(string Version, bool Activate = true);
public sealed record FlowResponse(string Id, string Name, string? Description, FlowKind Kind, string Version, bool Enabled, string? ActiveVersion, FlowSpec Spec, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record FlowSummaryResponse(string Id, string Name, string? Description, FlowKind Kind, string Version, bool Enabled, string? ActiveVersion, DateTimeOffset UpdatedAt);
public sealed record FlowVersionResponse(string FlowId, string Version, string? Description, FlowKind Kind, FlowSpec Spec, IReadOnlyDictionary<string, string> Metadata, DateTimeOffset PublishedAt, FlowGraphDefinition? Graph = null, string? DefinitionHash = null, string? ReleaseNotes = null);
public sealed record FlowPageResponse(IReadOnlyList<FlowSummaryResponse> Value, string? NextLink);
public sealed record CreateFlowRunRequest(JsonElement Input, string? Version = null, string? DeploymentResourceId = "local", FlowRunTrigger Trigger = FlowRunTrigger.Manual, string? StartedBy = null, string? CorrelationId = null, IReadOnlyDictionary<string, JsonElement>? Options = null);
public sealed record FlowRunPageResponse(IReadOnlyList<FlowRun> Value, string? NextLink);
public sealed record CreateFlowDraftRequest(string Name, string DisplayName, string? Description = null, string ResourceGroup = "default", string Location = "local", IReadOnlyDictionary<string, string>? Tags = null, string Template = "AgentRouting");
public sealed record UpdateFlowDraftRequest(string DisplayName, string? Description, string ResourceGroup, string Location, IReadOnlyDictionary<string, string>? Tags, FlowGraphDefinition Definition, string UpdatedBy = "local-user");
public sealed record PublishFlowDraftRequest(string Version, string? ReleaseNotes = null, bool Activate = true);
public sealed record FlowDraftResponse(FlowDraft Value, string ETag);
public sealed record FlowValidationResponse(bool IsValid, IReadOnlyList<FlowValidationIssue> Issues);
public sealed record FlowSourceResponse(string Source, string Format, long Revision);
public sealed record ReplaceFlowSourceRequest(string Source, string Format = "yaml", string UpdatedBy = "local-user");
