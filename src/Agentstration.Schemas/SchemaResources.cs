using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Schemas;

public static class SchemaResourceKinds
{
    public const string Schema = "Schema";
    public const string SchemaVersion = "SchemaVersion";
}

public static class SchemaFormatIds
{
    public const string JsonSchema = "json-schema";
}

public sealed record SchemaProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Format { get; init; }
    public string? Dialect { get; init; }
    public required JsonElement Content { get; init; }
    public DescendantUsePolicy UsePolicy { get; init; } = new();
}

public sealed record SchemaResource : Resource
{
    public SchemaProperties Definition { get; init; } = null!;
}

public sealed record SchemaVersionResource : Resource, IImmutableResource
{
    public required Guid SchemaUid { get; init; }
    public required string SchemaName { get; init; }
    public required ResourceNamespace SchemaNamespace { get; init; }
    public required long SchemaGeneration { get; init; }
    public required string Version { get; init; }
    public required string Format { get; init; }
    public string? Dialect { get; init; }
    public required JsonElement Content { get; init; }
    public required string DefinitionHash { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

public sealed class SchemaValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class SchemaNotFoundException(ResourceNamespace @namespace, string name)
    : Exception($"Schema '{@namespace}/{name}' was not found.");
