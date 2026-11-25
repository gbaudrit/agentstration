using System.Net;
using Agentstration.DataSources.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class DataSourceEditorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NewSourceStartsCleanAndTracksIdentityInput(bool explicitOwner)
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IDataSourcesClient>(new SourceClientStub());
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.AddSingleton(new ConsoleContextState(new AcquisitionConsoleContextProvider(Acquisition, true)));
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new CreationScopeInventory());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            explicitOwner ? $"/data-sources/new?scopeRef={Uri.EscapeDataString(Scope.Value)}" : "/data-sources/new");
        var cut = context.Render<DataSourceEditor>();
        var guard = cut.FindComponent<UnsavedNavigationGuard>().Instance;
        cut.WaitForAssertion(() => Assert.IsFalse(guard.HasUnsavedChanges!()));
        Assert.AreEqual(Scope.Value, cut.Find("[data-testid=resource-scope-picker]").GetAttribute("value"));
        cut.Find("[data-testid=data-source-display-name]").Input("New source draft");
        Assert.AreEqual("New source draft", cut.Find("[data-testid=data-source-display-name]").GetAttribute("value"));
        Assert.IsTrue(guard.HasUnsavedChanges!());
    }

    private sealed class CreationScopeInventory : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string resourceKind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(Scope, ResourceScopeKind.Workspace, "Catalog", true)]);
    }

    [TestMethod]
    [DataRow(DataSourceAcquisitionState.Running)]
    [DataRow(DataSourceAcquisitionState.Failed)]
    [DataRow(DataSourceAcquisitionState.Cancelled)]
    public void RegisteredArtifactEvidenceIsNotAdvertisedAsAnAcquiredResultBeforeGlobalSuccess(DataSourceAcquisitionState state)
    {
        using var context = FoundationContext(new SourceClientStub { AcquisitionResult = Acquisition with { State = state } });
        var rendered = context.Render<DataSourceEditor>(parameters => parameters.Add(component => component.Name, "documentation"));
        DataSourceScreenTestSupport.Select(rendered, "acquisitions");
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='data-source-acquisition-row']")));
        Assert.HasCount(0, rendered.FindAll("[data-testid='data-source-acquisition-preview']"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='data-source-create-tool']"));
    }

    private static BunitContext FoundationContext(SourceClientStub client)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<IDataSourcesClient>(client);
        context.Services.AddSingleton<IDataSourceProfilesClient>(new ProfileClientStub());
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"/data-sources/documentation?namespace=default&scopeRef={Uri.EscapeDataString(Scope.Value)}");
        DataSourceScreenTestSupport.Configure(context);
        return context;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshRetainsInvalidFormOrYamlAndOriginalEtagUntilExplicitDiscard(bool yaml)
    {
        var client = new SourceClientStub(); using var context = FoundationContext(client);
        var cut = context.Render<DataSourceEditor>(p => p.Add(c => c.Name, "documentation"));
        DataSourceScreenTestSupport.Select(cut, yaml ? "yaml" : "definition/identity");
        var field = yaml ? "[data-testid=data-source-yaml-editor] textarea" : "[data-testid=data-source-display-name]";
        cut.Find(field).Input(yaml ? "invalid: [ private draft" : "");
        var form = yaml ? null : cut.FindComponent<ResourceDefinitionForm>().Instance;
        if (form is not null)
        {
            Assert.AreSame(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionState, form.SectionState);
            Assert.AreSame(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionPresentations, form.Sections);
            await cut.Find("form").SubmitAsync();
            Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        }
        client.Saved = client.Saved with { Generation = 2, Definition = client.Saved.Definition with { DisplayName = "Updated server source" } };
        client.ReadEtag = "newer-etag";
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(yaml ? "invalid: [ private draft" : "", cut.Find(field).GetAttribute("value"));
        Assert.DoesNotContain("Updated server source", cut.Find(".resource-identity-summary").TextContent);
        if (form is not null) Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        cut.Find(".unsaved-changes-bar button[type=button]").Click();
        Assert.Contains("Updated server source", cut.Find(".resource-identity-summary").TextContent);
        Assert.AreEqual(0, client.Updates);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task DefinitionSaveRetainsIndependentYamlDraftAndItsOriginalEtag()
    {
        var client = new SourceClientStub(); using var context = FoundationContext(client);
        var cut = context.Render<DataSourceEditor>(p => p.Add(c => c.Name, "documentation"));
        DataSourceScreenTestSupport.Select(cut, "yaml");
        var yaml = cut.Find("[data-testid=data-source-yaml-editor] textarea"); var original = yaml.GetAttribute("value")!;
        yaml.Input(original.Replace("Documentation", "Yaml source", StringComparison.Ordinal));
        DataSourceScreenTestSupport.Select(cut, "definition/identity");
        cut.Find("[data-testid=data-source-display-name]").Input("Definition source");
        // The form command keeps the independent YAML draft. Both commands still use their accepted ETags.
        Assert.IsFalse(cut.Find("[data-testid=data-source-save]").HasAttribute("disabled"));
        await cut.Find("form").SubmitAsync();
        Assert.AreEqual("\"etag\"", client.LastWriteEtag);
        Assert.AreEqual(1, client.Updates);
        DataSourceScreenTestSupport.Select(cut, "yaml");
        Assert.Contains("Yaml source", cut.Find("[data-testid=data-source-yaml-editor] textarea").GetAttribute("value")!);
        await cut.Find(".unsaved-changes-bar .button-primary").ClickAsync();
        Assert.AreEqual(2, client.Updates); Assert.AreEqual("\"etag\"", client.LastWriteEtag);
        Assert.AreEqual(Source.Definition.DisplayName.Replace("Documentation", "Yaml source", StringComparison.Ordinal), client.Saved.Definition.DisplayName);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    public async Task ReadFailuresRetainOnlyStillAuthorizedDrafts(HttpStatusCode failure, bool retains)
    {
        var client = new SourceClientStub(); using var context = FoundationContext(client);
        var cut = context.Render<DataSourceEditor>(p => p.Add(c => c.Name, "documentation"));
        DataSourceScreenTestSupport.Select(cut, "definition/identity");
        cut.Find("[data-testid=data-source-display-name]").Input("Private source draft");
        client.ReadFailure = new AgentstrationApiException("fixture", "fixture", failure);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(retains, cut.Markup.Contains("Private source draft", StringComparison.Ordinal));
        Assert.AreEqual(retains, cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.AreEqual(0, client.Updates);
    }

    [TestMethod]
    public async Task TypingDuringAnUnchangedRefreshKeepsTheSameFormWithoutAFalseConflict()
    {
        var client = new SourceClientStub(); using var context = FoundationContext(client);
        var cut = context.Render<DataSourceEditor>(p => p.Add(c => c.Name, "documentation"));
        DataSourceScreenTestSupport.Select(cut, "definition/identity"); var form = cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        var pending = new TaskCompletionSource<ResourceSnapshot<DataSourceResource>>(TaskCreationOptions.RunContinuationsAsynchronously); client.PendingRead = pending.Task;
        var refresh = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.Find("[data-testid=data-source-display-name]").Input("Typed during refresh");
        pending.SetResult(new(client.Saved, client.ReadEtag)); await refresh;
        Assert.AreSame(form, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Typed during refresh", cut.Find("[data-testid=data-source-display-name]").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".resource-details-feedback .form-alert-warning"));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ManagedDefinitionAndIndependentExecutionKeepTheirOwnPermissions(bool managed, bool canRun)
    {
        var client = new SourceClientStub { CanRun = canRun };
        client.Saved = client.Saved with { Uid = Guid.NewGuid() };
        if (managed) client.Saved = client.Saved with { Governance = ResourceGovernance.Core("fixture") };
        using var context = FoundationContext(client);
        var cut = context.Render<DataSourceEditor>(p => p.Add(c => c.Name, "documentation"));
        DataSourceScreenTestSupport.Select(cut, "acquisitions");
        Assert.AreEqual(!canRun, cut.Find("[data-testid=data-source-start-acquisition]").HasAttribute("disabled"));
        DataSourceScreenTestSupport.Select(cut, "definition/identity");
        Assert.AreEqual(managed, cut.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        Assert.IsNotNull(cut.Find("[data-resource-section=access]"));
        if (managed)
        {
            await cut.InvokeAsync(() => cut.FindComponent<ResourceActivationToggle>().Instance.EnabledChanged.InvokeAsync(false));
            Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
            Assert.AreEqual(0, client.Updates);
        }
    }

    [TestMethod]
    public async Task ReplacementUidClearsTheOldPrivateDraftAndGuard()
    {
        var client = new SourceClientStub(); using var context = FoundationContext(client);
        var cut = context.Render<DataSourceEditor>(p => p.Add(c => c.Name, "documentation"));
        DataSourceScreenTestSupport.Select(cut, "definition/identity"); cut.Find("[data-testid=data-source-display-name]").Input("Private old source");
        client.Saved = client.Saved with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.DoesNotContain("Private old source", cut.Markup);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
    }

    [TestMethod]
    public async Task ConfirmedDeleteReleasesDefinitionAndPolicyDraftsBeforeReturnNavigation()
    {
        var client = new SourceClientStub(); client.Saved = client.Saved with { Uid = Guid.NewGuid() };
        using var context = FoundationContext(client); context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<DataSourceEditor>(p => p.Add(c => c.Name, "documentation"));
        DataSourceScreenTestSupport.Select(cut, "definition/identity"); cut.Find("[data-testid=data-source-display-name]").Input("Pending deletion draft");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        await ResourceScreenDeletionAssertions.ConfirmAndReleasePolicyDraftAsync(cut, navigation);
        Assert.AreEqual(1, client.Deletes); Assert.AreEqual("\"etag\"", client.LastWriteEtag);
        Assert.AreEqual("/data-sources", new Uri(navigation.Uri).AbsolutePath);
    }
}
