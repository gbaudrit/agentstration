using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

/// <summary>Read-only saved Agent and original Pack-source inspection. This route never edits a definition.</summary>
public sealed record ManagedAgentInspectionModel
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = ResourceNamespace.DefaultValue;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Tags { get; init; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; init; }
    public string Configuration { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string ModelProfile { get; init; } = string.Empty;
    public string RuntimeProfile { get; init; } = string.Empty;
    public static ManagedAgentInspectionModel From(AgentResource resource, string source) => new()
    {
        Name = resource.Name, Namespace = resource.Namespace.Value, DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description, ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")),
        Configuration = JsonSerializer.Serialize(resource.Definition), Source = source,
        ModelProfile = JsonSerializer.Serialize(resource.Definition.ModelProfile), RuntimeProfile = JsonSerializer.Serialize(resource.Definition.RuntimeProfile)
    };
}
