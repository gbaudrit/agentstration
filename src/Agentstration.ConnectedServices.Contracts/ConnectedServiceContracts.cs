using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.ConnectedServices.Contracts;

public static class ConnectedServiceResourceKinds
{
    public const string ConnectedServiceType = "ConnectedServiceType";
    public const string ConnectedService = "ConnectedService";
    public const string ConnectedServiceOperationType = "ConnectedServiceOperationType";
    public const string ConnectedServiceOperationDiscovery = "ConnectedServiceOperationDiscovery";
    public const string ConnectedServiceOperation = "ConnectedServiceOperation";
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectedServiceValueType>))]
public enum ConnectedServiceValueType
{
    [JsonStringEnumMemberName("string")] Text,
    [JsonStringEnumMemberName("integer")] WholeNumber,
    [JsonStringEnumMemberName("number")] DecimalNumber,
    [JsonStringEnumMemberName("boolean")] Logical
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectedServiceValueProtection>))]
public enum ConnectedServiceValueProtection
{
    [JsonStringEnumMemberName("standard")] Standard,
    [JsonStringEnumMemberName("secured")] Secured
}

public sealed record ConnectedServiceInputDescriptor(
    string Id,
    bool Required,
    ConnectedServiceValueType Type,
    ConnectedServiceValueProtection Protection,
    string? Format = null,
    string? Description = null);

public sealed record ConnectedServiceExportDescriptor(
    string Id,
    ConnectedServiceValueType Type,
    ConnectedServiceValueProtection Protection,
    string? Format = null,
    string? Description = null,
    string? SourceInputId = null);

public sealed record ConnectedServiceTypeVersionDescriptor(
    string Version,
    string SchemaDigest,
    JsonElement Schema,
    IReadOnlyList<ConnectedServiceInputDescriptor> Inputs,
    IReadOnlyList<ConnectedServiceExportDescriptor> Exports,
    bool Deprecated = false)
{
    public ConnectedServiceOperationDiscoveryDescriptor? OperationDiscovery { get; init; }
}

public sealed record ConnectedServiceDiscoveryFlowReference
{
    public required ResourceReference Flow { get; init; }
    public required Guid FlowUid { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required string Contract { get; init; }
}

public sealed record ConnectedServiceOperationDiscoveryDescriptor
{
    public required ConnectedServiceDiscoveryFlowReference Flow { get; init; }
    public IReadOnlyList<ConnectedServiceOperationTypeReference> ProducedTypes { get; init; } = [];
}

public sealed record ConnectedServiceTypeProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public required string PreferredVersion { get; init; }
    public IReadOnlyList<ConnectedServiceTypeVersionDescriptor> Versions { get; init; } = [];
    public DescendantUsePolicy UsePolicy { get; init; } = new();
}

public sealed record ConnectedServiceTypeResource : Resource
{
    public ConnectedServiceTypeProperties Definition { get; init; } = null!;
}

public sealed record ConnectedServiceTypeReference
{
    public required ResourceReference Type { get; init; }
    public required Guid TypeUid { get; init; }
    public required string Version { get; init; }
    public required string SchemaDigest { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectedServiceOperationInteraction>))]
public enum ConnectedServiceOperationInteraction
{
    [JsonStringEnumMemberName("request-response")] RequestResponse,
    [JsonStringEnumMemberName("server-stream")] ServerStream,
    [JsonStringEnumMemberName("client-stream")] ClientStream,
    [JsonStringEnumMemberName("duplex-stream")] DuplexStream,
    [JsonStringEnumMemberName("subscription")] Subscription
}

public sealed record ConnectedServiceOperationInvocationDescriptor(
    string ProviderOperationId,
    string ContractVersion);

public sealed record ConnectedServiceOperationTypeVersionDescriptor(
    string Version,
    string SchemaDigest,
    JsonElement DefinitionSchema,
    ConnectedServiceOperationInteraction Interaction,
    ConnectedServiceOperationInvocationDescriptor Invocation,
    bool Deprecated = false);

public sealed record ConnectedServiceOperationTypeProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public required string PreferredVersion { get; init; }
    public IReadOnlyList<ConnectedServiceOperationTypeVersionDescriptor> Versions { get; init; } = [];
    public DescendantUsePolicy UsePolicy { get; init; } = new();
}

public sealed record ConnectedServiceOperationTypeResource : Resource
{
    public ConnectedServiceOperationTypeProperties Definition { get; init; } = null!;
}

public sealed record ConnectedServiceOperationTypeReference
{
    public required ResourceReference Type { get; init; }
    public required Guid TypeUid { get; init; }
    public required string Version { get; init; }
    public required string SchemaDigest { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectedServiceBindingKind>))]
public enum ConnectedServiceBindingKind
{
    [JsonStringEnumMemberName("parameter")] Parameter,
    [JsonStringEnumMemberName("secret")] Secret
}

public sealed record ConnectedServiceValueBinding
{
    public required string InputId { get; init; }
    public required ConnectedServiceBindingKind Kind { get; init; }
    public ParameterReference? Parameter { get; init; }
    public SecretReference? Secret { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectedServiceAvailability>))]
public enum ConnectedServiceAvailability
{
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("stale")] Stale,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    [JsonStringEnumMemberName("disabled")] Disabled
}

public sealed record ConnectedServiceObservedState
{
    public required ConnectedServiceAvailability Availability { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
}

public sealed record ConnectedServiceProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public required ConnectedServiceTypeReference Type { get; init; }
    public required JsonElement Options { get; init; }
    public IReadOnlyList<ConnectedServiceValueBinding> Bindings { get; init; } = [];
    public DescendantUsePolicy UsePolicy { get; init; } = new();
}

public sealed record ConnectedServiceResource : Resource
{
    public ConnectedServiceProperties Definition { get; init; } = null!;
    public ConnectedServiceObservedState? ObservedState { get; init; }
}

public sealed record ConnectedServiceReference(ResourceAddress Address, ResourceScopeRef ScopeRef);
public sealed record ConnectedServiceResolutionContext(ResourceScopeRef ConsumerScopeRef, ResourceAddress Consumer);

public sealed record ConnectedServiceRevisionReference
{
    public required ConnectedServiceReference Service { get; init; }
    public required Guid ServiceUid { get; init; }
    public required long Generation { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectedServiceOperationAvailability>))]
public enum ConnectedServiceOperationAvailability
{
    [JsonStringEnumMemberName("available")] Available,
    [JsonStringEnumMemberName("stale")] Stale,
    [JsonStringEnumMemberName("unavailable")] Unavailable
}

public sealed record ConnectedServiceOperationObservedState
{
    public required ConnectedServiceOperationAvailability Availability { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
}

public sealed record ConnectedServiceOperationDiscoveryProvenance
{
    public required ScopedResourceAddress Discovery { get; init; }
    public required Guid DiscoveryUid { get; init; }
    public required string ManifestDigest { get; init; }
    public required DateTimeOffset DiscoveredAt { get; init; }
}

public sealed record ConnectedServiceOperationProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required ConnectedServiceRevisionReference Service { get; init; }
    public required ConnectedServiceOperationTypeReference Type { get; init; }
    public required string ExternalId { get; init; }
    public required JsonElement Definition { get; init; }
    public required string DefinitionDigest { get; init; }
    public required ConnectedServiceOperationDiscoveryProvenance Provenance { get; init; }
}

public sealed record ConnectedServiceOperationResource : Resource
{
    public ConnectedServiceOperationProperties Definition { get; init; } = null!;
    public ConnectedServiceOperationObservedState ObservedState { get; init; } = null!;
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectedServiceOperationDiscoveryState>))]
public enum ConnectedServiceOperationDiscoveryState
{
    Pending,
    Running,
    WaitingForInput,
    WaitingForChild,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public sealed record ConnectedServiceOperationCandidate
{
    public required string ExternalId { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required ConnectedServiceOperationTypeReference Type { get; init; }
    public required JsonElement Definition { get; init; }
}

public sealed record ConnectedServiceOperationDiscoveryDiagnostic(
    string Code,
    string Message,
    string? Target = null);

public sealed record ConnectedServiceOperationDiscoveryManifest
{
    public string Contract { get; init; } = ConnectedServiceOperationDiscoveryContracts.V1;
    public IReadOnlyList<ConnectedServiceOperationCandidate> Operations { get; init; } = [];
    public IReadOnlyList<ConnectedServiceOperationDiscoveryDiagnostic> Diagnostics { get; init; } = [];
}

public static class ConnectedServiceOperationDiscoveryContracts
{
    public const string V1 = "connected-service-operation-discovery/v1";
}

public sealed record ConnectedServiceOperationDiscoveryInput
{
    public required string Contract { get; init; }
    public required string DiscoveryId { get; init; }
    public required ConnectedServiceRevisionReference Service { get; init; }
    public required ConnectedServiceTypeReference ServiceType { get; init; }
    public required string CorrelationId { get; init; }
    public required ConnectedServiceOperationDiscoveryCaller Caller { get; init; }
}

public sealed record ConnectedServiceOperationDiscoveryCaller(
    Guid PrincipalId,
    Guid TenantId,
    Guid WorkspaceId);

public sealed record ConnectedServiceOperationDiscoveryResource : Resource
{
    public required ConnectedServiceRevisionReference Service { get; init; }
    public required ConnectedServiceTypeReference ServiceType { get; init; }
    public required ConnectedServiceDiscoveryFlowReference Flow { get; init; }
    public required string FlowRunId { get; init; }
    public required ConnectedServiceOperationDiscoveryState State { get; init; }
    public required string CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string RequestHash { get; init; }
    public required Guid CreatedBy { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ConnectedServiceOperationDiscoveryManifest? Manifest { get; init; }
    public string? ManifestDigest { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ResolvedConnectedServiceInput : IDisposable
{
    public required string Id { get; init; }
    public required ConnectedServiceValueType Type { get; init; }
    public JsonElement? Value { get; init; }
    public SecretValue? Secret { get; init; }
    public void Dispose() => Secret?.Dispose();
    public override string ToString() => "[REDACTED]";
}

public sealed class ResolvedConnectedService : IDisposable
{
    public required ConnectedServiceReference Reference { get; init; }
    public required ConnectedServiceTypeReference Type { get; init; }
    public required JsonElement Options { get; init; }
    public required IReadOnlyList<ConnectedServiceExportDescriptor> Exports { get; init; }
    public required IReadOnlyList<ResolvedConnectedServiceInput> Inputs { get; init; }
    public void Dispose()
    {
        foreach (var input in Inputs) input.Dispose();
    }
    public override string ToString() => "[REDACTED]";
}

public interface IConnectedServiceResolver
{
    Task<ResolvedConnectedService?> ResolveAsync(
        ConnectedServiceReference service,
        ConnectedServiceResolutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record CreateConnectedServiceRequest(
    string Name,
    ConnectedServiceProperties Properties,
    ResourceScopeRef? ScopeRef = null);

public sealed record PutConnectedServiceRequest(ConnectedServiceProperties Properties);
public sealed record CreateConnectedServiceTypeRequest(
    string Name,
    ConnectedServiceTypeProperties Properties,
    ResourceScopeRef? ScopeRef = null,
    ResourceNamespace? Namespace = null);
public sealed record PutConnectedServiceTypeRequest(ConnectedServiceTypeProperties Properties);
public sealed record CreateConnectedServiceOperationTypeRequest(
    string Name,
    ConnectedServiceOperationTypeProperties Properties,
    ResourceScopeRef? ScopeRef = null,
    ResourceNamespace? Namespace = null);
public sealed record PutConnectedServiceOperationTypeRequest(ConnectedServiceOperationTypeProperties Properties);
public sealed record StartConnectedServiceOperationDiscoveryRequest(
    string? CorrelationId = null,
    string? IdempotencyKey = null);
