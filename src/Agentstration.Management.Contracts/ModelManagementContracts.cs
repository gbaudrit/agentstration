using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public sealed record ValueResponse<T>(IReadOnlyList<T> Value);

public sealed record ModelProviderPropertiesResponse(
    string DisplayName,
    string ProviderType,
    string ManagementMode,
    string Status,
    string? EndpointDisplayName,
    int ModelCount,
    Uri? Endpoint = null,
    DateTimeOffset? LastCheckedAt = null);

public sealed record ModelProviderResponse(
    string Id,
    string Name,
    ModelProviderPropertiesResponse Properties);

public sealed record AvailableModelResponse(
    string Name,
    string DisplayName,
    string Status,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ModelProviderStatusResponse(string Provider, string Status, DateTimeOffset CheckedAt, string? Details);
public sealed record CreateModelProviderRequest(
    string Name,
    ModelProviderProperties Properties);
public sealed record PutModelProviderRequest(ModelProviderProperties Properties);
public sealed record ModelProviderUsageResponse(string ResourceType, string ResourceId, string Name, string DisplayName);
public sealed record ModelProviderUsagesResponse(IReadOnlyList<ModelProviderUsageResponse> Value, int Count);

public sealed record CreateModelProfileRequest(
    string Name,
    ModelProfileProperties Properties);

public sealed record PutModelProfileRequest(ModelProfileProperties Properties);

public sealed record ModelProviderReferenceResponse(string ResourceId, string Name, string? DisplayName = null, string? ProviderType = null, string? Status = null);
public sealed record ModelReferenceResponse(string Name, string? Status = null, IReadOnlyList<string>? Capabilities = null);

public sealed record ModelProfileSummaryPropertiesResponse(
    string DisplayName,
    string? Description,
    ModelProviderReferenceResponse Provider,
    ModelReferenceResponse Model,
    ModelGenerationOptions Generation,
    ModelReasoningOptions Reasoning,
    ModelOutputOptions Output,
    string Status,
    int UsageCount);

public sealed record ModelProfileSummaryResponse(
    string Id,
    string Name,
    ModelProfileSummaryPropertiesResponse Properties,
    string Namespace = "default");

public sealed record ModelProfileUsageResponse(string ResourceType, string ResourceId, string Name, string DisplayName);
public sealed record ModelProfileUsagesResponse(IReadOnlyList<ModelProfileUsageResponse> Value, int Count);

public sealed record ModelProfileIdentityResponse(string ResourceId, string Name, string? DisplayName = null);
public sealed record EffectiveModelOptionsResponse(
    ModelGenerationOptions Generation,
    ModelReasoningOptions Reasoning,
    ModelOutputOptions Output);

public sealed record ModelProfileResolutionResponse(
    ModelProfileIdentityResponse Profile,
    ModelProviderReferenceResponse? Provider,
    ModelReferenceResponse Model,
    EffectiveModelOptionsResponse EffectiveOptions,
    string Status,
    IReadOnlyList<string> Warnings);

public sealed record DeclaredAgentModelResponse(ModelProfileIdentityResponse ModelProfile);
public sealed record ResolvedAgentModelResponse(
    ModelProviderReferenceResponse? Provider,
    ModelReferenceResponse Model,
    EffectiveModelOptionsResponse Options);
public sealed record AgentModelResponse(
    DeclaredAgentModelResponse Declared,
    ResolvedAgentModelResponse Resolved,
    string Status,
    IReadOnlyList<string> Warnings);

public sealed record CreateRuntimeProfileRequest(
    string Name,
    RuntimeProfileProperties Properties);
public sealed record PutRuntimeProfileRequest(RuntimeProfileProperties Properties);
public sealed record RuntimeProfileSummaryResponse(
    string Id,
    string Name,
    RuntimeProfileProperties Properties,
    int UsageCount);
public sealed record RuntimeProfileUsageResponse(
    string ResourceId,
    string Name,
    string Environment,
    string AgentResourceId);
public sealed record RuntimeProfileUsagesResponse(IReadOnlyList<RuntimeProfileUsageResponse> Value, int Count);
