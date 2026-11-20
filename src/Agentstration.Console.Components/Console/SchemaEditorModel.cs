using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
namespace Agentstration.Web.Console;

public sealed class SchemaEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")] public string Name { get; set; } = "";
    [Required, StringLength(200)] public string DisplayName { get; set; } = "";
    public string Namespace { get; set; } = "default";
    [Required] public ResourceScopeRef? Scope { get; set; }
    public string Tags { get; set; } = "";
    [StringLength(2000)] public string? Description { get; set; }
    [Required] public string Format { get; set; } = SchemaFormatIds.JsonSchema;
    public string Dialect { get; set; } = "https://json-schema.org/draft/2020-12/schema";
    public JsonElement Content { get; set; } = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
    public bool NamespaceValid { get { try { _ = ResourceNamespace.Parse(Namespace); return true; } catch (ArgumentException) { return false; } } }
    public bool IdentityComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(DisplayName) && Scope is not null && NamespaceValid;
    public void Load(SchemaResource saved)
    {
        Name = saved.Name; Namespace = saved.Namespace.Value; Scope = saved.ScopeRef; DisplayName = saved.Definition.DisplayName; Description = saved.Definition.Description;
        Tags = string.Join(Environment.NewLine, saved.Metadata.Tags.Select(pair => $"{pair.Key}={pair.Value}"));
        Format = saved.Definition.Format; Dialect = saved.Definition.Dialect ?? ""; Content = saved.Definition.Content.Clone();
    }
    public SchemaProperties Properties(SchemaResource? saved) => new()
    {
        SourceEvidence = saved?.Definition.SourceEvidence,
        UsePolicy = saved?.Definition.UsePolicy ?? new(),
        DisplayName = DisplayName.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Format = saved?.Definition.Format ?? Format.Trim(),
        Dialect = saved is null ? (string.IsNullOrWhiteSpace(Dialect) ? null : Dialect.Trim()) : saved.Definition.Dialect,
        Content = Content.Clone()
    };
    public static string NextVersion(IReadOnlyList<SchemaVersionResource> values)
    {
        var latest = values.OrderByDescending(value => value.PublishedAt).FirstOrDefault()?.Version;
        return System.Version.TryParse(latest, out var version) && version.Build is >= 0 and < int.MaxValue && version.Revision < 0 ? $"{version.Major}.{version.Minor}.{version.Build + 1}" : "1.0.0";
    }
}
public sealed record SchemaManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public required ResourceMetadata Metadata { get; init; }
    public ResourceScopeRef? ScopeRef { get; init; }
    public required SchemaProperties Definition { get; init; }
    public string? IdentityError(SchemaResource saved) => ApiVersion != saved.ApiVersion || Kind != saved.Kind || Metadata.Name != saved.Name || Metadata.Namespace != saved.Namespace || ScopeRef != saved.ScopeRef
        || !Metadata.Tags.OrderBy(pair => pair.Key).SequenceEqual(saved.Metadata.Tags.OrderBy(pair => pair.Key))
        || Definition.Format != saved.Definition.Format || Definition.Dialect != saved.Definition.Dialect ? "IdentityImmutable" : null;
    public static SchemaManifest From(SchemaResource saved) => new() { ApiVersion = saved.ApiVersion, Kind = saved.Kind, Metadata = saved.Metadata, ScopeRef = saved.ScopeRef, Definition = saved.Definition };
}
