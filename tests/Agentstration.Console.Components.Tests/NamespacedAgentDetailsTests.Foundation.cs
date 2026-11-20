using System.Net;
using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.Runtime.Contracts;
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

public sealed partial class NamespacedAgentDetailsTests
{
    [TestMethod]
    [DataRow("en-US", "Labels")]
    [DataRow("fr-FR", "Étiquettes")]
    public async Task ManagedInspectionSharesTheCommonFormAndKeepsOriginalSourceDistinct(string locale, string labels)
    {
        using var culture = new CultureScope(locale);
        using var context = ManagedContext(out var client);
        var saved = (await client.GetAgentAsync(FakeManagementClient.PackNamespace, "concierge", CancellationToken.None)).Value;
        client.Replacement = saved with { Metadata = saved.Metadata with { Tags = new Dictionary<string, string> { ["team"] = "fixture" } } };
        var cut = RenderManaged(context);
        Assert.Contains(locale == "fr-FR" ? "Publié" : "Published", cut.Find(".resource-header .status-badge").TextContent);
        Assert.Contains(labels, cut.Find(".resource-identity-summary").TextContent);
        Assert.Contains("team", cut.Find(".resource-identity-summary").TextContent);
        await ManagedSectionAsync(cut, "definition/identity");
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(adapter.SectionState, form.SectionState);
        Assert.AreSame(adapter.SectionPresentations, form.Sections);
        Assert.IsTrue(form.ReadOnly);
        Assert.IsFalse(form.OnValidSubmit.HasDelegate);
        Assert.HasCount(6, adapter.SectionPresentations["definition/identity"].Fields);
        Assert.IsTrue(cut.Find("#managed-agent-inspection-form > fieldset").HasAttribute("disabled"));
        Assert.HasCount(1, cut.FindAll("form"));
        await ManagedSectionAsync(cut, "definition/configuration");
        Assert.Contains("Help the user.", cut.Find("#managed-agent-inspection-form").TextContent);
        await ManagedSectionAsync(cut, "definition/source");
        Assert.IsTrue(cut.FindAll("[data-testid=pack-agent-definition-form] textarea").Any(field => field.GetAttribute("value")?.Contains("Original instructions from the Pack.", StringComparison.Ordinal) == true));
        Assert.IsEmpty(cut.FindAll("[data-testid=pack-agent-definition-form] input:not([disabled]), [data-testid=pack-agent-definition-form] textarea:not([disabled])"));
        await ManagedSectionAsync(cut, "definition/runtime");
        Assert.IsNotNull(cut.Find(".resource-section-actions button"));
        Assert.IsNull(cut.Find(".resource-section-actions button").Closest("fieldset[disabled]"));
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar, [role=switch]"));
    }

    [TestMethod]
    public async Task ManagedRefreshRetainsOriginalSourceFormAndIndependentPolicyDraft()
    {
        using var context = ManagedContext(out var client);
        var cut = RenderManaged(context);
        await ManagedSectionAsync(cut, "definition/source");
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        var source = cut.FindComponent<PackAgentDefinitionForm>().Instance;
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await ManagedSectionAsync(cut, "access");
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(p => p.DraftChanged).InvokeAsync(true));
        client.ReadError = new AgentstrationApiException("Unavailable", "fixture", HttpStatusCode.ServiceUnavailable);

        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());

        Assert.AreEqual(ResourceLoadStatus.Failed, adapter.LoadState!.Status);
        Assert.IsTrue(adapter.LoadState.HasContent);
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreSame(source, cut.FindComponent<PackAgentDefinitionForm>().Instance);
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        client.ReadError = null;
        await cut.InvokeAsync(() => adapter.OnRetry.InvokeAsync());
        Assert.AreEqual(ResourceLoadStatus.Ready, adapter.LoadState!.Status);
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreSame(source, cut.FindComponent<PackAgentDefinitionForm>().Instance);
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task LosingManagedAgentAccessClearsOriginalSourceAndPolicy(HttpStatusCode status)
    {
        using var context = ManagedContext(out var client);
        var cut = RenderManaged(context);
        await ManagedSectionAsync(cut, "definition/source");
        await ManagedSectionAsync(cut, "yaml");
        await ManagedSectionAsync(cut, "access");
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        await cut.InvokeAsync(() => cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(p => p.DraftChanged).InvokeAsync(true));
        client.ReadError = new AgentstrationApiException("Denied", "fixture", status);

        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());

        Assert.AreEqual(status == HttpStatusCode.NotFound ? ResourceLoadStatus.NotFound : ResourceLoadStatus.Forbidden, adapter.LoadState!.Status);
        Assert.IsNull(adapter.Resource);
        Assert.DoesNotContain("Original instructions from the Pack.", cut.Markup);
        Assert.IsEmpty(cut.FindAll(".resource-form, .resource-yaml-editor, .resource-section-actions"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task AReplacedManagedUidCannotAdoptAnotherAgentsPolicyIdentity()
    {
        using var context = ManagedContext(out var client);
        var cut = RenderManaged(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        client.Replacement = ((AgentResource)adapter.Resource!) with { Uid = Guid.NewGuid() };
        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        Assert.AreEqual(ResourceLoadStatus.NotFound, adapter.LoadState!.Status);
        Assert.IsNull(adapter.Resource);
    }

    [TestMethod]
    public async Task ADelayedManagedReadCannotReplaceAnotherAgent()
    {
        using var context = ManagedContext(out var client);
        var cut = RenderManaged(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        var original = (AgentResource)adapter.Resource!;
        var pending = new TaskCompletionSource<ResourceSnapshot<AgentResource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NextRead = pending;
        var refresh = cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.AreEqual(2, client.Reads));
        client.Replacement = original with { Uid = Guid.NewGuid(), Metadata = original.Metadata with { Name = "next-agent" } };
        cut.Render(p => p.Add(c => c.Name, "next-agent"));
        cut.WaitForAssertion(() => Assert.AreEqual("next-agent", adapter.Resource!.Name));
        pending.SetResult(new(original, "\"old-etag\""));
        await refresh;
        Assert.AreEqual("next-agent", adapter.Resource!.Name);
        Assert.AreEqual(ResourceLoadStatus.Ready, adapter.LoadState!.Status);
    }

    [TestMethod]
    [DataRow("en-US", "saved, resolved Agent definition")]
    [DataRow("fr-FR", "définition enregistrée et résolue")]
    public async Task LostPackSourceAccessClearsOnlyThePrivateSourceAndRetainsTheAgentPolicy(string locale, string fallback)
    {
        using var culture = new CultureScope(locale);
        using var context = ManagedContext(out _);
        var packs = (FakePacksClient)context.Services.GetRequiredService<IPacksClient>();
        var cut = RenderManaged(context);
        await ManagedSectionAsync(cut, "definition/source");
        await ManagedSectionAsync(cut, "yaml");
        await ManagedSectionAsync(cut, "access");
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(p => p.DraftChanged).InvokeAsync(true));
        packs.ReadError = new AgentstrationApiException("Denied", "fixture", HttpStatusCode.Forbidden);

        await cut.InvokeAsync(() => adapter.OnRefresh.InvokeAsync());

        Assert.AreEqual(ResourceLoadStatus.Ready, adapter.LoadState!.Status);
        Assert.IsNotNull(adapter.Resource);
        Assert.DoesNotContain("Original instructions from the Pack.", cut.Markup);
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await ManagedSectionAsync(cut, "yaml");
        Assert.Contains(fallback, cut.Find("[data-testid=pack-agent-yaml-editor]").TextContent);
        Assert.Contains("Help the user.", cut.Find("[data-testid=pack-agent-yaml-editor] textarea").GetAttribute("value")!);
    }

    [TestMethod]
    public async Task AResourcesReaderCanInspectTheManagedDefinitionWithoutDeploymentCommands()
    {
        using var context = ManagedContext(out _, new ResourceListContextProvider
        {
            Scope = FakeManagementClient.Scope.Value,
            Permissions = new HashSet<string> { Agentstration.Identity.Contracts.AuthorizationPermissions.ResourcesRead }
        });
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var cut = RenderManaged(context);
        await ManagedSectionAsync(cut, "definition/runtime");
        Assert.IsEmpty(cut.FindAll(".resource-section-actions button"));
        Assert.IsEmpty(cut.FindAll(".resource-header .resource-action-bar button:not([data-testid=pack-agent-refresh]):not(.resource-navigation-trigger)"));
        Assert.IsNotNull(cut.Find(".resource-section-actions a"));
        Assert.IsTrue(cut.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
    }

    [TestMethod]
    public void UnavailableRuntimeInspectionIsNeutralAndDoesNotDenyTheAgentDefinition()
    {
        using var culture = new CultureScope("en-US");
        using var context = ManagedContext(out _);
        ((FakeRuntimeClient)context.Services.GetRequiredService<IAgentRunnerRuntimeClient>()).ReadError =
            new AgentstrationApiException("Runtime inspection denied", "fixture", HttpStatusCode.Forbidden);
        var cut = RenderManaged(context);
        var adapter = cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance;
        Assert.AreEqual(ResourceLoadStatus.Ready, adapter.LoadState!.Status);
        Assert.IsNotNull(adapter.Resource);
        var deployment = cut.FindComponents<MetricCard>().Select(c => c.Instance).Single(c => c.Label == "Execution revision");
        Assert.AreEqual("Unknown", deployment.Value);
        Assert.AreEqual(UiStatus.Neutral, deployment.Status);
        Assert.Contains("Runtime inspection denied", cut.Markup);
    }

    [TestMethod]
    public async Task AHomonymousPackFromAnotherOwnerCannotSupplyTheAgentsSource()
    {
        using var culture = new CultureScope("en-US");
        using var context = ManagedContext(out _);
        ((FakePacksClient)context.Services.GetRequiredService<IPacksClient>()).PackScope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var cut = RenderManaged(context);
        await ManagedSectionAsync(cut, "definition/source");
        Assert.DoesNotContain("Original instructions from the Pack.", cut.Markup);
        Assert.Contains("saved, resolved Agent definition", cut.Find("[data-testid=pack-agent-definition]").TextContent);
        Assert.IsNotNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
    }

    private static BunitContext ManagedContext(out FakeManagementClient client, ResourceListContextProvider? provider = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        client = new();
        if (provider is not null) context.WithResourceListContext(provider);
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IManagementApiClient>(client);
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        context.Services.AddSingleton<IAgentRunnerRuntimeClient>(new FakeRuntimeClient());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/namespaces/{FakeManagementClient.PackNamespace.Value}/agents/concierge?scopeRef={Uri.EscapeDataString(FakeManagementClient.Scope.Value)}");
        ConfigureNavigation(context);
        context.ComponentFactories.AddStub<ResourceAccessPanel>();
        return context;
    }
    private static IRenderedComponent<NamespacedAgentDetails> RenderManaged(BunitContext context) => context.Render<NamespacedAgentDetails>(p => p
        .Add(c => c.AgentNamespace, FakeManagementClient.PackNamespace.Value).Add(c => c.Name, "concierge"));
    private static Task ManagedSectionAsync(IRenderedComponent<NamespacedAgentDetails> cut, string path) => cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection(path));
}
