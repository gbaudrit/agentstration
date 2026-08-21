using System.ComponentModel.DataAnnotations;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class ModelProviderEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required] public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    [Required] public string DisplayName { get; set; } = string.Empty;
    [Required] public string ExtensionId { get; set; } = string.Empty;
    [Required] public string ContributionId { get; set; } = string.Empty;

    public CreateModelProviderRequest ToCreateRequest() => new(Name.Trim(), ToProperties(), ResourceNamespace.Parse(Namespace).Value);
    public PutModelProviderRequest ToPutRequest() => new(ToProperties());

    public ModelProviderProperties ToProperties() => new()
    {
        DisplayName = DisplayName.Trim(),
        Extension = ParseExtension(ExtensionId),
        ContributionId = ContributionId.Trim()
    };

    public static ModelProviderEditorModel FromResource(ModelProviderResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        DisplayName = resource.Definition.DisplayName,
        ExtensionId = $"{(resource.Definition.Extension.Namespace ?? resource.Namespace).Value}:{resource.Definition.Extension.Name}",
        ContributionId = resource.Definition.ContributionId
    };

    private static ResourceReference ParseExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An extension registration is required.");
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) throw new ArgumentException("Extension selection is invalid.");
        var @namespace = ResourceNamespace.Parse(value[..separator]);
        return new ResourceReference(value[(separator + 1)..], @namespace: @namespace.IsDefault ? null : @namespace);
    }
}
