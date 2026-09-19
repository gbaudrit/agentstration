using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Extensions.Contracts;

public sealed record CreateExtensionRegistrationRequest(
    string Name,
    ExtensionRegistrationProperties Properties,
    string Namespace = "default",
    ResourceScopeRef? ScopeRef = null);
public sealed record PutExtensionRegistrationRequest(ExtensionRegistrationProperties Properties);
public sealed record ExtensionDiscoveryResponse(int Sources, int Created, int Updated, int Unchanged);
public sealed record ExtensionIdentityResponse(string Id, string Name, string Version, string? Description);
public sealed record ExtensionContributionResponse(string Kind, string Id);
public sealed record ExtensionValueRequirementResponse(
    string ContributionKind,
    string ContributionId,
    string Id,
    bool Required,
    string ValueType,
    string Protection,
    string? Description);
public sealed record ExtensionOptionSetVersionResponse(string Version, string SchemaDigest, JsonElement Schema, bool Deprecated);
public sealed record ExtensionOptionMigrationDescriptorResponse(string FromVersion, string ToVersion);
public sealed record ExtensionOptionSetResponse(
    string Id,
    string ContributionKind,
    string ContributionId,
    string Scope,
    string PreferredVersion,
    IReadOnlyList<ExtensionOptionSetVersionResponse> Versions,
    IReadOnlyList<ExtensionOptionMigrationDescriptorResponse> Migrations);
public sealed record ExtensionOptionUsageResponse(
    string ProfileName,
    string ProfileNamespace,
    string OptionSet,
    string Version,
    string SchemaDigest,
    string Status,
    IReadOnlyList<string> Issues);
public sealed record ExtensionProviderBindingResponse(string Name, string Namespace, string ContributionId);
public sealed record ExtensionResponse(
    string RegistrationName,
    string RegistrationNamespace,
    Uri Endpoint,
    string Status,
    ExtensionIdentityResponse? Extension,
    IReadOnlyList<ExtensionContributionResponse> Contributions,
    IReadOnlyList<ExtensionOptionSetResponse> OptionSets,
    IReadOnlyList<ExtensionOptionUsageResponse> Usages,
    IReadOnlyList<ExtensionProviderBindingResponse> Providers,
    string? Details,
    string DiscoverySource,
    bool RegistrationEnabled = true,
    AepEnrollmentMode EnrollmentMode = AepEnrollmentMode.Disabled,
    ResourceScopeRef? RegistrationScopeRef = null,
    IReadOnlyList<ExtensionValueRequirementResponse>? ValueRequirements = null);
public sealed record ExtensionInventoryItemResponse(
    string Key,
    string? RegistrationName,
    string RegistrationNamespace,
    ResourceScopeRef? RegistrationScopeRef,
    Guid? EnrollmentInstanceId,
    string DisplayName,
    string ExtensionId,
    string? Version,
    Uri Endpoint,
    string RegistrationSource,
    bool RegistrationEnabled,
    string AvailabilityStatus,
    AepEnrollmentState? EnrollmentStatus,
    DateTimeOffset? AnnouncedAt,
    ExtensionResponse? Extension,
    IReadOnlyList<ExtensionInventoryConnectionResponse> Connections);
public sealed record ExtensionInventoryConnectionResponse(
    string RegistrationName,
    string RegistrationNamespace,
    ResourceScopeRef? RegistrationScopeRef,
    string DisplayName,
    Uri Endpoint,
    string Source,
    bool Enabled,
    AepEnrollmentMode EnrollmentMode,
    string AvailabilityStatus);
public sealed record AepEnrollmentSettingsSnapshot(
    bool PairingCodeEnabled,
    bool SharedKeyFileEnabled,
    bool PairingCodeConfigurable,
    bool SharedKeyFileConfigurable,
    string? ETag);
public sealed record AepPairingCodeResult(Guid RequestId, string Code, DateTimeOffset ExpiresAt);
