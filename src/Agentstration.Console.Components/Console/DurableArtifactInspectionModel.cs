using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

/// <summary>Public, immutable Artifact inspection fields; storage locators and content are deliberately absent.</summary>
public sealed record DurableArtifactInspectionModel
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = ResourceNamespace.DefaultValue;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Tags { get; init; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; init; }
    public string ArtifactId { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public long Length { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public static string DisplayNameFor(FlowRunArtifactResource resource) => resource.Receipt.Provenance.TryGetValue("fileName", out var fileName)
        && !string.IsNullOrWhiteSpace(fileName) ? fileName : resource.ArtifactId.ToString();
    public static DurableArtifactInspectionModel From(FlowRunArtifactResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        DisplayName = DisplayNameFor(resource),
        ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")),
        ArtifactId = resource.ArtifactId.ToString(),
        MediaType = resource.Receipt.MediaType,
        Length = resource.Receipt.Length,
        Sha256 = resource.Receipt.Sha256,
        CreatedAt = resource.CreatedAt
    };
}
