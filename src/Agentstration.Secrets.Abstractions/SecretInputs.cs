using System.Text.Json;

namespace Agentstration.Secrets.Abstractions;

/// <summary>A trusted, durable binding. Values are resolved only at the execution boundary.</summary>
public sealed record SecretInputReference(string Id, SecretReference Secret, Guid Uid, SecretResolutionContext Consumer);

public static class SecretInputs
{
    private const string MarkerProperty = "$agentstration.secretInput";

    public static JsonElement Marker(string id) => JsonSerializer.SerializeToElement(new Dictionary<string, string> { [MarkerProperty] = id });

    public static bool TryGetId(JsonElement value, out string? id)
    {
        id = null;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1
            || !value.TryGetProperty(MarkerProperty, out var marker) || marker.ValueKind != JsonValueKind.String) return false;
        id = marker.GetString();
        return true;
    }

    public static bool IsReference(JsonElement value, IReadOnlyList<SecretInputReference> references) =>
        TryGetId(value, out var id) && references.Any(reference => reference.Id == id);

    public static bool ContainsReference(JsonElement value, IReadOnlyList<SecretInputReference> references)
    {
        if (references.Count == 0) return false;
        if (IsReference(value, references)) return true;
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsReference(property.Value, references)),
            JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsReference(item, references)),
            _ => false
        };
    }

    public static JsonElement ForTypeValidation(JsonElement value, IReadOnlyList<SecretInputReference> references) =>
        references.Count == 0 ? value.Clone() : Rewrite(value, item => IsReference(item, references) ? JsonSerializer.SerializeToElement(string.Empty) : null);

    public static JsonElement PreserveReferences(JsonElement value, JsonElement symbolic, IReadOnlyList<SecretInputReference> references)
    {
        if (IsReference(symbolic, references)) return symbolic.Clone();
        if (value.ValueKind == JsonValueKind.Object && symbolic.ValueKind == JsonValueKind.Object)
            return JsonSerializer.SerializeToElement(value.EnumerateObject().ToDictionary(property => property.Name,
                property => symbolic.TryGetProperty(property.Name, out var original)
                    ? PreserveReferences(property.Value, original, references) : property.Value.Clone(), StringComparer.Ordinal));
        if (value.ValueKind == JsonValueKind.Array && symbolic.ValueKind == JsonValueKind.Array)
            return JsonSerializer.SerializeToElement(value.EnumerateArray().Select((item, index) => index < symbolic.GetArrayLength()
                ? PreserveReferences(item, symbolic[index], references) : item.Clone()).ToArray());
        return value.Clone();
    }

    public static JsonElement Rewrite(JsonElement value, Func<JsonElement, JsonElement?> replace)
    {
        if (replace(value) is { } replacement) return replacement.Clone();
        return value.ValueKind switch
        {
            JsonValueKind.Object => JsonSerializer.SerializeToElement(value.EnumerateObject().ToDictionary(
                property => property.Name, property => Rewrite(property.Value, replace), StringComparer.Ordinal)),
            JsonValueKind.Array => JsonSerializer.SerializeToElement(value.EnumerateArray().Select(item => Rewrite(item, replace)).ToArray()),
            _ => value.Clone()
        };
    }
}

public interface ISecretInputResolver
{
    Task<ResolvedSecretInputs> ResolveAsync(JsonElement input, IReadOnlyList<SecretInputReference> references,
        CancellationToken cancellationToken);
}

/// <summary>Transient materialization; only the symbolic output may cross a durable boundary.</summary>
public sealed class ResolvedSecretInputs(JsonElement value, IReadOnlyDictionary<string, string> values) : IDisposable
{
    [System.Text.Json.Serialization.JsonIgnore]
    public JsonElement Value { get; } = value;
    public override string ToString() => "[REDACTED]";
    public bool IsSensitive => values.Count != 0;

    public JsonElement? Protect(JsonElement? output) => output is null ? null : !IsSensitive ? output.Value.Clone() : SecretInputs.Rewrite(output.Value, item =>
    {
        if (item.ValueKind != JsonValueKind.String) return null;
        var text = item.GetString()!;
        foreach (var entry in values)
            if (text == entry.Value) return SecretInputs.Marker(entry.Key);
        foreach (var entry in values.OrderByDescending(entry => entry.Value.Length))
            if (entry.Value.Length != 0) text = text.Replace(entry.Value, "[REDACTED]", StringComparison.Ordinal);
        return JsonSerializer.SerializeToElement(text);
    });

    public void Dispose()
    {
        // JsonElement/string values cannot be zeroed; release the attempt's lookup promptly.
        if (values is Dictionary<string, string> dictionary) dictionary.Clear();
    }
}
