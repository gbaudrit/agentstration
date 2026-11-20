using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Console;

/// <summary>Presentation draft. Selection changes originate exclusively in explicit user controls.</summary>
public sealed class StorageEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public string Namespace { get; set; } = ResourceNamespace.Default.Value;
    public ResourceScopeRef? ScopeRef { get; set; }
    public string Tags { get; set; } = string.Empty;
    public bool ValidName => Name.Length is >= 1 and <= 128 && char.IsAsciiLetterOrDigit(Name[0]) && Name.All(value => char.IsAsciiLetterLower(value) || char.IsAsciiDigit(value) || value is '.' or '-');
    public IReadOnlyList<string> InvalidIdentityFields()
    {
        var fields = new List<string>();
        if (string.IsNullOrWhiteSpace(DisplayName)) fields.Add(nameof(DisplayName));
        if (!ValidName) fields.Add(nameof(Name));
        return fields;
    }
    public StorageTypeReference? Type { get; private set; }
    public ConnectedServiceRevisionReference? ConnectedService { get; private set; }
    public ConnectedServiceTypeReference? ConnectedServiceType { get; private set; }
    public List<StorageExportBinding> Bindings { get; private set; } = [];
    public JsonElement Configuration { get; set; } = JsonSerializer.SerializeToElement(new { });
    public JsonElement Envelope { get; set; } = JsonSerializer.SerializeToElement(new { });
    public List<StorageDocumentCollection> Collections { get; set; } = [];
    public List<DescendantUseGrant> UseGrants { get; set; } = [];

    public bool HasBindingChange(StorageResource? original) => original is null || Type is not null && StorageDigests.Binding(original.Definition) != StorageDigests.Binding(Properties());
    public bool HasValidBinding(StorageTypeChoice? choice, ConnectedServiceResource? service, IReadOnlyList<ConnectedServiceExportDescriptor> exports) => choice is not null && Available(choice)
        && (!choice.Version.RequiresConnectedService || service is not null && ServiceAvailable(service))
        && choice.Version.Requirements.Where(value => value.Required).All(requirement => Bindings.Any(binding => binding.RequirementId == requirement.Id && exports.Contains(binding.Export) && Compatible(requirement, binding.Export)));
    public static bool Compatible(StorageRequirementDescriptor requirement, ConnectedServiceExportDescriptor export) => requirement.Type == export.Type && requirement.Protection == export.Protection && (requirement.Format is null || requirement.Format == export.Format);
    public static bool ServiceAvailable(ConnectedServiceResource service) => service.Definition.Enabled && service.ObservedState?.Availability == ConnectedServiceAvailability.Ready;
    public static StorageAvailability TypeAvailability(StorageTypeChoice choice) => choice.Resource.ObservedVersions.FirstOrDefault(value => value.Version == choice.Version.Version)?.Availability ?? StorageAvailability.Unavailable;
    public static bool Available(StorageTypeChoice choice) => choice.Resource.Definition.Enabled && choice.Resource.ObservedUse?.Use.Allowed == true && TypeAvailability(choice) == StorageAvailability.Ready;

    public void SelectType(StorageTypeChoice? choice)
    {
        Type = choice?.Reference;
        Configuration = JsonSerializer.SerializeToElement(new { });
        Envelope = JsonSerializer.SerializeToElement(new { });
        Collections.Clear();
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
        DisplayName = DisplayName.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Enabled = Enabled,
        Type = Type ?? throw new InvalidOperationException("Select a Storage type."),
        ConnectedService = ConnectedService,
        ConnectedServiceType = ConnectedServiceType,
        Bindings = Bindings.ToArray(),
        Configuration = Configuration.Clone(),
        Envelope = Envelope.Clone(),
        Collections = Collections.ToArray(),
        UsePolicy = new() { Grants = UseGrants.ToArray() }
    };

    public static StorageEditorModel From(StorageResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")),
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        Enabled = resource.Definition.Enabled,
        Type = resource.Definition.Type,
        ConnectedService = resource.Definition.ConnectedService,
        ConnectedServiceType = resource.Definition.ConnectedServiceType,
        Bindings = resource.Definition.Bindings.ToList(),
        Configuration = resource.Definition.Configuration.Clone(),
        Collections = resource.Definition.Collections.ToList(),
        Envelope = resource.Definition.Envelope.Clone(),
        UseGrants = resource.Definition.UsePolicy.Grants.ToList()
    };
}

public sealed record StorageTypeChoice(StorageTypeResource Resource, StorageTypeVersionDescriptor Version)
{
    public StorageTypeReference Reference => new()
    {
        Type = new(Resource.Name, Resource.ScopeRef!.Value, Resource.Namespace),
        TypeUid = Resource.Uid,
        Version = Version.Version,
        SchemaDigest = Version.SchemaDigest,
        ContractDigest = Version.ContractDigest
    };
    public string Key => JsonSerializer.Serialize(Reference);
    public bool Matches(StorageTypeReference? reference) => reference is not null && Key == JsonSerializer.Serialize(reference);
}

public static class StorageLinks
{
    public static string FromApi(string url) => url.StartsWith("/api/storages/", StringComparison.Ordinal) ? url[4..] : url;

    // Usage URLs are already authorized by their family provider. Only expose metadata
    // explicitly present in that local destination; absent values remain unknown.
    public static (string? Namespace, ResourceScopeRef? ScopeRef) UsageIdentity(StorageUsage usage)
    {
        var url = usage.Url;
        if (!url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal)
            || url.Contains('\\') || url.Any(char.IsControl)) return (null, null);
        var question = url.IndexOf('?');
        if (question < 0) return (null, null);
        var fragment = url.IndexOf('#');
        if (fragment >= 0 && fragment < question) return (null, null);
        var query = QueryHelpers.ParseQuery(fragment < 0 ? url[question..] : url[question..fragment]);
        string? resourceNamespace = null;
        if (query.TryGetValue("namespace", out var values) && values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]))
        {
            try { resourceNamespace = ResourceNamespace.Parse(values[0]).Value; }
            catch (ArgumentException) { }
        }
        ResourceScopeRef? scope = query.TryGetValue("scopeRef", out var scopes) && scopes.Count == 1
            && ResourceScopeRef.TryParse(scopes[0], out var parsed) ? parsed : null;
        return (resourceNamespace, scope);
    }

    public static ScopedResourceAddress Address(StorageResource resource) =>
        ScopedResourceAddress.Create(resource.ScopeRef!.Value, resource.Namespace, StorageResourceKinds.Storage, resource.Name);

    public static string Details(StorageResource resource) => Details(Address(resource));
    public static string Details(ScopedResourceAddress address) =>
        $"/storages/{Uri.EscapeDataString(address.Name)}?scopeRef={Uri.EscapeDataString(address.ScopeRef.Value)}&namespace={Uri.EscapeDataString(address.Namespace.Value)}";
}
