using System.ComponentModel.DataAnnotations;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class SourceProviderEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required] public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    [Required] public string DisplayName { get; set; } = string.Empty;
    [Required] public string ExtensionId { get; set; } = string.Empty;
    [Required] public string ContributionId { get; set; } = string.Empty;

    public CreateSourceProviderRequest ToCreateRequest(ResourceScopeRef? scopeRef) =>
        new(Name.Trim(), ToProperties(), ResourceNamespace.Parse(Namespace).Value, scopeRef);
    public PutSourceProviderRequest ToPutRequest() => new(ToProperties());

    public SourceProviderProperties ToProperties() => new()
    {
        DisplayName = DisplayName.Trim(),
        Extension = ParseExtension(ExtensionId),
        ContributionId = ContributionId.Trim()
    };

    public static SourceProviderEditorModel FromResource(SourceProviderResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        DisplayName = resource.Definition.DisplayName,
        ExtensionId = ExtensionKey(
            resource.Definition.Extension.ScopeRef
                ?? throw new ArgumentException("The Source Provider extension reference has no ownership scope."),
            resource.Definition.Extension.Namespace ?? resource.Namespace,
            resource.Definition.Extension.Name),
        ContributionId = resource.Definition.ContributionId
    };

    private static ResourceReference ParseExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An extension registration is required.");
        var parts = value.Split('\n', 3);
        if (parts.Length != 3) throw new ArgumentException("Extension selection is invalid.");
        return new ResourceReference(parts[2], ResourceScopeRef.Parse(parts[0]), ResourceNamespace.Parse(parts[1]));
    }

    public static string ExtensionKey(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name) =>
        $"{scopeRef.Value}\n{@namespace.Value}\n{name}";
}
