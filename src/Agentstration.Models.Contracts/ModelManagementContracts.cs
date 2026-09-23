using Agentstration.Models;
using Agentstration.Resources;

namespace Agentstration.Models.Contracts;

public sealed record ModelProviderPropertiesResponse(
    string DisplayName,
    string AdapterType,
    string ContributionId,
    string ExtensionName,
    string ExtensionNamespace,
    string RegistrationSource,
    string Status,
    string? EndpointDisplayName,
    int ModelCount,
    Uri? Endpoint = null,
    DateTimeOffset? LastCheckedAt = null);

public sealed record ModelProviderResponse(
    string Id,
    string Name,
    ModelProviderPropertiesResponse Properties,
    string Namespace = "default",
    ResourceScopeRef? ScopeRef = null);

public sealed record AvailableModelResponse(
    string Name,
    string DisplayName,
    string Status,
    ModelSpecification Specification,
    ModelIdentity? Identity = null,
    ModelSpecification? ObservedSpecification = null,
    ModelSpecificationOverride? SpecificationOverride = null);

public sealed record ModelDiscoveryDiffResponse(
    int Created,
    int Updated,
    int Unchanged,
    int Missing,
    int Reappeared,
    int Total);

public sealed record ModelProviderStatusResponse(string Provider, string Status, DateTimeOffset CheckedAt, string? Details);
public sealed record CreateModelProviderRequest(
    string Name,
    ModelProviderProperties Properties,
    string Namespace = "default",
    ResourceScopeRef? ScopeRef = null);
public sealed record PutModelProviderRequest(ModelProviderProperties Properties);
public sealed record ModelProviderUsageResponse(string ResourceType, string ResourceId, string Name, string DisplayName);
public sealed record ModelProviderUsagesResponse(IReadOnlyList<ModelProviderUsageResponse> Value, int Count);
public sealed record PreviewModelProfileOptionMigrationRequest(string TargetVersion);
public sealed record ModelProfileOptionMigrationPreviewResponse(
    string ProfileName,
    string ProfileNamespace,
    string ProviderType,
    VersionedExtensionOptions Source,
    VersionedExtensionOptions Target);

public sealed record CreateModelProfileRequest(
    string Name,
    ModelProfileProperties Properties,
    string Namespace = "default",
    ResourceScopeRef? ScopeRef = null);

public sealed record PutModelProfileRequest(ModelProfileProperties Properties);

public sealed record ModelProviderReferenceResponse(string ResourceId, string Name, string? DisplayName = null, string? ContributionId = null, string? Status = null, string Namespace = "default");
public sealed record ModelReferenceResponse(
    string Name,
    string? Status = null,
    ModelSpecification? Specification = null,
    ModelSpecification? ObservedSpecification = null,
    ModelSpecificationOverride? SpecificationOverride = null);

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
    string Namespace = "default",
    ResourceScopeRef? ScopeRef = null);

public sealed record ModelProfileUsageResponse(string ResourceType, string ResourceId, string Name, string DisplayName);
public sealed record ModelProfileUsagesResponse(IReadOnlyList<ModelProfileUsageResponse> Value, int Count);

public sealed record ModelProfileIdentityResponse(string ResourceId, string Name, string? DisplayName = null, string Namespace = "default");
public sealed record EffectiveModelOptionsResponse(
    ModelGenerationOptions Generation,
    ModelReasoningOptions Reasoning,
    ModelOutputOptions Output);

public sealed record ModelCapabilityResponse(
    string Name,
    string ProviderSupport,
    string ModelSupport,
    string AdapterSupport,
    string EffectiveSupport,
    IReadOnlyList<string> SupportedValues);

public sealed record ModelCompatibilityIssueResponse(string Capability, string EffectiveSupport, string Message);

public sealed record ModelProfileResolutionResponse(
    ModelProfileIdentityResponse Profile,
    ModelProviderReferenceResponse? Provider,
    ModelReferenceResponse Model,
    EffectiveModelOptionsResponse EffectiveOptions,
    string Status,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ModelCapabilityResponse>? Capabilities = null,
    IReadOnlyList<ModelCompatibilityIssueResponse>? Incompatibilities = null);

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
