using System.Net;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.Schemas;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentstration.Web.Tests;

public sealed partial class SchemaConsoleTests
{
    private static IRenderedComponent<SchemaEditor> Persisted(BunitContext context)
    {
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/schemas/contact?namespace=shared.contracts&scopeRef={Uri.EscapeDataString(Scope.Value)}");
        return context.Render<SchemaEditor>(p => p.Add(x => x.Name, "contact"));
    }

    [TestMethod]
    public async Task RawSourceAndVisitedEditorSurviveRefreshAndMetadataSaveUntilExplicitDiscard()
    {
        var client = new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]); using var context = Context(client); var page = Persisted(context);
        DataSourceScreenTestSupport.Select(page, "definition/configuration");
        page.Find("[data-testid=schema-definition-source-mode]").Click();
        var source = page.FindComponent<SchemaSourceEditor>();
        await page.InvokeAsync(() => source.Instance.ValueChanged.InvokeAsync("{ invalid source"));
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        DataSourceScreenTestSupport.Select(page, "definition/identity"); page.Find("[data-testid=schema-display-name]").Input("Metadata saved separately");
        await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreSame(source.Instance, page.FindComponent<SchemaSourceEditor>().Instance);
        Assert.AreEqual("{ invalid source", page.FindComponent<SchemaSourceEditor>().Instance.Value);
        await page.Find("form").SubmitAsync();
        Assert.AreEqual("Metadata saved separately", client.UpdatedProperties?.DisplayName);
        Assert.AreEqual("object", client.UpdatedProperties!.Content.GetProperty("type").GetString());
        Assert.AreEqual("{ invalid source", page.FindComponent<SchemaSourceEditor>().Instance.Value);
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        page.Find(".unsaved-changes-bar .text-button").Click();
        DataSourceScreenTestSupport.Select(page, "definition/configuration"); page.Find("[data-testid=schema-definition-source-mode]").Click();
        Assert.DoesNotContain("invalid source", page.FindComponent<SchemaSourceEditor>().Instance.Value);
    }

    [TestMethod]
    public async Task DefinitionSavePreservesIndependentYamlAndItsOriginalEtag()
    {
        var client = new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]); using var context = Context(client); var page = Persisted(context);
        DataSourceScreenTestSupport.Select(page, "yaml"); var input = page.Find("[data-testid=schema-yaml-source]");
        var pending = (input.GetAttribute("value") ?? input.TextContent).Replace("displayName: Contact", "displayName: Independent YAML", StringComparison.Ordinal); input.Input(pending);
        DataSourceScreenTestSupport.Select(page, "definition/identity"); page.Find("[data-testid=schema-display-name]").Input("Saved metadata");
        await page.Find("form").SubmitAsync(); Assert.AreEqual("\"etag\"", client.UpdatedEtag);
        DataSourceScreenTestSupport.Select(page, "yaml"); input = page.Find("[data-testid=schema-yaml-source]"); Assert.AreEqual(pending, input.GetAttribute("value") ?? input.TextContent);
        page.Find("[data-testid=schema-yaml-save]").Click(); Assert.AreEqual("Independent YAML", client.UpdatedProperties?.DisplayName); Assert.AreEqual("\"etag\"", client.UpdatedEtag);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    public async Task ResourceRefreshRetainsRecoverableDraftsAndClearsDeniedContent(HttpStatusCode status, bool retains)
    {
        var client = new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]); using var context = Context(client); var page = Persisted(context);
        DataSourceScreenTestSupport.Select(page, "definition/identity"); page.Find("[data-testid=schema-display-name]").Input("Private schema draft");
        client.ReadFailure = new AgentstrationApiException("fixture", "fixture", status);
        await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(retains, page.Markup.Contains("Private schema draft", StringComparison.Ordinal));
        Assert.AreEqual(retains, page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()); Assert.IsNull(client.UpdatedProperties);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeniedDefinitionAndYamlWritesClearPrivateContent(bool yaml)
    {
        var client = new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]); using var context = Context(client); var page = Persisted(context);
        client.WriteFailure = new AgentstrationApiException("private-response", "fixture", HttpStatusCode.Forbidden);
        if (yaml) { DataSourceScreenTestSupport.Select(page, "yaml"); var field = page.Find("[data-testid=schema-yaml-source]"); field.Input((field.GetAttribute("value") ?? field.TextContent).Replace("displayName: Contact", "displayName: Private schema draft", StringComparison.Ordinal)); page.Find("[data-testid=schema-yaml-save]").Click(); }
        else { DataSourceScreenTestSupport.Select(page, "definition/identity"); page.Find("[data-testid=schema-display-name]").Input("Private schema draft"); await page.Find("form").SubmitAsync(); }
        Assert.IsNull(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource); Assert.DoesNotContain("Private schema draft", page.Markup); Assert.DoesNotContain("private-response", page.Markup);
    }

    [TestMethod]
    public void ManagedSchemaKeepsPublicationSeparateFromDefinitionEditing()
    {
        var saved = Schema(SchemaFormatIds.JsonSchema) with { Governance = ResourceGovernance.Core("fixture") };
        using var context = Context(new SchemaClientStub([saved])); var page = Persisted(context);
        DataSourceScreenTestSupport.Select(page, "versions"); Assert.IsFalse(page.Find("[data-testid=schema-publish]").HasAttribute("disabled"));
        DataSourceScreenTestSupport.Select(page, "definition/configuration"); Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly); Assert.IsTrue(page.FindComponent<SchemaDocumentEditor>().Instance.ReadOnly);
    }

    [TestMethod]
    public void AutomaticInitialScopeDoesNotCreateAnUnsavedNavigationPrompt()
    {
        using var context = Context(new SchemaClientStub([])); var page = context.Render<SchemaEditor>();
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        page.Find("[data-testid=schema-display-name]").Input("Actual user input");
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task CreationPrerenderDisablesFieldsAndWizardUntilInteractive()
    {
        using var context = Context(new SchemaClientStub([]));
        await using var renderer = new HtmlRenderer(context.Services, context.Services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<SchemaEditor>()).ToHtmlString());
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        Assert.IsTrue(document.QuerySelector("[data-testid=schema-definition-fields]")!.HasAttribute("disabled"));
        Assert.IsTrue(document.QuerySelector("[data-wizard-step=configuration]")!.HasAttribute("disabled"));
    }

    [TestMethod]
    public void InheritedDefinitionKeepsTheExistingScopedPublicationCommand()
    {
        using var context = Context(new SchemaClientStub([Schema(SchemaFormatIds.JsonSchema)]));
        context.Services.AddSingleton<IConsoleNavigationContext>(new SchemaNavigationContext());
        var page = Persisted(context);
        DataSourceScreenTestSupport.Select(page, "definition/identity");
        Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        DataSourceScreenTestSupport.Select(page, "versions");
        Assert.IsFalse(page.Find("[data-testid=schema-publish]").HasAttribute("disabled"));
    }
    private sealed class SchemaNavigationContext : IConsoleNavigationContext
    {
        public string? ScopeRef => "/instance";
        public string AddToUri(string uri) => uri;
    }

    [TestMethod]
    public async Task CreationWizardAllowsIncompleteNavigationAndReturnsToIdentityOnFinalValidation()
    {
        using var context = Context(new SchemaClientStub([])); var page = context.Render<SchemaEditor>();
        page.Find("[data-wizard-step=configuration]").Click();
        Assert.AreEqual("configuration", page.FindComponent<CreationWizard>().Instance.CurrentStepId);
        page.Find("[data-testid=schema-save]").Click();
        Assert.AreEqual("identity", page.FindComponent<CreationWizard>().Instance.CurrentStepId);
        Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.GetValidationMessages().Any());
        await Task.CompletedTask;
    }
}
