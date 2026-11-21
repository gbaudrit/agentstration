using System.Net;
using Agentstration.Extensions.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ModelProviderNavigationTests
{
    [TestMethod]
    [DataRow("/tenants/33333333-3333-3333-3333-333333333333")]
    [DataRow("/workspaces/22222222-2222-2222-2222-222222222222")]
    public void InheritedProviderLoadsWithoutItsOwnersEditingSecretCatalogue(string contextScope)
    {
        using var context = CreateContext(out _);
        context.Services.AddSingleton<IConsoleNavigationContext>(new StubConsoleNavigationContext(contextScope));
        var secrets = (StubSecretsClient)context.Services.GetRequiredService<ISecretsClient>();
        secrets.FailReads = true;
        var cut = RenderFoundationProvider(context);
        cut.WaitForAssertion(() =>
        {
            var frame = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
            Assert.AreEqual(ResourceLoadStatus.Ready, frame.LoadState!.Status);
            Assert.IsNotNull(frame.Resource);
        });
        Assert.AreEqual(0, secrets.ReadCount);
        Assert.IsTrue(cut.FindComponent<ResourceDetailsTemplate>().Instance.DefinitionReadOnlyReason is not null);
    }

    [TestMethod]
    public void ProviderWizardAllowsIncompleteNavigationAndReportsIncompleteReview()
    {
        using var context = CreateContext(out var client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/modelproviders/new");
        var cut = context.Render<ModelProviderDetails>();
        Assert.AreEqual("false", cut.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete"));
        Assert.AreEqual("false", cut.Find("[data-wizard-step=review]").GetAttribute("data-step-complete"));
        cut.Find("[data-wizard-step=review]").Click();
        Assert.AreEqual("step", cut.Find("[data-wizard-step=review]").GetAttribute("aria-current"));
        cut.Find("[data-testid=model-provider-save]").Click();
        Assert.AreEqual("step", cut.Find("[data-wizard-step=identity]").GetAttribute("aria-current"));
        Assert.IsNull(client.CreatedRequest);
    }

    [TestMethod]
    public async Task ProviderUsesSharedFormAndKeepsInvalidDraftAndOriginalEtagDuringRefresh()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(out var client);
        var cut = RenderFoundationProvider(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProviderSectionAsync(context, cut, "definition/identity");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(adapter.SectionState, form.SectionState);
        Assert.AreSame(adapter.SectionPresentations, form.Sections);
        cut.Find("[data-testid=model-provider-display-name]").Change("");
        await cut.Find("[data-testid=model-provider-form]").SubmitAsync();
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        client.Replacement = (await client.GetModelProviderAsync("provider", CancellationToken.None)).Value with { Generation = 2 };
        client.ReadEtag = "\"provider-newer\"";
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("", cut.Find("[data-testid=model-provider-display-name]").GetAttribute("value"));
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        Assert.AreEqual("\"provider-etag\"", cut.Find("[data-testid=model-provider-form]").GetAttribute("data-resource-etag"));
        await ProviderSectionAsync(context, cut, "yaml");
        await ProviderSectionAsync(context, cut, "definition/identity");
        cut.Find("[data-testid=model-provider-display-name]").Change("My draft");
        await cut.Find("[data-testid=model-provider-form]").SubmitAsync();
        Assert.AreEqual("\"provider-etag\"", client.UpdateEtag);
        Assert.AreEqual("My draft", client.UpdatedRequest?.Properties.DisplayName);
    }

    [TestMethod]
    public async Task ProviderUnchangedRefreshDoesNotAdvertiseANewerSnapshot()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(out _);
        var cut = RenderFoundationProvider(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProviderSectionAsync(context, cut, "definition/identity");
        cut.Find("[data-testid=model-provider-display-name]").Change("Retained");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.DoesNotContain("A newer saved version", cut.Markup);
        Assert.AreEqual("Retained", cut.Find("[data-testid=model-provider-display-name]").GetAttribute("value"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task ProviderRefreshRetainsRecoverableDraftAndClearsPrivateStateOnLostAccess(HttpStatusCode statusCode, bool retain)
    {
        using var context = CreateContext(out var client);
        var cut = RenderFoundationProvider(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProviderSectionAsync(context, cut, "definition/identity");
        cut.Find("[data-testid=model-provider-display-name]").Change("Private draft");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        client.ReadError = new("Fixture failure", "fixture", statusCode);
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreEqual(retain, adapter.LoadState!.HasContent);
        if (retain)
        {
            Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
            client.ReadError = null;
            await cut.InvokeAsync(() => adapter.OnRetry.InvokeAsync());
            Assert.AreEqual(ResourceLoadStatus.Ready, adapter.LoadState!.Status);
            Assert.AreEqual("Private draft", cut.Find("[data-testid=model-provider-display-name]").GetAttribute("value"));
        }
        else
        {
            Assert.IsNull(adapter.Resource);
            Assert.IsEmpty(cut.FindAll("form, .resource-identity-summary, .unsaved-changes-bar"));
            Assert.DoesNotContain("Private draft", cut.Markup);
        }
    }

    [TestMethod]
    public async Task ProviderRefreshPreservesOpenContextualCreatorAndProtectsNavigation()
    {
        using var context = CreateContext(out var client, [FoundationExtension]);
        client.Replacement = (await client.GetModelProviderAsync("provider", CancellationToken.None)).Value with
        {
            Definition = new() { DisplayName = "Provider", Extension = new ResourceReference("typed-extension", @namespace: ResourceNamespace.Default), ContributionId = "typed" }
        };
        var cut = RenderFoundationProvider(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProviderSectionAsync(context, cut, "definition/configuration");
        cut.Find("[data-testid=model-provider-binding-create]").Click();
        var creator = cut.FindComponent<ContextualParameterCreator>().Instance;
        Assert.IsTrue(creator.IsOpen);
        Assert.IsTrue(adapter.HasAdditionalDraft);
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreSame(creator, cut.FindComponent<ContextualParameterCreator>().Instance);
        Assert.IsTrue(creator.IsOpen);
        await cut.InvokeAsync(() => creator.OnCancel.InvokeAsync());
        Assert.IsFalse(adapter.HasAdditionalDraft);
    }

    [TestMethod]
    public async Task ProviderRequiredBindingErrorTargetsConfigurationRootField()
    {
        using var context = CreateContext(out var client, [FoundationExtension]);
        client.Replacement = (await client.GetModelProviderAsync("provider", CancellationToken.None)).Value with
        {
            Definition = new() { DisplayName = "Provider", Extension = new ResourceReference("typed-extension", @namespace: ResourceNamespace.Default), ContributionId = "typed" }
        };
        var cut = RenderFoundationProvider(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ProviderSectionAsync(context, cut, "definition/configuration");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.IsTrue(form.EditContext.GetValidationMessages(form.EditContext.Field(nameof(ModelProviderEditorModel.ValueBindings))).Any());
        await ProviderSectionAsync(context, cut, "definition/identity");
        await cut.Find("[data-testid=model-provider-form]").SubmitAsync();
        Assert.AreEqual("definition/configuration", adapter.SectionState.CurrentPath);
        Assert.AreEqual("true", cut.Find("[data-testid=model-provider-binding-target]").Closest(".form-field")!.GetAttribute("aria-invalid"));
        Assert.IsNull(client.UpdatedRequest);
    }

    private static ExtensionResponse FoundationExtension => new("typed-extension", ResourceNamespace.DefaultValue,
        new Uri("http://localhost:5000"), "available", new("typed", "Typed", "1.0.0", null),
        [new("model-provider", "typed")], [], [], [], null, "manual",
        ValueRequirements: [new("model-provider", "typed", "endpoint", true, "string", "standard", "Endpoint")]);

    private static IRenderedComponent<ModelProviderDetails> RenderFoundationProvider(BunitContext context)
    {
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/modelproviders/provider?namespace=shared.models&scopeRef=%2Finstance");
        var cut = context.Render<ModelProviderDetails>(parameters => parameters.Add(p => p.Name, "provider"));
        cut.WaitForElement(".resource-identity-summary");
        return cut;
    }
}
