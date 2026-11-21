using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components.Pages;

public partial class Extensions
{
    private ResourceCollectionPreview? preview;
    private bool focusEnrollmentAfterPreview;
    private bool CanPreview => catalogContext is { Error: null, Current: not null }
        && catalogContext.HasPermission(AuthorizationPermissions.ResourcesRead) && catalogLoadState.Status != ResourceLoadStatus.Forbidden;
    private bool CanEditItem(ExtensionInventoryItemResponse item) => CanAdministerEnrollments
        && registrations?.Any(value => value.Name == item.RegistrationName && value.Namespace.Value == item.RegistrationNamespace
            && value.ScopeRef == item.RegistrationScopeRef && value.Definition.Source == ExtensionRegistrationSource.Manual
            && value.ScopeRef?.Value == catalogContext?.Current?.SelectedScopeRef && !value.Governance.IsManaged && ResourceOwnershipContext.CanManage(value.ScopeRef, Services)) == true;
    private void OpenPreview(ExtensionInventoryItemResponse item)
    {
        if (!CanPreview || loading || pairingBusy || item.RegistrationName is not { } name || item.RegistrationScopeRef is not { } owner) return;
        preview?.Open(ScopedResourceAddress.Create(owner, ResourceNamespace.Parse(item.RegistrationNamespace), ExtensionKinds.ExtensionRegistration, name));
    }
    private string PreviewUrl(ScopedResourceAddress address, string section) => ResourceOwnershipContext.PreserveNavigation(
        $"/extensions/{Uri.EscapeDataString(address.Name)}?namespace={Uri.EscapeDataString(address.Namespace.Value)}&scopeRef={Uri.EscapeDataString(address.ScopeRef.Value)}", Services) + "#" + section;
    private ExtensionInventoryItemResponse? PreviewEnrollment(ScopedResourceAddress address) => inventory?.SingleOrDefault(item =>
        item.RegistrationName == address.Name && item.RegistrationNamespace == address.Namespace.Value && item.RegistrationScopeRef == address.ScopeRef
        && EnrollmentFor(item) is not null);
    private void OpenPreviewEnrollment(ScopedResourceAddress address)
    {
        if (!CanPreview || loading || pairingBusy || PreviewEnrollment(address) is not { } item) return;
        expandedEnrollmentId = item.EnrollmentInstanceId;
        focusEnrollmentAfterPreview = true;
        preview?.Close();
    }
    private async Task PreviewClosed()
    {
        if (!focusEnrollmentAfterPreview) return;
        focusEnrollmentAfterPreview = false;
        if (!disposed && CanPreview && expandedEnrollmentId is not null) await enrollmentPanel.FocusAsync();
    }
}
