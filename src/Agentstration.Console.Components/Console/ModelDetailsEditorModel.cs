using Agentstration.Models;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

/// <summary>Saved immutable Model identity plus the independently persisted Provider override command.</summary>
public sealed class ModelDetailsEditorModel : ModelSpecificationOverrideEditorModel
{
    private ModelDetailsEditorModel(ModelResource resource, ModelSpecificationOverride? value) : base(value)
    {
        Name = resource.Name;
        Namespace = resource.Namespace.Value;
        DisplayName = resource.Definition.DisplayName;
        ScopeRef = resource.ScopeRef;
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        Provider = resource.Definition.Provider;
        ExternalId = resource.Definition.ExternalId;
        Publisher = resource.Definition.Identity?.Publisher;
        ModelFamily = resource.Definition.Identity?.Model;
        ModelVersion = resource.Definition.Identity?.Version;
        Generation = resource.Generation;
        ObservedSpecification = resource.Definition.Specification;
    }
    public string Name { get; }
    public string Namespace { get; }
    public string DisplayName { get; }
    public string? Description => null;
    public ResourceScopeRef? ScopeRef { get; }
    public string Tags { get; }
    public ResourceReference Provider { get; }
    public string ExternalId { get; }
    public string? Publisher { get; }
    public string? ModelFamily { get; }
    public string? ModelVersion { get; }
    public long Generation { get; }
    public ModelSpecification ObservedSpecification { get; }
    public static ModelDetailsEditorModel FromResource(ModelResource resource, ModelSpecificationOverride? value) => new(resource, value);
}
