using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Web.Console;

/// <summary>Saved catalog identity; only activation is mutable through this screen's commands.</summary>
public sealed record ToolInspectionModel
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = ResourceNamespace.DefaultValue;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Tags { get; init; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; init; }
    public static ToolInspectionModel From(ToolResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"))
    };
}
