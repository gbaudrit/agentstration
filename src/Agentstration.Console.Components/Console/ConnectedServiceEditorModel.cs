using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Web.Console;

public sealed class ConnectedServiceEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public JsonElement Options { get; set; } = JsonSerializer.SerializeToElement(new { });
    public List<ConnectedServiceBindingEditorModel> Bindings { get; set; } = [];
    public List<DescendantUseGrant> UseGrants { get; set; } = [];

    public ConnectedServiceProperties Properties(ConnectedServiceTypeResource type,
        ConnectedServiceTypeVersionDescriptor version) => new()
    {
        DisplayName = DisplayName.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Enabled = Enabled,
        Type = new()
        {
            Type = new ResourceReference(type.Name,
                type.ScopeRef ?? throw new InvalidOperationException("The Connected Service type has no exact scope."),
                type.Namespace),
            TypeUid = type.Uid,
            Version = version.Version,
            SchemaDigest = version.SchemaDigest
        },
        Options = Options.Clone(),
        Bindings = Bindings.Where(value => value.Reference is not null).Select(value => value.ToBinding()).ToArray(),
        UsePolicy = new() { Grants = UseGrants.ToArray() }
    };

    public static ConnectedServiceEditorModel From(ConnectedServiceResource resource) => new()
    {
        Name = resource.Name,
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        Enabled = resource.Definition.Enabled,
        Options = resource.Definition.Options.Clone(),
        Bindings = resource.Definition.Bindings.Select(ConnectedServiceBindingEditorModel.From).ToList(),
        UseGrants = resource.Definition.UsePolicy.Grants.ToList()
    };
}

public sealed class ConnectedServiceBindingEditorModel
{
    public required string InputId { get; init; }
    public required ConnectedServiceBindingKind Kind { get; set; }
    public string? Reference { get; set; }

    public ConnectedServiceValueBinding ToBinding()
    {
        var parts = (Reference ?? throw new InvalidOperationException("A binding reference is required.")).Split('|', 3);
        if (parts.Length != 3) throw new InvalidOperationException("The binding reference is invalid.");
        var scope = ResourceScopeRef.Parse(parts[0]);
        var address = ResourceAddress.Create(ResourceNamespace.Parse(parts[1]),
            Kind == ConnectedServiceBindingKind.Parameter ? ParameterResourceKinds.Parameter : SecretResourceKinds.Secret,
            parts[2]);
        return Kind == ConnectedServiceBindingKind.Parameter
            ? new() { InputId = InputId, Kind = Kind, Parameter = new ParameterReference(address, scope) }
            : new() { InputId = InputId, Kind = Kind, Secret = new SecretReference(address, scope) };
    }

    public static ConnectedServiceBindingEditorModel From(ConnectedServiceValueBinding binding)
    {
        var address = binding.Parameter?.Address ?? binding.Secret?.Address
            ?? throw new InvalidOperationException("The Connected Service binding has no resource reference.");
        var scope = binding.Parameter?.ScopeRef ?? binding.Secret?.ScopeRef
            ?? throw new InvalidOperationException("The Connected Service binding has no exact scope.");
        return new()
        {
            InputId = binding.InputId,
            Kind = binding.Kind,
            Reference = $"{scope.Value}|{address.Namespace.Value}|{address.Name}"
        };
    }
}
