using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ModelDetailsTests
{
    [TestMethod]
    public async Task ModelInspectionUsesSharedReadonlyFormAndIndependentProviderCommandForm()
    {
        using var context = ModelContext(out _, out _);
        var cut = RenderModel(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.IsNull(cut.FindComponent<ResourceIdentitySummary>().Instance.Description);
        await ModelSectionAsync(context, cut, "definition/identity");
        var inspection = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(adapter.SectionState, inspection.SectionState);
        Assert.AreSame(adapter.SectionPresentations, inspection.Sections);
        Assert.IsTrue(inspection.ReadOnly);
        Assert.IsFalse(inspection.OnValidSubmit.HasDelegate);
        Assert.IsNotNull(cut.Find("[data-testid=model-identity-editor]"));
        Assert.IsNull(cut.FindComponent<ResourceIdentityEditor>().Instance.Description);
        await ModelSectionAsync(context, cut, "definition/configuration");
        Assert.IsNotNull(cut.Find(".resource-section-actions [data-testid=edit-model-override]"));
        await cut.Find(".resource-section-actions [data-testid=edit-model-override]").ClickAsync(new());
        var forms = cut.FindComponents<ResourceDefinitionForm>();
        var command = forms.Select(c => c.Instance).Single(f => f.Id == "model-provider-override-form");
        Assert.AreSame(adapter.SectionState, command.SectionState);
        Assert.AreSame(adapter.SectionPresentations, command.Sections);
        Assert.IsFalse(command.ReadOnly);
        Assert.IsFalse(cut.Find("#model-provider-override-form > fieldset").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("#model-inspection-form > fieldset").HasAttribute("disabled"));
        await ModelSectionAsync(context, cut, "overview");
        Assert.HasCount(1, cut.FindAll("[data-testid=edit-model-override]"));
    }

    [TestMethod]
    public async Task OverrideRefreshRetainsInvalidNumberContextAndOriginalProviderEtag()
    {
        using var context = ModelContext(out _, out var providers);
        var cut = RenderModel(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await cut.Find("[data-testid=edit-model-override]").ClickAsync(new());
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        cut.Find("[data-testid=model-override-context-tokens]").Change("invalid-number");
        await cut.Find("#model-provider-override-form").SubmitAsync();
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        Assert.IsTrue(adapter.HasAdditionalDraft);
        providers.ReadEtag = "\"newer-provider\"";
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("invalid-number", cut.Find("[data-testid=model-override-context-tokens]").GetAttribute("value"));
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        await ModelSectionAsync(context, cut, "yaml");
        await ModelSectionAsync(context, cut, "overrides");
        cut.Find("[data-testid=model-override-context-tokens]").Change("64000");
        await cut.Find("#model-provider-override-form").SubmitAsync();
        Assert.AreEqual("\"provider-etag\"", providers.UpdateEtag);
        Assert.AreEqual(64_000, providers.UpdatedRequest!.Properties.SpecificationOverrides["gpt-5"].Limits.ContextTokens);
    }

    [TestMethod]
    public async Task OverrideUnchangedRefreshRetainsTheFormWithoutAFalseConflict()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = ModelContext(out _, out _);
        var cut = RenderModel(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await cut.Find("[data-testid=edit-model-override]").ClickAsync(new());
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        cut.Find("[data-testid=model-override-context-tokens]").Change("64000");
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("64000", cut.Find("[data-testid=model-override-context-tokens]").GetAttribute("value"));
        Assert.DoesNotContain("A newer observation", cut.Markup);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task ModelRefreshPreservesRecoverableOverrideAndClearsPrivateContentOnLostAccess(HttpStatusCode status, bool retain)
    {
        using var context = ModelContext(out var models, out _);
        var cut = RenderModel(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await cut.Find("[data-testid=edit-model-override]").ClickAsync(new());
        cut.Find("[data-testid=model-override-context-tokens]").Change("64000");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        models.ReadError = new("Fixture error", "fixture", status);
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreEqual(retain, adapter.LoadState!.HasContent);
        if (retain)
        {
            Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
            models.ReadError = null;
            await cut.InvokeAsync(() => adapter.OnRetry.InvokeAsync());
            Assert.AreEqual(ResourceLoadStatus.Ready, adapter.LoadState!.Status);
            Assert.AreEqual("64000", cut.Find("[data-testid=model-override-context-tokens]").GetAttribute("value"));
        }
        else
        {
            Assert.IsNull(adapter.Resource);
            Assert.IsFalse(adapter.HasAdditionalDraft);
            Assert.IsEmpty(cut.FindAll("form, .resource-identity-summary, [data-testid=model-override-editor]"));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AChangedProviderUidOrDeniedProviderReadClearsItsOverrideButRetainsAuthorizedModel(bool denied)
    {
        using var context = ModelContext(out _, out var providers);
        var cut = RenderModel(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await cut.Find("[data-testid=edit-model-override]").ClickAsync(new());
        cut.Find("[data-testid=model-override-context-tokens]").Change("64000");
        if (denied) providers.ReadError = new("Provider denied", "fixture", HttpStatusCode.Forbidden);
        else providers.Resource = providers.Resource with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.IsNotNull(adapter.Resource);
        Assert.AreEqual(ResourceLoadStatus.Ready, adapter.LoadState!.Status);
        Assert.IsFalse(adapter.HasAdditionalDraft);
        Assert.IsEmpty(cut.FindAll("#model-provider-override-form, [data-testid=edit-model-override]"));
        Assert.IsNull(providers.UpdatedRequest);
    }

    [TestMethod]
    public async Task ReadOnlyProviderCommandsCannotWriteThroughTheObservedModelsForm()
    {
        using var context = ModelContext(out _, out var providers, new ResourceListContextProvider { Scope = ResourceScopeRef.Instance.Value, Permissions = new HashSet<string> { AuthorizationPermissions.ResourcesRead } });
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var cut = RenderModel(context);
        await ModelSectionAsync(context, cut, "overrides");
        Assert.IsTrue(cut.Find("#model-provider-override-form > fieldset").HasAttribute("disabled"));
        await cut.Find("#model-provider-override-form").SubmitAsync();
        Assert.IsNull(providers.UpdatedRequest);
    }

    [TestMethod]
    public async Task SuccessfulOverrideWriteIsNotTurnedBackIntoAnUnsavedDraftByObservationFailure()
    {
        using var context = ModelContext(out _, out var providers);
        var cut = RenderModel(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await cut.Find("[data-testid=edit-model-override]").ClickAsync(new());
        cut.Find("[data-testid=model-override-context-tokens]").Change("64000");
        providers.InventoryError = new("Observation unavailable", "fixture", HttpStatusCode.ServiceUnavailable);
        await cut.Find("#model-provider-override-form").SubmitAsync();
        Assert.IsNotNull(providers.UpdatedRequest);
        Assert.IsFalse(adapter.HasAdditionalDraft);
        Assert.IsNotNull(cut.Find("[data-testid=model-override-message]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=model-override-error]"));
    }

    private static BunitContext ModelContext(out FakeModelsClient models, out FakeProvidersClient providers, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        if (provider is not null) context.WithResourceListContext(provider);
        models = new(ModelObservationState.Available); providers = new(true);
        context.Services.AddSingleton<IModelsClient>(models);
        context.Services.AddSingleton<IModelProvidersClient>(providers);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.ComponentFactories.AddStub<ResourceAccessPanel>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/models/{FakeModelsClient.ResourceName}?namespace=shared.models&scopeRef=%2Finstance");
        ConfigureNavigation(context);
        return context;
    }
    private static IRenderedComponent<ModelDetails> RenderModel(BunitContext context)
    {
        var cut = context.Render<ModelDetails>(p => p.Add(c => c.Name, FakeModelsClient.ResourceName));
        cut.WaitForElement(".resource-identity-summary");
        return cut;
    }
}
