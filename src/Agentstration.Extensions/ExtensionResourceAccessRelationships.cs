using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Extensions;

public sealed class ExtensionResourceAccessRelationships : IResourceAccessRelationshipProvider
{
    public ResourceAccessRelationships Describe(ResourceAccessDocument resource,
        IReadOnlyDictionary<Guid, ResourceAccessDocument> documents)
    {
        if (resource.Identity.Kind == ExtensionKinds.ExtensionRegistration)
            return new() { Collection = true, Members = documents.Values.Where(value =>
                value.Governance.Origin.Kind == ResourceOriginKind.Extension
                && value.Governance.ManagedBy?.Kind == ResourceManagementAuthorityKind.ExtensionInstallation
                && value.Governance.Origin.ResourceUid == resource.Identity.Uid
                && value.Identity.ScopeRef == resource.Identity.ScopeRef).Select(value => value.Identity.Uid).ToArray() };
        if (resource.Governance.Origin.Kind != ResourceOriginKind.Extension) return new();
        if (resource.Governance.ManagedBy?.Kind != ResourceManagementAuthorityKind.ExtensionInstallation
            || resource.Governance.Origin.ResourceUid is not { } uid
            || !documents.TryGetValue(uid, out var registration)
            || registration.Identity.Kind != ExtensionKinds.ExtensionRegistration
            || registration.Identity.ScopeRef != resource.Identity.ScopeRef)
            return new() { Valid = false };
        var disabled = registration.Payload.TryGetProperty("definition", out var definition)
            && definition.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == System.Text.Json.JsonValueKind.False;
        return new() { Prerequisites = [uid], Available = disabled ? false : null };
    }
}
