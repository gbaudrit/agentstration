using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Tests;

internal static class ResourceScreenDeletionAssertions
{
    public static async Task ConfirmAndReleasePolicyDraftAsync<TComponent>(IRenderedComponent<TComponent> cut, NavigationManager navigation)
        where TComponent : ComponentBase
    {
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(c => c.DraftChanged).InvokeAsync(true));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        ResourceLoadStatus? statusAtDeparture = null;
        bool? guardAtDeparture = null, snapshotClearedAtDeparture = null;
        using var observer = navigation.RegisterLocationChangingHandler(change =>
        {
            if (new Uri(change.TargetLocation).AbsolutePath != new Uri(navigation.Uri).AbsolutePath)
            {
                var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
                statusAtDeparture = adapter.LoadState!.Status;
                snapshotClearedAtDeparture = adapter.Resource is null;
                guardAtDeparture = cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!();
            }
            return ValueTask.CompletedTask;
        });
        cut.Find(".resource-header button.button-danger").Click();
        await cut.Find("[role=alertdialog] .button-danger").ClickAsync(new());
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.AreEqual(ResourceLoadStatus.NotFound, statusAtDeparture);
        Assert.AreEqual(true, snapshotClearedAtDeparture);
        Assert.AreEqual(false, guardAtDeparture);
        Assert.IsEmpty(cut.FindAll(".resource-form, .unsaved-changes-bar"));
    }
}
