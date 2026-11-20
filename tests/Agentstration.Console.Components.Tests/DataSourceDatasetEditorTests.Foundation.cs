using System.Net;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class DataSourceDatasetEditorTests
{
    [TestMethod]
    public async Task RefreshKeepsInvalidLifecycleAndOriginalFormUntilExplicitDiscard()
    {
        using var handler = new DatasetHandler { Existing = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http); DataSourceScreenTestSupport.Configure(context);
        var cut = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.Name, "inventory").Add(c => c.ScopeRef, "/instance"));
        DataSourceScreenTestSupport.Select(cut, "definition/configuration");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        var template = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreSame(template.SectionState, form.SectionState); Assert.AreSame(template.SectionPresentations, form.Sections);
        cut.Find("[data-testid=lifecycle-enable]").Change(true);
        cut.Find("[data-testid=lifecycle-ttl]").Change("invalid duration");
        handler.SavedName = "Refreshed inventory"; handler.ReadEtag = "\"newer\"";
        await cut.InvokeAsync(() => template.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("invalid duration", cut.Find("[data-testid=lifecycle-ttl]").GetAttribute("value"));
        Assert.DoesNotContain("Refreshed inventory", cut.Find(".resource-identity-summary").TextContent);
        cut.Find(".unsaved-changes-bar button[type=button]").Click();
        Assert.Contains("Refreshed inventory", cut.Find(".resource-identity-summary").TextContent);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsNull(handler.Created);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    public async Task DatasetRefreshClearsPrivateDataOnAccessLossAndRetainsRecoverableDrafts(HttpStatusCode status, bool retained)
    {
        using var handler = new DatasetHandler { Existing = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http); DataSourceScreenTestSupport.Configure(context);
        var cut = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.Name, "inventory").Add(c => c.ScopeRef, "/instance"));
        DataSourceScreenTestSupport.Select(cut, "definition/identity");
        cut.Find("[data-testid=data-source-display-name]").Input("Private dataset draft");
        handler.ReadFailure = status;
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(retained, cut.Markup.Contains("Private dataset draft", StringComparison.Ordinal));
        Assert.AreEqual(retained, cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsNull(handler.Created);
    }

    [TestMethod]
    public async Task DefinitionSaveKeepsQualifiedCoordinatesAndVisitedContentWithoutReloading()
    {
        using var handler = new DatasetHandler { Existing = true, WithHistory = true };
        handler.SourceOverride = handler.Source with { Metadata = handler.Source.Metadata with { Namespace = new("custom") } };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/data-sources/inventory?namespace=custom&scopeRef=%2Finstance#overview");
        DataSourceScreenTestSupport.Configure(context);
        var cut = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.Name, "inventory").Add(c => c.Namespace, "custom").Add(c => c.ScopeRef, "/instance"));
        DataSourceScreenTestSupport.Select(cut, "acquisitions");
        await cut.Find(".resource-details-section:not([hidden]) [data-testid=dataset-read-content]").ClickAsync(new());
        var preview = cut.Find("[data-testid=dataset-content]").TextContent;
        await cut.InvokeAsync(() => navigation.NavigateTo(cut.Find("[data-resource-section='definition/identity']").GetAttribute("href")!));
        cut.Find("[data-testid=data-source-display-name]").Input("Updated inventory");
        var address = navigation.Uri;
        await cut.Find("form").SubmitAsync();
        Assert.AreEqual(1, handler.SourceUpdates); Assert.AreEqual(1, handler.SourceReads);
        Assert.AreEqual(address, navigation.Uri); Assert.AreEqual(new ResourceNamespace("custom"), handler.Source.Namespace);
        Assert.AreEqual("Updated inventory", handler.Source.Definition.DisplayName);
        Assert.AreEqual(preview, cut.Find("[data-testid=dataset-content]").TextContent);
        Assert.AreEqual(1, handler.ContentReads); Assert.Contains("acq-3", preview);
        Assert.Contains("namespace=custom", handler.LastUpdatePath!); Assert.Contains("scopeRef=%2Finstance", handler.LastUpdatePath!);
        Assert.AreEqual("\"fixture\"", handler.LastUpdateEtag);
    }

    [TestMethod]
    [DataRow("en-US", "Enter a valid namespace.")]
    [DataRow("fr-FR", "Saisissez un espace de noms valide.")]
    public async Task FinalValidationReturnsToTheInvalidNamespaceWithoutCreating(string cultureName, string expected)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new DatasetHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http); DataSourceScreenTestSupport.Configure(context);
        var cut = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.ScopeRef, "/instance"));
        await SelectAsync(cut, handler); cut.Find("[data-testid=dataset-namespace]").Change("invalid/namespace");
        await cut.InvokeAsync(() => cut.FindComponent<CreationWizard>().Instance.GoToStepAsync("review"));
        await cut.Find("[data-testid=data-source-save]").ClickAsync(new());
        Assert.AreEqual("identity", cut.FindComponent<CreationWizard>().Instance.CurrentStepId);
        Assert.Contains(expected, cut.Find("[data-testid=dataset-namespace]").Closest(".form-field")!.TextContent);
        Assert.IsNull(handler.Created);
    }
}
