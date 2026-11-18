using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SourceProviderConsoleTests
{
    [TestMethod]
    public void CreationFromTenantExtensionPrefillsItsScopeAndSourceContribution()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISourceProvidersClient>(new StubSourceProvidersClient());
        context.Services.AddSingleton<IExtensionsClient>(new StubExtensionsClient());
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new StubScopeInventoryClient());
        context.Services.AddSingleton(new NotificationState());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/sourceproviders/new?extension=tenant-extension&extensionNamespace=default&extensionScopeRef={Uri.EscapeDataString(StubScopeInventoryClient.TenantScope.Value)}&contributionId=git&displayName=Tenant%20Git");

        var rendered = context.Render<SourceProviderDetails>();

        rendered.WaitForAssertion(() =>
        {
            var selects = rendered.FindAll("select");
            Assert.HasCount(3, selects);
            Assert.AreEqual(StubScopeInventoryClient.TenantScope.Value, selects[0].GetAttribute("value"));
            StringAssert.Contains(selects[1].GetAttribute("value"), "tenant-extension");
            Assert.IsNotNull(selects[1].QuerySelector("option"));
            Assert.AreEqual("git", selects[2].GetAttribute("value"));
            Assert.IsFalse(rendered.Markup.Contains("instance-owned", StringComparison.Ordinal));
            Assert.IsFalse(rendered.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        });
        rendered.Find("[data-testid=source-provider-display-name]").Input("Modified tenant provider");
        Assert.AreEqual("modified-tenant-provider", rendered.Find("[data-testid=source-provider-name]").GetAttribute("value"));
        Assert.IsTrue(rendered.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public void ListShowsConfiguredProviderAndObservedStatus()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISourceProvidersClient>(new StubSourceProvidersClient(includeProvider: true));

        var rendered = context.Render<SourceProviders>();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Git Source Provider");
            StringAssert.Contains(rendered.Markup, "Available");
            Assert.AreEqual("/sourceproviders/git-local?namespace=default&scopeRef=%2Finstance&context=%2Finstance", rendered.Find("a.text-link").GetAttribute("href"));
        });
    }

    [TestMethod]
    public void TypingOnlyATechnicalNameIsAUserDraft()
    {
        using var context = Context(new StubSourceProvidersClient(), true);
        var cut = context.Render<SourceProviderDetails>();
        cut.Find("[data-testid=source-provider-name]").Input("manual-name");
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task WizardPreservesTheExactExtensionReferenceAndValidatesBeforeCreation()
    {
        var client = new StubSourceProvidersClient();
        using var context = Context(client, true);
        var cut = context.Render<SourceProviderDetails>();
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.AreEqual(0, client.Writes);
        cut.Find("[data-testid=source-provider-display-name]").Change("New Git provider");
        cut.Find("[data-testid=source-provider-name]").Change("new-git");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceScopeFormField>().Instance.ValueChanged.InvokeAsync(ResourceScopeRef.Instance));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("extension", wizard.CurrentStepId);
        cut.Find("[data-testid=source-provider-extension]").Change(SourceProviderEditorModel.ExtensionKey(ResourceScopeRef.Instance, ResourceNamespace.Default, "git-extension"));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual(1, client.Writes);
        Assert.AreEqual(ResourceScopeRef.Instance, client.Saved.Definition.Extension.ScopeRef);
        Assert.AreEqual("git-extension", client.Saved.Definition.Extension.Name);
        Assert.AreEqual("git", client.Saved.Definition.ContributionId);
        Assert.Contains("/sourceproviders/new-git", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task OverviewRetainsSavedValuesAndTheEditorAcrossSections(string locale, string identity)
    {
        using var culture = new TestCultureScope(locale);
        var client = new StubSourceProvidersClient();
        using var context = Context(client, false);
        var cut = context.Render<SourceProviderDetails>(p => p.Add(c => c.Name, "git-local"));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=source-provider-display-name]").Change("Draft provider");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.Contains("Git Source Provider", cut.Find(".resource-identity-summary").TextContent);
        Assert.DoesNotContain("Draft provider", cut.Find(".resource-identity-summary").TextContent);
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual(1, client.Reads);
        await cut.Find("#source-provider-resource-form").SubmitAsync();
        Assert.AreEqual(1, client.Writes);
        Assert.AreEqual("\"initial\"", client.LastEtag);
        Assert.AreEqual("Draft provider", client.Saved.Definition.DisplayName);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=source-provider-display-name]").Change("Not saved");
        await cut.InvokeAsync(() => navigation.NavigateSection("connection"));
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.AreEqual("Draft provider", cut.Find("[data-testid=source-provider-display-name]").GetAttribute("value"));
        Assert.AreEqual(1, client.Writes);
    }

    private static BunitContext Context(StubSourceProvidersClient client, bool create)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<ISourceProvidersClient>(client);
        context.Services.AddSingleton<IExtensionsClient>(new StubExtensionsClient());
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new StubScopeInventoryClient());
        context.Services.AddSingleton(new NotificationState());
        var uri = create ? "http://localhost/sourceproviders/new" : "http://localhost/sourceproviders/git-local?scopeRef=%2Finstance";
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/configuration", "definition/extension", "connection", "usages" })
        {
            var target = uri + "#" + section;
            module.Setup<string>("navigate", i => i.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
        return context;
    }

    private sealed class StubSourceProvidersClient(bool includeProvider = false) : ISourceProvidersClient
    {
        public Task<IReadOnlyList<SourceProviderSummaryResponse>> GetSourceProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceProviderSummaryResponse>>(includeProvider
                ? [new("provider-id", "git-local", "Git Source Provider", "git-extension", "default", "git", "available", null, ScopeRef: ResourceScopeRef.Instance)]
                : []);
        public SourceProviderResource Saved { get; private set; } = new()
        {
            Uid = Guid.NewGuid(), ApiVersion = ResourceApiVersions.CoreV1, Kind = SourceResourceKinds.SourceProvider,
            Metadata = new() { Name = "git-local" }, ScopeRef = ResourceScopeRef.Instance, Generation = 1,
            Definition = new() { DisplayName = "Git Source Provider", Extension = new("git-extension", ResourceScopeRef.Instance, ResourceNamespace.Default), ContributionId = "git" }
        };
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public string? LastEtag { get; private set; }
        public Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) { Reads++; return Task.FromResult(new ResourceSnapshot<SourceProviderResource>(Saved, "\"initial\"")); }
        public Task<ResourceSnapshot<SourceProviderResource>> CreateSourceProviderAsync(CreateSourceProviderRequest request, CancellationToken cancellationToken)
        {
            Writes++; Saved = Saved with { Metadata = new() { Name = request.Name, Namespace = ResourceNamespace.Parse(request.Namespace) }, ScopeRef = request.ScopeRef, Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<SourceProviderResource>(Saved, "\"created\""));
        }
        public Task<ResourceSnapshot<SourceProviderResource>> UpdateSourceProviderAsync(ResourceNamespace @namespace, string name, PutSourceProviderRequest request, string etag, CancellationToken cancellationToken)
        {
            Writes++; LastEtag = etag; Saved = Saved with { Definition = request.Properties, Generation = Saved.Generation + 1 };
            return Task.FromResult(new ResourceSnapshot<SourceProviderResource>(Saved, "\"updated\""));
        }
        public Task DeleteSourceProviderAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceProviderStatusResponse> GetStatusAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => Task.FromResult(new SourceProviderStatusResponse(name, "available", DateTimeOffset.UnixEpoch, null));
        public Task<SourceProviderUsagesResponse> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => Task.FromResult(new SourceProviderUsagesResponse([], 0));
    }

    private sealed class StubExtensionsClient : IExtensionsClient
    {
        public Task<IReadOnlyList<ExtensionResponse>> GetExtensionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionResponse>>([
                Extension("git-extension", "Git Source Provider", ResourceScopeRef.Instance),
                Extension("tenant-extension", "Tenant Git", StubScopeInventoryClient.TenantScope)
            ]);
        public Task<IReadOnlyList<ExtensionRegistrationResource>> GetRegistrationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtensionRegistrationResource>>([
                Registration("git-extension", ResourceScopeRef.Instance),
                Registration("tenant-extension", StubScopeInventoryClient.TenantScope)
            ]);
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> GetRegistrationAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> CreateRegistrationAsync(CreateExtensionRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ExtensionRegistrationResource>> UpdateRegistrationAsync(ResourceNamespace @namespace, string name, PutExtensionRegistrationRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRegistrationAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static ExtensionResponse Extension(string name, string displayName, ResourceScopeRef scopeRef) => new(
            name, "default", new("http://127.0.0.1:5290"), "available",
            new(name, displayName, "1.0.0", null), [new("source-provider", "git")], [], [], [], null, "configuration", RegistrationScopeRef: scopeRef);
        private static ExtensionRegistrationResource Registration(string name, ResourceScopeRef scopeRef) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionRegistration,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scopeRef,
            Definition = new ExtensionRegistrationProperties { DisplayName = name, Endpoint = new("http://127.0.0.1:5290") }
        };
    }

    private sealed class StubScopeInventoryClient : IResourceScopeInventoryClient
    {
        public static readonly ResourceScopeRef TenantScope = ResourceScopeRef.Tenant(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", true),
                new(TenantScope, ResourceScopeKind.Tenant, "Development", true)
            ]);
    }
}
