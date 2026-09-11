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

[JsonConverter(typeof(JsonStringEnumConverter<ResourceScopeKind>))]
public enum ResourceScopeKind
{
    [JsonStringEnumMemberName("instance")] Instance,
    [JsonStringEnumMemberName("tenant")] Tenant,
    [JsonStringEnumMemberName("workspace")] Workspace
}

/// <summary>Canonical local reference to the immutable owner of a Management resource.</summary>
[JsonConverter(typeof(ResourceScopeRefJsonConverter))]
public readonly struct ResourceScopeRef : IEquatable<ResourceScopeRef>
{
    private readonly string? value;

    private ResourceScopeRef(string value, ResourceScopeKind kind, Guid? targetId)
    {
        this.value = value;
        Kind = kind;
        TargetId = targetId;
    }

    public static ResourceScopeRef Instance { get; } = new("/instance", ResourceScopeKind.Instance, null);
    public ResourceScopeKind Kind { get; }
    public Guid? TargetId { get; }
    public string Value => value ?? throw new InvalidOperationException("The resource scope reference is not initialized.");
    public string TargetKey => TargetId?.ToString("D") ?? "instance";

    public static ResourceScopeRef Tenant(Guid tenantId) => Create(ResourceScopeKind.Tenant, "tenants", tenantId);
    public static ResourceScopeRef Workspace(Guid workspaceId) => Create(ResourceScopeKind.Workspace, "workspaces", workspaceId);

    public static ResourceScopeRef Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (string.Equals(value, Instance.Value, StringComparison.Ordinal)) return Instance;
        if (TryParseTarget(value, "/tenants/", ResourceScopeKind.Tenant, out var tenant)) return tenant;
        if (TryParseTarget(value, "/workspaces/", ResourceScopeKind.Workspace, out var workspace)) return workspace;
        throw new FormatException($"Resource scope reference '{value}' is not canonical.");
    }

    public static bool TryParse(string? value, out ResourceScopeRef scope)
    {
        try
        {
            scope = Parse(value!);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            scope = default;
            return false;
        }
    }

    public bool Equals(ResourceScopeRef other) => string.Equals(value, other.value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is ResourceScopeRef other && Equals(other);
    public override int GetHashCode() => value is null ? 0 : StringComparer.Ordinal.GetHashCode(value);
    public override string ToString() => Value;
    public static bool operator ==(ResourceScopeRef left, ResourceScopeRef right) => left.Equals(right);
    public static bool operator !=(ResourceScopeRef left, ResourceScopeRef right) => !left.Equals(right);

    private static ResourceScopeRef Create(ResourceScopeKind kind, string segment, Guid targetId)
    {
        if (targetId == Guid.Empty) throw new ArgumentException($"A {kind.ToString().ToLowerInvariant()} scope requires a non-empty target ID.", nameof(targetId));
        return new($"/{segment}/{targetId:D}", kind, targetId);
    }

    private static bool TryParseTarget(string value, string prefix, ResourceScopeKind kind, out ResourceScopeRef scope)
    {
        scope = default;
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var identifier = value[prefix.Length..];
        if (!Guid.TryParseExact(identifier, "D", out var targetId) || targetId == Guid.Empty) return false;
        var canonical = Create(kind, prefix[1..^1], targetId);
        if (!string.Equals(value, canonical.Value, StringComparison.Ordinal)) return false;
        scope = canonical;
        return true;
    }
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

public readonly record struct ScopedResourceAddress(ResourceScopeRef ScopeRef, ResourceNamespace Namespace, string Kind, string Name)
{
    public static ScopedResourceAddress Create(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(scopeRef, @namespace, kind, name);
    }

    public ResourceAddress Address => ResourceAddress.Create(Namespace, Kind, Name);
    public override string ToString() => $"{ScopeRef}/{Address}";
}

public sealed class ResourceNamespaceJsonConverter : JsonConverter<ResourceNamespace>
{
    public override ResourceNamespace Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ResourceNamespace.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, ResourceNamespace value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed class ResourceScopeRefJsonConverter : JsonConverter<ResourceScopeRef>
{
    public override ResourceScopeRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ResourceScopeRef.Parse(reader.GetString() ?? throw new JsonException("A resource scope reference must be a string."));

    public override void Write(Utf8JsonWriter writer, ResourceScopeRef value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
