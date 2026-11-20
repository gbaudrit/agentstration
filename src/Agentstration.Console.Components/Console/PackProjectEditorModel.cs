using System.ComponentModel.DataAnnotations;
using Agentstration.Packs.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class PackProjectEditorModel
{
    public string ResourceName { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public ResourceScopeRef? ScopeRef { get; set; }
    public string MetadataTags { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string PackName { get; set; } = string.Empty;
    [Required] public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public PackAudience Audience { get; set; } = PackAudience.Universal;
    public PackPurpose Purpose { get; set; } = PackPurpose.Standard;
    public string Categories { get; set; } = string.Empty;
    public string ManifestTags { get; set; } = string.Empty;

    public static PackProjectEditorModel From(PackProjectResource resource) => new()
    {
        ResourceName = resource.Name, Namespace = resource.Namespace.Value, ScopeRef = resource.ScopeRef,
        MetadataTags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => $"{value.Key}={value.Value}")),
        Publisher = resource.Definition.Publisher, PackName = resource.Definition.PackName, Version = resource.Definition.Version,
        DisplayName = resource.Definition.DisplayName ?? string.Empty, Description = resource.Definition.Description,
        Audience = resource.Definition.Audience, Purpose = resource.Definition.Purpose,
        Categories = string.Join(", ", resource.Definition.Categories), ManifestTags = string.Join(", ", resource.Definition.Tags)
    };

    public UpdatePackProjectCommand Command() => new(Version.Trim(), Empty(DisplayName), Empty(Description), Split(Categories), Split(ManifestTags), Audience, Purpose);
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IReadOnlyList<string> Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
