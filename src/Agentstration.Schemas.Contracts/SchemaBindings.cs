using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Schemas.Contracts;

/// <summary>An inline Schema definition carried by the consuming resource.</summary>
public sealed record InlineSchemaBinding
{
    public required string Format { get; init; }
    public string? Dialect { get; init; }
    public required JsonElement Content { get; init; }
}

/// <summary>An exact reference to an immutable published Schema version.</summary>
public sealed record SchemaVersionReference
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public ResourceNamespace? Namespace { get; init; }
    public ResourceScopeRef? ScopeRef { get; init; }
}

/// <summary>
/// Selects either an inline definition or an immutable published Schema version. The two forms are
/// deliberately exclusive; Schema composition is outside this contract.
/// </summary>
public sealed record SchemaBinding
{
    public InlineSchemaBinding? Inline { get; init; }
    public SchemaVersionReference? Reference { get; init; }
}

public sealed record SchemaBindingResolutionContext(
    ResourceScopeRef ConsumerScopeRef,
    ResourceNamespace OwnerNamespace,
    string RequiredFormat);

public sealed record SchemaBindingSnapshot(
    string Path,
    string Format,
    string? Dialect,
    JsonElement Content,
    string DefinitionHash,
    SchemaVersionReference? Source = null,
    Guid? SchemaUid = null,
    long? SchemaGeneration = null);

public interface ISchemaBindingResolver
{
    ValueTask<SchemaBindingSnapshot> ResolveAsync(
        string path,
        SchemaBinding binding,
        SchemaBindingResolutionContext context,
        CancellationToken cancellationToken);
}

public sealed class SchemaBindingResolutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
