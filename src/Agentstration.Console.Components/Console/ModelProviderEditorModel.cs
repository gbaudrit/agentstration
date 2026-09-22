using System.ComponentModel.DataAnnotations;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Parameters;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Web.Console;

public sealed class ModelProviderEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required] public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    [Required] public string DisplayName { get; set; } = string.Empty;
    [Required] public string ExtensionId { get; set; } = string.Empty;
    [Required] public string ContributionId { get; set; } = string.Empty;
    public List<ModelProviderValueBindingEditorModel> ValueBindings { get; set; } = [];

    public CreateModelProviderRequest ToCreateRequest() => new(Name.Trim(), ToProperties(), ResourceNamespace.Parse(Namespace).Value);
    public PutModelProviderRequest ToPutRequest() => new(ToProperties());

    public ModelProviderProperties ToProperties() => new()
    {
        DisplayName = DisplayName.Trim(),
        Extension = ParseExtension(ExtensionId),
        ContributionId = ContributionId.Trim(),
        ValueBindings = ValueBindings.Where(binding => binding.IsConfigured).Select(binding => binding.ToBinding()).ToArray()
    };

    public static ModelProviderEditorModel FromResource(ModelProviderResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        DisplayName = resource.Definition.DisplayName,
        ExtensionId = $"{(resource.Definition.Extension.Namespace ?? resource.Namespace).Value}:{resource.Definition.Extension.Name}",
        ContributionId = resource.Definition.ContributionId,
        ValueBindings = resource.Definition.ValueBindings.Select(ModelProviderValueBindingEditorModel.From).ToList()
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

public sealed class ModelProviderValueBindingEditorModel
{
    public string RequirementId { get; set; } = string.Empty;
    public ModelProviderValueBindingKind Kind { get; set; } = ModelProviderValueBindingKind.Parameter;
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public string ScopeRef { get; set; } = string.Empty;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(ScopeRef);

    public ModelProviderValueBinding ToBinding()
    {
        var scope = ResourceScopeRef.Parse(ScopeRef);
        var @namespace = ResourceNamespace.Parse(Namespace);
        return Kind == ModelProviderValueBindingKind.Parameter
            ? ModelProviderValueBinding.FromParameter(RequirementId, new(
                ResourceAddress.Create(@namespace, ParameterResourceKinds.Parameter, Name), scope))
            : ModelProviderValueBinding.FromSecret(RequirementId, new SecretReference(
                ResourceAddress.Create(@namespace, SecretResourceKinds.Secret, Name), scope));
    }

    public static ModelProviderValueBindingEditorModel From(ModelProviderValueBinding value)
    {
        var address = value.Kind == ModelProviderValueBindingKind.Parameter
            ? value.Parameter!.Address
            : value.Secret!.Address;
        var scope = value.Kind == ModelProviderValueBindingKind.Parameter
            ? value.Parameter!.ScopeRef
            : value.Secret!.ScopeRef;
        return new()
        {
            RequirementId = value.RequirementId,
            Kind = value.Kind,
            Name = address.Name,
            Namespace = address.Namespace.Value,
            ScopeRef = scope?.Value ?? string.Empty
        };
    }
}
