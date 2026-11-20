using System.Net;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Console;
using Bunit;

namespace Agentstration.Web.Tests;

public sealed partial class StorageComponentTests
{
    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task StorageRefreshRetainsRecoverableDraftAndClearsLostPrivateState(HttpStatusCode status, bool retain)
    {
        var client = new StorageClient(); using var context = Setup(client); Navigate(context, Resource());
        var cut = RenderDetails(context); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        SelectSection(context, "definition/identity"); cut.Find("[data-testid=storage-display-name]").Change("Private Storage draft");
        client.ReadError = new AgentstrationApiException("failed read", "read-id", status);
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreEqual(retain, adapter.Resource is not null); Assert.AreEqual(retain, cut.FindAll("[data-testid=storage-form]").Count > 0);
        if (retain) Assert.AreEqual("Private Storage draft", cut.Find("[data-testid=storage-display-name]").GetAttribute("value"));
        else Assert.DoesNotContain("Private Storage draft", cut.Markup);
    }

    [TestMethod]
    public async Task StorageKeepsEditsMadeDuringRefreshAndDoesNotLockTheFields()
    {
        var client = new StorageClient(); using var context = Setup(client); Navigate(context, Resource());
        var cut = RenderDetails(context); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        SelectSection(context, "definition/identity"); var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        var pending = new TaskCompletionSource<ResourceSnapshot<StorageResource>>(TaskCreationOptions.RunContinuationsAsynchronously); client.PendingRead = pending.Task;
        var refresh = cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find("[data-testid=storage-refresh]").HasAttribute("disabled")));
        Assert.IsFalse(cut.Find("[data-testid=storage-definition-fields]").HasAttribute("disabled"));
        cut.Find("[data-testid=storage-display-name]").Change("Typed during pending refresh");
        pending.SetResult(new(Resource() with { Generation = 9 }, "new-etag")); await refresh;
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Typed during pending refresh", cut.Find("[data-testid=storage-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"revision-4\"", cut.Find("[data-testid=storage-form]").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    [DataRow("uid")]
    [DataRow("namespace")]
    [DataRow("scope")]
    public async Task StorageRejectsAReplacedIdentityBeforeLoadingDependencies(string changed)
    {
        var client = new StorageClient(); using var context = Setup(client); Navigate(context, Resource());
        var cut = RenderDetails(context); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        SelectSection(context, "definition/identity"); cut.Find("[data-testid=storage-display-name]").Change("Private replaced Storage draft"); var usageCalls = client.UsageCalls;
        client.Current = changed switch { "uid" => client.Current with { Uid = Guid.NewGuid() }, "namespace" => client.Current with { Metadata = client.Current.Metadata with { Namespace = ResourceNamespace.Parse("other") } }, _ => client.Current with { ScopeRef = ResourceScopeRef.Instance } };
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.IsNull(adapter.Resource); Assert.IsEmpty(cut.FindAll("[data-testid=storage-form]")); Assert.DoesNotContain("Private replaced Storage draft", cut.Markup); Assert.AreEqual(usageCalls, client.UsageCalls);
    }

    [TestMethod]
    public void ManagedStorageLocksTheCommonFormButKeepsIndependentOwnerAccessAndNeutralUnknownObservation()
    {
        var client = new StorageClient(); client.Current = client.Current with { Governance = ResourceGovernance.Core("fixture"), ObservedState = null };
        using var context = Setup(client); Navigate(context, Resource()); var cut = RenderDetails(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance; Assert.AreEqual(UiStatus.Neutral, adapter.Status);
        Assert.HasCount(1, cut.FindAll(".resource-navigation-rail [data-resource-section=access]")); Assert.IsEmpty(cut.FindAll("[data-testid=storage-reconcile]"));
        SelectSection(context, "definition/identity"); Assert.IsTrue(cut.Find("[data-testid=storage-definition-fields]").HasAttribute("disabled"));
        cut.Find("form").Submit(); Assert.IsNull(client.Updated); Assert.IsEmpty(cut.FindAll("[data-testid=storage-save]"));
        SelectSection(context, "yaml"); StringAssert.Contains(cut.Find("[data-testid=storage-yaml]").GetAttribute("value")!, "kind: Storage");
    }

    [TestMethod]
    public async Task UnknownStorageUsagesAreNotShownAsAnEmptyCollectionAndRetryCanRecover()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient { UsageError = new AgentstrationApiException("unavailable", "usage-id", HttpStatusCode.ServiceUnavailable) };
        using var context = Setup(client); Navigate(context, Resource()); var cut = RenderDetails(context); var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.IsNotNull(adapter.Resource); SelectSection(context, "dependents"); StringAssert.Contains(cut.Markup, "Usages could not be loaded"); Assert.DoesNotContain("No dependencies reported by the API", cut.Markup);
        client.UsageError = null; await cut.InvokeAsync(() => adapter.OnRetry.InvokeAsync());
        StringAssert.Contains(cut.Markup, "No dependencies reported by the API");
    }
}
