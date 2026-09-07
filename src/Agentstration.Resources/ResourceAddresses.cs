using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentstration.Resources;

/// <summary>
/// Identifies the canonical Management Workspace across module boundaries.
/// </summary>
public readonly record struct WorkspaceId(Guid Value)
{
    public static WorkspaceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum ResourceScopeType
{
    Instance,
    Tenant,
    Workspace
}

/// <summary>Identifies the immutable owner of a Management resource.</summary>
public readonly record struct ResourceScope
{
    private ResourceScope(ResourceScopeType type, Guid tenantId, Guid workspaceId)
    {
        Type = type;
        TenantId = tenantId;
        WorkspaceId = workspaceId;
    }

    public ResourceScopeType Type { get; }
    public Guid TenantId { get; }
    public Guid WorkspaceId { get; }
    public string Key => Type switch
    {
        ResourceScopeType.Instance => "instance",
        ResourceScopeType.Tenant => $"tenant:{TenantId:D}",
        ResourceScopeType.Workspace => $"workspace:{WorkspaceId:D}",
        _ => throw new InvalidOperationException($"Unsupported resource scope type '{Type}'.")
    };

    public static ResourceScope Instance => new(ResourceScopeType.Instance, Guid.Empty, Guid.Empty);
    public static ResourceScope Tenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant scope requires a non-empty Tenant ID.", nameof(tenantId));
        return new(ResourceScopeType.Tenant, tenantId, Guid.Empty);
    }

    public static ResourceScope Workspace(Guid tenantId, Guid workspaceId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A workspace scope requires a non-empty Tenant ID.", nameof(tenantId));
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace scope requires a non-empty Workspace ID.", nameof(workspaceId));
        return new(ResourceScopeType.Workspace, tenantId, workspaceId);
    }

    public static ResourceScope From(ResourceScopeType type, Guid tenantId, Guid workspaceId) => type switch
    {
        ResourceScopeType.Instance when tenantId == Guid.Empty && workspaceId == Guid.Empty => Instance,
        ResourceScopeType.Tenant when workspaceId == Guid.Empty => Tenant(tenantId),
        ResourceScopeType.Workspace => Workspace(tenantId, workspaceId),
        _ => throw new ArgumentException("The scope type and identifiers are inconsistent.")
    };

    public bool IsVisibleFrom(ResourceScope target) => Type switch
    {
        ResourceScopeType.Instance => true,
        ResourceScopeType.Tenant => (target.Type is ResourceScopeType.Tenant or ResourceScopeType.Workspace) && target.TenantId == TenantId,
        ResourceScopeType.Workspace => target.Type == ResourceScopeType.Workspace && target.TenantId == TenantId && target.WorkspaceId == WorkspaceId,
        _ => false
    };

    public override string ToString() => Key;
}

[JsonConverter(typeof(ResourceNamespaceJsonConverter))]
public readonly struct ResourceNamespace : IEquatable<ResourceNamespace>
{
    public const string DefaultValue = "default";
    private readonly string? value;

    public ResourceNamespace(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 128
            || !char.IsLetterOrDigit(normalized[0])
            || normalized.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-'))
            throw new ArgumentException("Namespaces must contain 1 to 128 lowercase letters, digits, '.' or '-' and start with a letter or digit.", nameof(value));
        this.value = normalized;
    }

    public static ResourceNamespace Default => new(DefaultValue);
    public string Value => value ?? DefaultValue;
    public bool IsDefault => string.Equals(Value, DefaultValue, StringComparison.Ordinal);
    public static ResourceNamespace Parse(string? value) => string.IsNullOrWhiteSpace(value) ? Default : new(value);
    public bool Equals(ResourceNamespace other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is ResourceNamespace other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;
    public static bool operator ==(ResourceNamespace left, ResourceNamespace right) => left.Equals(right);
    public static bool operator !=(ResourceNamespace left, ResourceNamespace right) => !left.Equals(right);
}

public readonly record struct ResourceAddress(ResourceNamespace Namespace, string Kind, string Name)
{
    public static ResourceAddress Create(ResourceNamespace @namespace, string kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(@namespace, kind, name);
    }

    public override string ToString() => $"{Namespace}/{Kind}/{Name}";
}

public readonly record struct ScopedResourceAddress(ResourceScope Scope, ResourceNamespace Namespace, string Kind, string Name)
{
    public static ScopedResourceAddress Create(ResourceScope scope, ResourceNamespace @namespace, string kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(scope, @namespace, kind, name);
    }

    public ResourceAddress Address => ResourceAddress.Create(Namespace, Kind, Name);
    public override string ToString() => $"{Scope}/{Address}";
}

public sealed class ResourceNamespaceJsonConverter : JsonConverter<ResourceNamespace>
{
    public override ResourceNamespace Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ResourceNamespace.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, ResourceNamespace value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
