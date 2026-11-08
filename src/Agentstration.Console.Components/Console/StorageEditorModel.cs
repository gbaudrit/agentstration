using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Web.Console;

/// <summary>Presentation draft. Selection changes originate exclusively in explicit user controls.</summary>
public sealed class StorageEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public StorageTypeReference? Type { get; private set; }
    public ConnectedServiceRevisionReference? ConnectedService { get; private set; }
    public ConnectedServiceTypeReference? ConnectedServiceType { get; private set; }
    public List<StorageExportBinding> Bindings { get; private set; } = [];
    public JsonElement Configuration { get; set; } = JsonSerializer.SerializeToElement(new { });
    public JsonElement Envelope { get; set; } = JsonSerializer.SerializeToElement(new { });
    public List<DescendantUseGrant> UseGrants { get; set; } = [];

    public void SelectType(StorageTypeChoice choice)
    {
        Type = choice.Reference;
        Configuration = JsonSerializer.SerializeToElement(new { });
        Envelope = JsonSerializer.SerializeToElement(new { });
        SelectService(null);
    }

    public void SelectService(ConnectedServiceResource? service)
    {
        ConnectedService = service is null ? null : new()
        {
            Service = new(ResourceAddress.Create(service.Namespace, service.Kind, service.Name), service.ScopeRef!.Value),
            ServiceUid = service.Uid,
            Generation = service.Generation
        };
        ConnectedServiceType = service?.Definition.Type;
        Bindings.Clear();
    }

    public void Bind(string requirementId, ConnectedServiceExportDescriptor? export)
    {
        Bindings.RemoveAll(binding => binding.RequirementId == requirementId);
        if (export is not null) Bindings.Add(new(requirementId, export));
    }

    public StorageProperties Properties() => new()
    {
        DisplayName = DisplayName.Trim(), Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Enabled = Enabled, Type = Type ?? throw new InvalidOperationException("Select a Storage type."),
        ConnectedService = ConnectedService, ConnectedServiceType = ConnectedServiceType,
        Bindings = Bindings.ToArray(), Configuration = Configuration.Clone(), Envelope = Envelope.Clone(),
        UsePolicy = new() { Grants = UseGrants.ToArray() }
    };

    public static StorageEditorModel From(StorageResource resource) => new()
    {
        Name = resource.Name, DisplayName = resource.Definition.DisplayName, Description = resource.Definition.Description,
        Enabled = resource.Definition.Enabled, Type = resource.Definition.Type,
        ConnectedService = resource.Definition.ConnectedService, ConnectedServiceType = resource.Definition.ConnectedServiceType,
        Bindings = resource.Definition.Bindings.ToList(), Configuration = resource.Definition.Configuration.Clone(),
        Envelope = resource.Definition.Envelope.Clone(), UseGrants = resource.Definition.UsePolicy.Grants.ToList()
    };
}

public sealed record StorageTypeChoice(StorageTypeResource Resource, StorageTypeVersionDescriptor Version)
{
    public StorageTypeReference Reference => new()
    {
        Type = new(Resource.Name, Resource.ScopeRef!.Value, Resource.Namespace), TypeUid = Resource.Uid,
        Version = Version.Version, SchemaDigest = Version.SchemaDigest, ContractDigest = Version.ContractDigest
    };
    public string Key => JsonSerializer.Serialize(Reference);
    public bool Matches(StorageTypeReference? reference) => reference is not null && Key == JsonSerializer.Serialize(reference);
}

public static class StorageLinks
{
    public static string FromApi(string url) => url.StartsWith("/api/storages/", StringComparison.Ordinal) ? url[4..] : url;

    public static ScopedResourceAddress Address(StorageResource resource) =>
        ScopedResourceAddress.Create(resource.ScopeRef!.Value, resource.Namespace, StorageResourceKinds.Storage, resource.Name);

    public static string Details(StorageResource resource) => Details(Address(resource));
    public static string Details(ScopedResourceAddress address) =>
        $"/storages/{Uri.EscapeDataString(address.Name)}?scopeRef={Uri.EscapeDataString(address.ScopeRef.Value)}&namespace={Uri.EscapeDataString(address.Namespace.Value)}";
}
