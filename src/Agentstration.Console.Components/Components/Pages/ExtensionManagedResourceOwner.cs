using Agentstration.Extensions.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Components.Pages;

internal sealed record ExtensionManagedResourceOwner(
    ExtensionInventoryItemResponse Extension,
    ExtensionBundleInstallationResponse Installation)
{
    public string ExtensionUrl => $"/extensions/{Uri.EscapeDataString(Extension.RegistrationName!)}?namespace={Uri.EscapeDataString(Extension.RegistrationNamespace)}&scopeRef={Uri.EscapeDataString(Installation.ScopeRef.Value)}&context={Uri.EscapeDataString(Installation.ScopeRef.Value)}";

    public static ExtensionManagedResourceOwner? Find(
        IReadOnlyList<ExtensionInventoryItemResponse> inventory, ResourceGovernance governance,
        ResourceScopeRef scopeRef, ResourceNamespace @namespace, string kind, string name, Guid? uid)
    {
        if (governance.ManagedBy?.Kind != ResourceManagementAuthorityKind.ExtensionInstallation) return null;
        foreach (var extension in inventory)
        foreach (var installation in extension.Extension?.BundleInstallations ?? [])
        {
            var address = ScopedResourceAddress.Create(installation.ScopeRef,
                ResourceNamespace.Parse(extension.RegistrationNamespace), ExtensionKinds.ExtensionBundleInstallation, installation.Name);
            if (address.ToString() == governance.ManagedBy.Reference && installation.ManagedResources.Any(resource =>
                resource.ScopeRef == scopeRef && resource.Namespace == @namespace.Value && resource.Kind == kind
                && resource.Name == name && (uid is null || resource.Uid == uid)))
                return new(extension, installation);
        }
        return null;
    }
}
