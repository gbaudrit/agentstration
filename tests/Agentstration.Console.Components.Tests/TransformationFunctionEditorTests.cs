using System.Net;
using System.Text.Json;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class TransformationFunctionEditorTests
{
    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task SavedIdentityAndLabelsFollowMetricsAndUseTheCommonRetainedForm(string locale, string identity)
    {
        using var culture = new TestCultureScope(locale);
        using var context = Context(new FunctionClient());
        var cut = Render(context);
        Assert.Contains(identity, cut.Find(".resource-overview .resource-identity-summary").TextContent);
        Assert.Contains("business:fixture", cut.Find(".resource-overview .resource-identity-summary").TextContent.Replace("\n", "").Replace(" ", ""));
        Assert.IsTrue(cut.Find(".resource-overview").InnerHtml.IndexOf("metric-card", StringComparison.Ordinal) < cut.Find(".resource-overview").InnerHtml.IndexOf("resource-identity-summary", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("form, .schema-document-editor"));
        Assert.HasCount(2, cut.FindAll("pre.runner-json"));
        await Select(cut, "definition/identity");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        var template = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreSame(template.SectionState, form.SectionState);
        Assert.AreSame(template.SectionPresentations, form.Sections);
        Assert.IsNotNull(cut.Find("[data-testid=function-identity-editor] [data-testid=labels-editor]"));
        await Select(cut, "definition/configuration");
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        await Select(cut, "yaml");
        Assert.IsTrue(cut.Find(".resource-yaml-editor textarea").HasAttribute("readonly"));
        Assert.AreEqual("other", cut.FindComponent<ResourceHeader>().Instance.Namespace);
        Assert.AreEqual("function", cut.FindComponent<ResourceHeader>().Instance.TechnicalName);
        Assert.Contains("other", cut.Find(".resource-header .resource-identity-summary").TextContent);
        Assert.Contains("function", cut.Find(".resource-header .resource-identity-summary").TextContent);
    }

    [TestMethod]
    public async Task InvalidIdentityAndOriginalEtagSurviveRefreshUntilExplicitDiscard()
    {
        var client = new FunctionClient(); using var context = Context(client); var cut = Render(context);
        await Select(cut, "definition/identity"); var originalContext = cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        cut.Find("[data-testid=function-display-name]").Input("");
        await cut.Find("form").SubmitAsync(); Assert.IsTrue(originalContext.GetValidationMessages().Any());
        client.Resource = client.Resource with { Generation = 2, Definition = client.Resource.Definition with { DisplayName = "Saved newer" } }; client.Etag = "\"newer\"";
        await Select(cut, "overview"); await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(originalContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.HasCount(1, cut.FindAll(".resource-details-feedback .form-alert-warning"));
        Assert.AreEqual("\"initial\"", cut.Find("form").GetAttribute("data-resource-etag"));
        Assert.Contains("Function fixture", cut.Find(".resource-header h1").TextContent);
        await Select(cut, "definition/identity"); Assert.AreEqual("", cut.Find("[data-testid=function-display-name]").GetAttribute("value"));
        Assert.IsTrue(originalContext.GetValidationMessages().Any());
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.AreNotSame(originalContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Saved newer", cut.Find("[data-testid=function-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"newer\"", cut.Find("form").GetAttribute("data-resource-etag")); Assert.AreEqual(0, client.Writes);
    }

    [TestMethod]
    public async Task HeaderActivationIsADraftAndMetadataSavePreservesEveryFrozenVersion()
    {
        var client = new FunctionClient(); using var context = Context(client); var cut = Render(context);
        var version = client.Resource.Definition.Versions.Single();
        cut.Find(".resource-header input[role=switch]").Change(false); Assert.AreEqual(0, client.Writes);
        await cut.Find("form").SubmitAsync();
        Assert.AreEqual(1, client.Writes); Assert.AreEqual("\"initial\"", client.LastEtag);
        Assert.IsFalse(client.Resource.Definition.Enabled);
        Assert.AreSame(version, client.Resource.Definition.Versions.Single());
        Assert.AreEqual("native-frozen", client.Resource.Definition.Versions.Single().NativeContractDigest);
        Assert.AreEqual(ResourceNamespace.Parse("other"), client.Resource.Namespace);
        Assert.AreEqual("fixture", client.Resource.Metadata.Tags["business"]);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task CreationWizardAllowsIncompleteNavigationAndRoutesErrorsWithoutWriting()
    {
        var client = new FunctionClient(); using var context = Context(client, create: true);
        var cut = context.Render<TransformationFunctionEditor>(); var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review")); await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId); Assert.AreEqual(0, client.Writes);
        var fields = cut.FindComponent<ResourceIdentityFields>().Instance;
        await cut.InvokeAsync(() => fields.DisplayNameChanged.InvokeAsync("New function"));
        await cut.InvokeAsync(() => fields.TechnicalNameChanged.InvokeAsync("new-function"));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review")); await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("configuration", wizard.CurrentStepId); Assert.AreEqual(0, client.Writes);
        cut.Find("[data-testid=function-kind]").Change(TransformationFunctionEditorModel.KindIdentity(client.Kind, "1.0.0"));
        cut.Find("[data-testid=function-implementation]").Change("json.select@1.0.0");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review")); await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual(1, client.Writes); Assert.IsNull(client.LastEtag); Assert.AreEqual("new-function", client.Resource.Name);
    }

    [TestMethod]
    public async Task IncompleteLabelsAndPolicyRemainMountedAcrossRefresh()
    {
        var client = new FunctionClient(); using var context = Context(client); var cut = Render(context);
        await Select(cut, "definition/identity"); cut.Find("[data-testid=label-value]").Input("");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance; Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        await Select(cut, "access"); var access = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => access.Parameters.Get(p => p.DraftChanged).InvokeAsync(true));
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(access, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance); Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsEmpty(cut.FindAll(".resource-details-feedback .form-alert-warning"), "An unchanged saved snapshot must not claim that a newer definition exists.");
        await Select(cut, "definition/identity"); Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("", cut.Find("[data-testid=label-value]").GetAttribute("value")); Assert.IsTrue(form.EditContext.GetValidationMessages().Any()); Assert.AreEqual(0, client.Writes);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    public async Task RefreshDistinguishesLostContentFromRecoverableFailure(HttpStatusCode status, bool retained)
    {
        var client = new FunctionClient(); using var context = Context(client); var cut = Render(context);
        await Select(cut, "definition/identity"); cut.Find("[data-testid=function-description-input]").Input("private draft fixture");
        await Select(cut, "access"); var access = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => access.Parameters.Get(p => p.DraftChanged).InvokeAsync(true));
        client.ReadError = new AgentstrationApiException("fixture error", "fixture", status);
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreEqual(retained, cut.FindAll("form").Count > 0);
        Assert.AreEqual(retained, cut.FindAll("[data-testid=function-description-input]").Any());
        Assert.AreEqual(retained, cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        if (retained) Assert.AreSame(access, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        else Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
    }

    [TestMethod]
    public async Task ARecreatedIdentityCannotKeepPrivateDefinitionOrPolicyDrafts()
    {
        var client = new FunctionClient(); using var context = Context(client); var cut = Render(context);
        await Select(cut, "definition/identity"); cut.Find("[data-testid=function-description-input]").Input("private draft fixture");
        client.Resource = client.Resource with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource); Assert.IsEmpty(cut.FindAll("form,.resource-yaml-editor"));
    }

    [TestMethod]
    public async Task ReadOnlyDefinitionsStillExposeIndependentlyAuthorizedAccess()
    {
        var client = new FunctionClient(); client.Resource = client.Resource with { Governance = ResourceGovernance.Core("functions.fixture") };
        using var context = Context(client); var cut = Render(context);
        Assert.IsTrue(cut.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
        await Select(cut, "definition/identity"); Assert.IsTrue(cut.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        Assert.IsTrue(cut.Find("form > fieldset").HasAttribute("disabled"));
        await cut.Find("form").SubmitAsync(); Assert.AreEqual(0, client.Writes);
        await Select(cut, "access"); Assert.IsNotNull(cut.FindComponent<Stub<ResourceAccessPanel>>());
    }

    [TestMethod]
    public async Task EditsMadeWhileRefreshIsPendingRetainTheirContextAndOriginalEtag()
    {
        var client = new FunctionClient(); using var context = Context(client); var cut = Render(context);
        await Select(cut, "definition/identity"); var form = cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        client.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.Find("[data-testid=function-description-input]").Input("draft entered during refresh");
        await cut.InvokeAsync(() => client.PendingRead.SetResult(new(client.Resource with { Generation = 2 }, "\"newer\""))); await refresh;
        Assert.AreSame(form, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("draft entered during refresh", cut.Find("[data-testid=function-description-input]").GetAttribute("value"));
        Assert.AreEqual("\"initial\"", cut.Find("form").GetAttribute("data-resource-etag")); Assert.AreEqual(0, client.Writes);
    }

    private static IRenderedComponent<TransformationFunctionEditor> Render(BunitContext context) => context.Render<TransformationFunctionEditor>(p => p.Add(c => c.Name, "function"));
    private static Task Select(IRenderedComponent<TransformationFunctionEditor> cut, string path) => cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection(path));
    private static BunitContext Context(FunctionClient client, bool create = false)
    {
        var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<ITransformationFunctionsClient>(client);
        context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext());
        context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var uri = create ? "http://localhost/transformation-functions/new" : "http://localhost/transformation-functions/function?namespace=other&scopeRef=%2Finstance";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/identity", "definition/configuration", "access", "yaml" })
            module.Setup<string>("navigate", i => i.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(uri + "#" + section);
        return context;
    }
    private sealed class NavigationContext : IConsoleNavigationContext
    {
        public string? ScopeRef => "/instance";
        public string AddToUri(string uri) => uri;
    }
    private sealed class FunctionClient : ITransformationFunctionsClient
    {
        public FunctionKindResource Kind { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = FunctionResourceKinds.FunctionKind,
            Uid = Guid.NewGuid(),
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "native" },
            Definition = new("Native", [new("1.0.0", "native", NativeFunctionKindContract.DefinitionSchema)])
        };
        public TransformationFunctionResource Resource { get; set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = FunctionResourceKinds.TransformationFunction,
            Uid = Guid.NewGuid(),
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "function", Namespace = ResourceNamespace.Parse("other"), Tags = new Dictionary<string, string> { ["business"] = "fixture" } },
            Definition = new("Function fixture", [new("1.0.0", new("native", "1.0.0", ResourceScopeRef.Instance, ResourceNamespace.Default), JsonSerializer.Deserialize<JsonElement>("""{"implementation":{"id":"json.select","version":"1.0.0"}}""")) { NativeContractDigest = "native-frozen" }])
        };
        public string Etag { get; set; } = "\"initial\"";
        public Exception? ReadError { get; set; }
        public TaskCompletionSource<ResourceSnapshot<TransformationFunctionResource>>? PendingRead { get; set; }
        public int Writes { get; private set; }
        public string? LastEtag { get; private set; }
        public Task<IReadOnlyList<TransformationFunctionResource>> ListAsync(ResourceScopeRef? scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<TransformationFunctionResource>>([Resource]);
        public Task<IReadOnlyList<FunctionKindResource>> ListKindsAsync(ResourceScopeRef? scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<FunctionKindResource>>([Kind]);
        public Task<IReadOnlyList<NativeFunctionDescriptor>> ImplementationsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<NativeFunctionDescriptor>>([new("json.select", "1.0.0", JsonSerializer.Deserialize<JsonElement>("{}"), JsonSerializer.Deserialize<JsonElement>("{}"))]);
        public Task<ResourceSnapshot<TransformationFunctionResource>> GetAsync(FunctionReference reference, CancellationToken ct) => ReadError is { } e ? Task.FromException<ResourceSnapshot<TransformationFunctionResource>>(e) : PendingRead?.Task ?? Task.FromResult(new ResourceSnapshot<TransformationFunctionResource>(Resource, Etag));
        public Task SaveAsync(TransformationFunctionResource resource, string? etag, CancellationToken ct) { Writes++; LastEtag = etag; Resource = resource; Etag = "\"saved\""; return Task.CompletedTask; }
        public Task<ResourceSnapshot<FunctionKindResource>> GetKindAsync(FunctionReference reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FunctionChoice>> ChoicesAsync(ResourceScopeRef scope, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveKindAsync(FunctionKindResource resource, string? etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
