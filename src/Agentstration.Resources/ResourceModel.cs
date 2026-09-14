using System.Text.Json.Serialization;

namespace Agentstration.Resources;

public static class ResourceApiVersions
{
    public const string CoreV1 = "agentstration.io/v1";
    public const string V20260801 = CoreV1;
}

public sealed record ResourceMetadata
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();
}

public readonly record struct ResourceKey(string Kind, string Name, ResourceNamespace Namespace = default)
{
    public static ResourceKey Create(string kind, string name, ResourceNamespace @namespace = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(kind, name, @namespace);
    }

    public ResourceAddress Address => ResourceAddress.Create(Namespace, Kind, Name);
    public ScopedResourceAddress AtScope(ResourceScopeRef scopeRef) => ScopedResourceAddress.Create(scopeRef, Namespace, Kind, Name);
    public override string ToString() => Address.ToString();
}

public abstract record Resource
{
    public Guid Uid { get; init; }
    public required string ApiVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public ResourceMetadata Metadata { get; init; } = new();
    [JsonIgnore] public string Name => Metadata.Name;
    [JsonIgnore] public ResourceNamespace Namespace => Metadata.Namespace;
    [JsonIgnore] public ResourceAddress Address => ResourceAddress.Create(Namespace, Kind, Name);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResourceScopeRef? ScopeRef { get; init; }
    public long Generation { get; init; }
    public ResourceStatus Status { get; init; } = new() { ProvisioningState = ProvisioningState.Accepted };
    public string? ETag { get; init; }

    public Resource WithSystemState(Guid uid, ResourceScopeRef scopeRef, string etag) => this with
    {
        Uid = uid,
        ScopeRef = scopeRef,
        ETag = etag,
        Status = Status with { ResourceVersion = etag }
    };
}

public sealed record ResourceCondition
{
    public required string Type { get; init; }
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? LastTransitionTime { get; init; }
}

public sealed record ResourceStatus
{
    public required ProvisioningState ProvisioningState { get; init; }
    public string? ResourceVersion { get; init; }
    public IReadOnlyList<ResourceCondition> Conditions { get; init; } = [];
}

public sealed record ResourceReference
{
    public ResourceReference(string name, ResourceScopeRef? scopeRef = null, ResourceNamespace? @namespace = null)
    {
        Name = name;
        ScopeRef = scopeRef;
        Namespace = @namespace;
    }

    public string Name { get; init; }
    public ResourceScopeRef? ScopeRef { get; init; }
    [JsonIgnore] public string ResourceId => Name;
    public ResourceNamespace? Namespace { get; init; }
    public ResourceAddress Resolve(ResourceNamespace ownerNamespace, string kind) =>
        ResourceAddress.Create(Namespace ?? ownerNamespace, kind, Name);
}

public enum ProvisioningState { Accepted, Validating, Creating, Updating, Succeeded, Failed, Deleting, Canceled }
public enum OperationStatus { Accepted, Running, Succeeded, Failed, Canceled }
