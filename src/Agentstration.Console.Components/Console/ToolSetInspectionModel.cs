using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Web.Console;

/// <summary>Read-only identity projection; this screen does not expose a definition-write command.</summary>
public sealed record ToolSetInspectionModel
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = ResourceNamespace.DefaultValue;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Tags { get; init; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; init; }
    public static ToolSetInspectionModel From(ToolSetResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"))
    };
}
