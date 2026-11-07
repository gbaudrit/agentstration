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
    bool Deprecated = false);

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

public sealed record ConnectedServiceUsage(string Kind, string Name, string DisplayName, string Url);

public interface IConnectedServiceUsageProvider
{
    Task<IReadOnlyList<ConnectedServiceUsage>> GetUsagesAsync(
        ScopedResourceAddress connectedService,
        CancellationToken cancellationToken = default);
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
public sealed record ConnectedServiceUsageResponse(string ResourceType, string Name, string DisplayName, string Url);
public sealed record ConnectedServiceUsagesResponse(IReadOnlyList<ConnectedServiceUsageResponse> Value, int Count);
