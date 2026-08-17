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

public sealed class ResourceNamespaceJsonConverter : JsonConverter<ResourceNamespace>
{
    public override ResourceNamespace Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ResourceNamespace.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, ResourceNamespace value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
