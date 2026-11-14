using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Functions.Contracts;

public static class FunctionResourceKinds
{
    public const string FunctionKind = "FunctionKind";
    public const string TransformationFunction = "TransformationFunction";
}

public sealed record FunctionKindVersion(string Version, string Executor, JsonElement DefinitionSchema);
public sealed record FunctionKindProperties(string DisplayName, IReadOnlyList<FunctionKindVersion> Versions, bool Enabled = true);
public sealed record FunctionKindResource : Resource
{
    public required FunctionKindProperties Definition { get; init; }
}

public sealed record FunctionReference(string Name, string Version, ResourceScopeRef? ScopeRef = null,
    ResourceNamespace? Namespace = null, Guid? Uid = null, string? Digest = null);
public sealed record TransformationFunctionVersion(string Version, FunctionReference FunctionKind, JsonElement Configuration)
{
    public string? NativeContractDigest { get; init; }
}
public sealed record TransformationFunctionProperties(string DisplayName, IReadOnlyList<TransformationFunctionVersion> Versions, bool Enabled = true);
public sealed record TransformationFunctionResource : Resource
{
    public required TransformationFunctionProperties Definition { get; init; }
}

public sealed record ResolvedTransformationFunction(FunctionReference Reference, FunctionReference FunctionKind,
    string ImplementationId, string ImplementationVersion, JsonElement InputSchema, JsonElement OutputSchema);

public interface ITransformationFunctionResolver
{
    Task<ResolvedTransformationFunction> ResolveAsync(FunctionReference reference, ResourceScopeRef consumer,
        CancellationToken cancellationToken);
}

public static class FunctionDigests
{
    public static string Compute<T>(T value)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Write(writer, JsonSerializer.SerializeToElement(value));
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

public static class NativeFunctionKindContract
{
    public static JsonElement DefinitionSchema { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"implementation":{"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":128},"version":{"type":"string","minLength":1,"maxLength":64}},"required":["id","version"],"additionalProperties":false}},"required":["implementation"],"additionalProperties":false}
        """);
}
