using Agentstration.ResourceManagement.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components;
using Microsoft.AspNetCore.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class RuntimeProfileEditorTests
{
    private static readonly Guid ResourceUid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly ResourceScopeRef TenantScope = ResourceScopeRef.Tenant(
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    [TestMethod]
    public async Task ExistingProfileExposesLocalizedAccessManagement()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton<IRuntimeProfilesClient>(new RuntimeProfilesClientStub());
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ScopeInventoryClientStub());
        var policies = new AccessPolicyClientStub();
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);

        ConfigureNavigation(context);
        var rendered = context.Render<RuntimeProfileEditor>(parameters => parameters
            .Add(component => component.Name, "maf-builtin"));

        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Accès", rendered.Find("a[data-resource-section=access]").TextContent.Trim());
            StringAssert.Contains(rendered.Find(".access-identity").TextContent, ResourceUid.ToString());
            Assert.IsNotNull(rendered.Find("[data-testid=runtime-profile-form]").Closest("[hidden]"));
        });

        rendered.Find("[data-testid='resource-access-target']").Change(TenantScope.Value);
        rendered.Find("[data-testid='resource-access-add']").Click();
        rendered.Find("[data-testid='resource-access-save']").Click();

        rendered.WaitForAssertion(() =>
        {
            var grant = policies.Saved?.Grants.Single();
            Assert.IsNotNull(grant);
            Assert.AreEqual(TenantScope, grant.TargetScopeRef);
        });
    }

    [TestMethod]
    [DataRow("en-US", "Runtime options must be a JSON object.")]
    [DataRow("fr-FR", "Les options d’exécution doivent former un objet JSON.")]
    public async Task WizardKeepsInvalidOptionsAndValidatesBeforeCreating(string locale, string invalid)
    {
        using var culture = new TestCultureScope(locale);
        var client = new RuntimeProfilesClientStub();
        using var context = Context(client, true);
        var cut = context.Render<RuntimeProfileEditor>();
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        cut.Find("[data-testid=runtime-profile-display-name]").Change("My runtime");
        cut.Find("[data-testid=runtime-profile-name]").Change("my-runtime");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceScopeFormField>().Instance.ValueChanged.InvokeAsync(ResourceScopeRef.Instance));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("options"));
        cut.Find("textarea").Change("{");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("options", wizard.CurrentStepId);
        Assert.AreEqual(0, client.Writes);
        Assert.Contains(invalid, cut.Find(".validation-message").TextContent);
        Assert.AreEqual("{", cut.Find("textarea").GetAttribute("value"));
        cut.Find("textarea").Change("{\"trace\":true}");
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual(1, client.Writes);
        Assert.IsTrue(client.Profile.Definition.RuntimeOptions["trace"].GetBoolean());
        Assert.Contains("/runtimeprofiles/my-runtime", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    [DataRow("en-US", "Identity")]
    [DataRow("fr-FR", "Identité")]
    public async Task OverviewRetainsTheEditorRoutesInvalidHiddenOptionsAndPreservesEtag(string locale, string identity)
    {
        using var culture = new TestCultureScope(locale);
        var client = new RuntimeProfilesClientStub();
        using var context = Context(client, false);
        var cut = context.Render<RuntimeProfileEditor>(p => p.Add(c => c.Name, "maf-builtin"));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/options"));
        cut.Find("textarea").Change("{");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.Contains("Microsoft Agent Framework", cut.Find(".resource-identity-summary").TextContent);
        await cut.Find("#runtime-profile-resource-form").SubmitAsync();
        Assert.AreEqual("definition/options", navigation.CurrentPath);
        Assert.HasCount(1, cut.FindAll(".validation-message"));
        Assert.AreEqual(0, client.Writes);
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.IsEmpty(cut.FindAll(".validation-message"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=runtime-profile-display-name]").Change("Updated runtime");
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        Assert.DoesNotContain("Updated runtime", cut.Find(".resource-identity-summary").TextContent);
        await cut.Find("#runtime-profile-resource-form").SubmitAsync();
        Assert.AreEqual(1, client.Writes);
        Assert.AreEqual("\"runtime-profile\"", client.LastEtag);
        Assert.AreEqual("Updated runtime", client.Profile.Definition.DisplayName);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void ManagedDefinitionsAndInheritedViewsKeepAccessCapabilitiesSeparate(bool managed, bool inherited)
    {
        using var context = Context(new RuntimeProfilesClientStub(managed), false, inherited ? TenantScope : ResourceScopeRef.Instance);
        var cut = context.Render<RuntimeProfileEditor>(p => p.Add(c => c.Name, "maf-builtin"));
        Assert.AreEqual(managed || inherited, cut.Find("[data-testid=runtime-profile-definition-fields]").HasAttribute("disabled"));
        Assert.AreEqual(inherited ? 0 : 1, cut.FindAll("a[data-resource-section=access]").Count);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task AccessDraftSurvivesSectionChangesAndSavingANewDefinitionGeneration()
    {
        var client = new RuntimeProfilesClientStub();
        var policies = new AccessPolicyClientStub();
        using var context = Context(client, false, policies: policies);
        var cut = context.Render<RuntimeProfileEditor>(p => p.Add(c => c.Name, "maf-builtin"));
        var navigation = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => navigation.NavigateSection("definition/configuration"));
        cut.Find("[data-testid=runtime-profile-display-name]").Change("Updated runtime");
        await cut.InvokeAsync(() => navigation.NavigateSection("access"));
        var panel = cut.FindComponent<ResourceAccessPanel>().Instance;
        cut.Find("[data-testid=resource-access-target]").Change(TenantScope.Value);
        cut.Find("[data-testid=resource-access-add]").Click();
        await cut.InvokeAsync(() => navigation.NavigateSection("overview"));
        await cut.Find("#runtime-profile-resource-form").SubmitAsync();
        Assert.AreEqual(3L, client.Profile.Generation);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        await cut.InvokeAsync(() => navigation.NavigateSection("access"));
        Assert.AreSame(panel, cut.FindComponent<ResourceAccessPanel>().Instance);
        Assert.HasCount(1, cut.FindAll("[data-testid=resource-access-grant]"));
        Assert.IsNull(policies.Saved);
        cut.Find("[data-testid=resource-access-save]").Click();
        Assert.AreEqual(TenantScope, policies.Saved!.Grants.Single().TargetScopeRef);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    private sealed class NavigationContext(ResourceScopeRef scope) : IConsoleNavigationContext
    {
        public string? ScopeRef => scope.Value;
        public string AddToUri(string uri) => uri;
    }

    private static BunitContext Context(RuntimeProfilesClientStub client, bool create, ResourceScopeRef? contextScope = null, AccessPolicyClientStub? policies = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton<IRuntimeProfilesClient>(client);
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ScopeInventoryClientStub());
        if (contextScope is { } scope) context.Services.AddSingleton<IConsoleNavigationContext>(new NavigationContext(scope));
        if (policies is not null) context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(create ? "/runtimeprofiles/new" : "/runtimeprofiles/maf-builtin");
        ConfigureNavigation(context);
        return context;
    }
    private static void ConfigureNavigation(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        if (navigation.Uri == "http://localhost/") navigation.NavigateTo("/runtimeprofiles/maf-builtin");
        var uri = navigation.Uri.Split('#')[0];
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var section in new[] { "overview", "definition/configuration", "definition/execution", "definition/options", "deployments", "access" })
        {
            var target = uri + "#" + section;
            module.Setup<string>("navigate", i => i.Arguments[0] is string value && value.EndsWith("#" + section, StringComparison.Ordinal)).SetResult(target);
        }
    }

    private sealed class RuntimeProfilesClientStub : IRuntimeProfilesClient
    {
        public RuntimeProfilesClientStub(bool managed = false) { if (managed) Profile = Profile with { Governance = ResourceGovernance.Core("runtime") }; }
        public RuntimeProfileResource Profile { get; private set; } = new()
        {
            Uid = ResourceUid,
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = RuntimeProfileResourceKinds.RuntimeProfile,
            Metadata = new ResourceMetadata { Name = "maf-builtin" },
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 2,
            Definition = new()
            {
                DisplayName = "Microsoft Agent Framework",
                RuntimeType = "microsoft-agent-framework"
            }
        };

        public Task<IReadOnlyList<RuntimeProfileSummaryResponse>> GetRuntimeProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RuntimeProfileSummaryResponse>>([]);

        public Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(string profileName, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<RuntimeProfileResource>(Profile, "\"runtime-profile\""));

        public Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(string profileName, CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeProfileUsagesResponse([], 0));

        public int Writes { get; private set; }
        public string? LastEtag { get; private set; }
        public Task<ResourceSnapshot<RuntimeProfileResource>> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken)
        {
            Writes++; Profile = Profile with { Metadata = new() { Name = request.Name, Namespace = ResourceNamespace.Parse(request.Namespace) }, ScopeRef = request.ScopeRef, Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<RuntimeProfileResource>(Profile, "\"created\""));
        }
        public Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken)
        {
            Writes++; LastEtag = etag; Profile = Profile with { Definition = request.Properties, Generation = Profile.Generation + 1 };
            return Task.FromResult(new ResourceSnapshot<RuntimeProfileResource>(Profile, "\"updated\""));
        }
        public Task DeleteRuntimeProfileAsync(string profileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ScopeInventoryClientStub : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ResourceScopeInventoryResponse([
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", null, false,
                    [new(ResourceUid, ResourceNamespace.Default, RuntimeProfileResourceKinds.RuntimeProfile, "maf-builtin", DateTimeOffset.UnixEpoch)]),
                new(TenantScope, ResourceScopeKind.Tenant, "Development", ResourceScopeRef.Instance, true, [])
            ]));

        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", true)
            ]);
    }

    private sealed class AccessPolicyClientStub : IResourceAccessPolicyClient
    {
        public Task<ResourceAccessDecision> EvaluateAsync(EvaluateResourceAccessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceAccessDecision(false, ResourceAccessDecisionReasons.GrantMissing));
        public ResourceAccessPolicy? Saved { get; private set; }

        public Task<ResourceAccessPolicySnapshot?> GetAsync(Guid resourceUid, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceAccessPolicySnapshot?>(null);

        public Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceAccessPolicySnapshot(Saved = policy, "\"policy\""));
    }
}
