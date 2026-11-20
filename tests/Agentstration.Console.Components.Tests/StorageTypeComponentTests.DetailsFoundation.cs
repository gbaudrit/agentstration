using System.Net;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class StorageTypeComponentTests
{
    [TestMethod]
    public async Task TypeDetailsShareTheFormStateAndRetainInvalidFieldsAndOriginalEtagUntilDiscard()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new TypesClient(); using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var cut = RenderDetails(context, client.Resource.Name);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        SelectSection(context, "definition/identity");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(adapter.SectionState, form.SectionState); Assert.AreSame(adapter.SectionPresentations, form.Sections);
        cut.Find("[data-testid=storage-type-display-name]").Change(""); cut.Find("form").Submit();
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        client.Resource = client.Resource with { Generation = 2, Definition = client.Resource.Definition with { DisplayName = "New saved type" } }; client.CurrentEtag = "new-etag";
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("", cut.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"original\"", cut.Find("[data-testid=storage-type-form]").GetAttribute("data-resource-etag"));
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any()); StringAssert.Contains(cut.Markup, "A newer saved version is available");
        SelectSection(context, "yaml"); SelectSection(context, "overview");
        StringAssert.Contains(cut.Find(".resource-header h1").TextContent, "Custom type");
        cut.Find(".unsaved-changes-bar button[type=button]").Click();
        SelectSection(context, "definition/identity");
        Assert.AreEqual("New saved type", cut.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        Assert.AreEqual("new-etag", cut.Find("[data-testid=storage-type-form]").GetAttribute("data-resource-etag"));
        Assert.IsNull(client.Updated);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task TypeRefreshRetainsRecoverableDraftAndClearsLostPrivateState(HttpStatusCode code, bool retain)
    {
        var client = new TypesClient(); using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var cut = RenderDetails(context, client.Resource.Name); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        SelectSection(context, "definition/identity"); cut.Find("[data-testid=storage-type-display-name]").Change("Private type draft");
        client.ReadError = new AgentstrationApiException("read failed", "read-id", code);
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreEqual(retain, cut.FindAll("[data-testid=storage-type-form]").Count > 0);
        Assert.AreEqual(retain, adapter.Resource is not null);
        if (retain) Assert.AreEqual("Private type draft", cut.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        else Assert.DoesNotContain("Private type draft", cut.Markup);
    }

    [TestMethod]
    public async Task TypeRefreshEvaluatesDraftsWhenTheResponseArrivesAndKeepsTheFieldsEditable()
    {
        var client = new TypesClient(); var original = client.Resource; using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(original));
        var cut = RenderDetails(context, original.Name); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        SelectSection(context, "definition/identity"); var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        var pending = new TaskCompletionSource<ResourceSnapshot<StorageTypeResource>>(TaskCreationOptions.RunContinuationsAsynchronously); client.PendingRead = pending.Task;
        var refresh = cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.AreEqual("true", cut.Find(".resource-details-body").GetAttribute("aria-busy")));
        Assert.IsFalse(cut.Find("[data-testid=storage-type-definition-fields]").HasAttribute("disabled"));
        cut.Find("[data-testid=storage-type-display-name]").Change("Typed during refresh");
        pending.SetResult(new(original with { Generation = 2 }, "new-version")); await refresh;
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Typed during refresh", cut.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"original\"", cut.Find("[data-testid=storage-type-form]").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    [DataRow("uid")]
    [DataRow("namespace")]
    [DataRow("scope")]
    public async Task TypeDetailsRejectReplacedCoordinatesBeforeLoadingDependencies(string changed)
    {
        var client = new TypesClient(); using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var cut = RenderDetails(context, client.Resource.Name); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        SelectSection(context, "definition/identity"); cut.Find("[data-testid=storage-type-display-name]").Change("Private replaced draft");
        var calls = client.UsageCalls;
        client.Resource = changed switch { "uid" => client.Resource with { Uid = Guid.NewGuid() }, "namespace" => client.Resource with { Metadata = client.Resource.Metadata with { Namespace = ResourceNamespace.Parse("other") } }, _ => client.Resource with { ScopeRef = ResourceScopeRef.Instance } };
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.IsNull(adapter.Resource); Assert.IsEmpty(cut.FindAll("[data-testid=storage-type-form]")); Assert.AreEqual(calls, client.UsageCalls); Assert.DoesNotContain("Private replaced draft", cut.Markup);
    }

    [TestMethod]
    public async Task TypeSavedSnapshotUpdatesTheHeaderWithoutAnExtraReadAndUnchangedRefreshDoesNotReportConflict()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new TypesClient(); using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var cut = RenderDetails(context, client.Resource.Name); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        SelectSection(context, "definition/identity"); cut.Find("[data-testid=storage-type-display-name]").Change("Pending title");
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.DoesNotContain("A newer saved version is available", cut.Markup);
        var reads = client.ResourceReads; cut.Find("form").Submit();
        Assert.AreEqual(reads, client.ResourceReads); Assert.AreEqual("Pending title", adapter.Resource is StorageTypeResource saved ? saved.Definition.DisplayName : null);
        Assert.AreEqual("\"original\"", client.LastEtag);
    }

    [TestMethod]
    public async Task FailedUsageReadDoesNotInventAnEmptyCollectionAndCanRetry()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new TypesClient { UsageError = new AgentstrationApiException("unavailable", "usage-id", HttpStatusCode.ServiceUnavailable) }; using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var cut = RenderDetails(context, client.Resource.Name); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.IsNotNull(adapter.Resource); StringAssert.Contains(cut.Markup, "Usages could not be loaded"); Assert.DoesNotContain("No discoverable usages in this context", cut.Markup);
        client.UsageError = null; await cut.InvokeAsync(() => adapter.OnRetry.InvokeAsync());
        StringAssert.Contains(cut.Markup, "No discoverable usages in this context");
    }
    [TestMethod]
    public void ExplicitViewerContextKeepsIdentityInspectionButLocksDefinitionAndAccess()
    {
        var client = new TypesClient(); using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource) + "&context=" + Uri.EscapeDataString(ResourceScopeRef.Workspace(Guid.NewGuid()).Value));
        var cut = RenderDetails(context, client.Resource.Name);
        Assert.IsEmpty(cut.FindAll("[data-resource-section=access], [data-testid=storage-type-publish], [data-testid=storage-type-delete]"));
        SelectSection(context, "definition/identity");
        Assert.IsTrue(cut.Find("[data-testid=storage-type-definition-fields]").HasAttribute("disabled"));
        cut.Find("form").Submit(); Assert.IsNull(client.Updated);
        Assert.IsEmpty(cut.FindAll("[data-testid=storage-type-save]"));
        SelectSection(context, "versions"); Assert.HasCount(1, cut.FindAll("[data-testid=storage-type-version-select]"));
    }
}
