using System.Globalization;
using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Triggers;
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
[DoNotParallelize]
public sealed class NamespacedAgentDetailsTests
{
    [TestMethod]
    public void BootstrapAgentRedirectsToTheEditableNamespacedEditor()
    {
        using var culture = new CultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddSingleton<IManagementApiClient>(new FakeManagementClient(packManaged: false));
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        context.Services.AddSingleton<IAgentRunnerRuntimeClient>(new FakeRuntimeClient());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        _ = context.Render<NamespacedAgentDetails>(parameters => parameters
            .Add(component => component.AgentNamespace, "agentstration.assistant")
            .Add(component => component.Name, "assistant-diagnostics"));

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.AreEqual($"http://localhost/agents/assistant-diagnostics?namespace=agentstration.assistant&scopeRef={Uri.EscapeDataString(FakeManagementClient.Scope.Value)}&context={Uri.EscapeDataString(FakeManagementClient.Scope.Value)}", navigation.Uri);
    }

    [TestMethod]
    public async Task PageShowsPackBindingsAndPreparesTheExactNamespacedAgentRevision()
    {
        using var culture = new CultureScope("en-US");
        using var context = new BunitContext().WithCollectionPresentation();
        var agents = new FakeManagementClient();
        var runtime = new FakeRuntimeClient();
        context.Services.AddSingleton<IManagementApiClient>(agents);
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        context.Services.AddSingleton<IAgentRunnerRuntimeClient>(runtime);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"http://localhost/namespaces/{FakeManagementClient.PackNamespace.Value}/agents/concierge");
        navigation.NavigateTo(navigation.GetUriWithQueryParameter(
            "returnUrl",
            "/modelprofiles/reasoning?context=/instance&tab=usages"));

        ConfigureNavigation(context);
        var rendered = context.Render<NamespacedAgentDetails>(parameters => parameters
            .Add(component => component.AgentNamespace, FakeManagementClient.PackNamespace.Value)
            .Add(component => component.Name, "concierge"));

        Assert.AreEqual(FakeManagementClient.PackNamespace, agents.RequestedNamespace);
        CollectionAssert.AreEqual(new[] { "Overview", "Configuration", "Model", "Runtime", "Access", "YAML" }, rendered.FindAll(".resource-navigation-rail a[data-resource-section]").Select(tab => tab.TextContent.Trim()).ToArray());
        Assert.AreEqual("page", rendered.Find("a[data-resource-section='overview']").GetAttribute("aria-current"));
        Assert.AreEqual(4, rendered.FindAll(".namespaced-agent-metrics .metric-card").Count);
        Assert.IsTrue(rendered.Find(".namespaced-agent-metrics").TextContent.Contains("Model profile", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find(".namespaced-agent-metrics").TextContent.Contains("Runtime profile", StringComparison.Ordinal));
        Assert.AreEqual(2, rendered.FindAll(".namespaced-agent-metrics .metric-card").Count(card => card.TextContent.Contains("Installation binding", StringComparison.Ordinal)));
        Assert.AreEqual(4, rendered.FindAll(".namespaced-agent-metrics .metric-help").Count);
        Assert.IsTrue(rendered.FindAll(".namespaced-agent-metrics .metric-tooltip").Any(tooltip => tooltip.TextContent.Contains("logical requirement", StringComparison.Ordinal)));
        Assert.IsTrue(rendered.Find(".namespaced-agent-metrics").TextContent.Contains("Not ready", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find(".namespaced-agent-metrics").TextContent.Contains("agent_deployment_not_found", StringComparison.Ordinal));
        Assert.AreEqual(
            "/modelprofiles/reasoning?context=/instance&tab=usages",
            rendered.Find("[data-testid='namespaced-agent-context-return']").GetAttribute("href"));
        Assert.IsTrue(rendered.Markup.Contains("model-reasoning", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("local-runtime", StringComparison.Ordinal));
        Assert.IsFalse(rendered.FindComponent<ResourceDetailsTemplate>().Instance.Sections.Single(item => item.Path == "definition").Navigable);
        Assert.Contains("Concierge", rendered.Find(".resource-identity-summary").TextContent);
        Assert.Contains(FakeManagementClient.PackNamespace.Value, rendered.Find(".resource-identity-summary").TextContent);
        Assert.IsEmpty(rendered.FindAll("[role=switch]"));
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        Assert.AreEqual("page", rendered.Find("a[data-resource-section='definition/configuration']").GetAttribute("aria-current"));
        Assert.IsTrue(rendered.Markup.Contains("Review the original Agent fields.", StringComparison.Ordinal));
        var definition = rendered.Find("[data-testid='pack-agent-definition-form']");
        Assert.IsTrue(definition.QuerySelectorAll("input:not([disabled]), textarea:not([disabled])").Length == 0);
        Assert.IsTrue(definition.QuerySelectorAll("textarea").Any(field => field.GetAttribute("value")?.Contains("Original instructions from the Pack.", StringComparison.Ordinal) == true));
        Assert.IsTrue(definition.InnerHtml.Contains("binding:model-reasoning", StringComparison.Ordinal));
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("overview"));

        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/model"));

        Assert.IsNull(rendered.Find("a[data-resource-section='overview']").GetAttribute("aria-current"));
        Assert.AreEqual("page", rendered.Find("a[data-resource-section='definition/model']").GetAttribute("aria-current"));
        var profileLinks = rendered.FindAll("a")
            .Where(link => link.GetAttribute("href")?.StartsWith("/modelprofiles/", StringComparison.Ordinal) == true)
            .Where(link => link.GetAttribute("data-testid") != "namespaced-agent-context-return")
            .ToArray();
        Assert.IsNotEmpty(profileLinks);
        Assert.IsTrue(profileLinks.All(link => link.GetAttribute("href") == "/modelprofiles/reasoning?namespace=shared.models"));
        Assert.IsTrue(rendered.Markup.Contains("model-reasoning", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("shared.models/reasoning", StringComparison.Ordinal));
        Assert.AreEqual(
            "/packs?publisher=agentstration&name=daily-life-assistant",
            rendered.FindAll("a").First(link => link.TextContent.Contains("Open Pack installation", StringComparison.Ordinal)).GetAttribute("href"));

        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/runtime"));

        Assert.IsNull(rendered.Find("a[data-resource-section='definition/model']").GetAttribute("aria-current"));
        Assert.AreEqual("page", rendered.Find("a[data-resource-section='definition/runtime']").GetAttribute("aria-current"));
        Assert.IsTrue(rendered.Markup.Contains("local-runtime", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("shared.platform/maf-shared", StringComparison.Ordinal));
        Assert.AreEqual(
            "/runtimeprofiles/maf-shared?namespace=shared.platform",
            rendered.FindAll("a").First(link => link.TextContent.Contains("Open effective runtime profile", StringComparison.Ordinal)).GetAttribute("href"));
        Assert.IsTrue(rendered.Markup.Contains("agentstration/daily-life-assistant", StringComparison.Ordinal));
        Assert.IsFalse(rendered.FindAll(".form-actions").Any());
        Assert.IsTrue(rendered.FindAll(".namespaced-agent-actions").Any());

        await rendered.FindAll("button").First(button => button.TextContent.Contains("Prepare generation 1", StringComparison.Ordinal)).ClickAsync(new());

        Assert.AreEqual(FakeManagementClient.PackNamespace, runtime.PreparedNamespace);
        Assert.AreEqual("concierge", runtime.PreparedAgentName);
        Assert.AreEqual(1, runtime.PreparedGeneration);
        Assert.AreEqual(FakeManagementClient.Scope, runtime.PreparedScope);
        Assert.AreEqual(FakeManagementClient.Scope, runtime.ObservedScope);
        Assert.IsTrue(rendered.Markup.Contains("Revision ready", StringComparison.Ordinal));

        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));

        Assert.AreEqual("page", rendered.Find("a[data-resource-section='definition/configuration']").GetAttribute("aria-current"));
        Assert.IsTrue(rendered.Find("[data-testid='pack-agent-definition-form']").QuerySelectorAll("input:not([disabled]), textarea:not([disabled])").Length == 0);
        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        var source = rendered.Find("[data-testid='pack-agent-yaml'] textarea");
        Assert.IsTrue(source.HasAttribute("readonly"));
        Assert.IsTrue((source.GetAttribute("value") ?? string.Empty).Contains("binding: model-reasoning", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FrenchCatalogLocalizesNamespacedAgentOperations()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var localizer = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<NamespacedAgentDetailsStrings>>();

        Assert.AreEqual("Vue d’ensemble", localizer["Tab.Overview"].Value);
        Assert.AreEqual("Liaison d’installation", localizer["InstallationBinding"].Value);
        Assert.AreEqual("Préparer la génération 4", localizer["DeployGeneration", 4].Value);
    }

    [TestMethod]
    [DataRow("en-US", "Identity", "Settings")]
    [DataRow("fr-FR", "Identité", "Paramètres")]
    public async Task ManagedAgentKeepsItsPolicyDraftAndOriginalSourceAcrossSections(string locale, string identity, string settings)
    {
        using var culture = new CultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        var client = new FakeManagementClient();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IManagementApiClient>(client);
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        context.Services.AddSingleton<IAgentRunnerRuntimeClient>(new FakeRuntimeClient());
        context.ComponentFactories.AddStub<ResourceAccessPanel>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/namespaces/{FakeManagementClient.PackNamespace.Value}/agents/concierge?scopeRef={Uri.EscapeDataString(FakeManagementClient.Scope.Value)}");
        ConfigureNavigation(context);
        var cut = context.Render<NamespacedAgentDetails>(p => p.Add(c => c.AgentNamespace, FakeManagementClient.PackNamespace.Value).Add(c => c.Name, "concierge"));
        Assert.Contains(identity, cut.Find(".resource-identity-summary").TextContent);
        Assert.Contains(settings, cut.Find(".resource-configuration-summary").TextContent);
        Assert.AreEqual(FakeManagementClient.Scope, client.RequestedScope);
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("access"));
        var panel = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        Assert.AreEqual(FakeManagementClient.Uid, panel.Parameters.Get(p => p.ResourceUid));
        await cut.InvokeAsync(() => panel.Parameters.Get(p => p.DraftChanged).InvokeAsync(true));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await cut.InvokeAsync(() => nav.NavigateSection("yaml"));
        Assert.Contains("binding: model-reasoning", cut.Find("[data-testid=pack-agent-yaml] textarea").GetAttribute("value")!);
        await cut.InvokeAsync(() => nav.NavigateSection("access"));
        Assert.AreSame(panel, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.AreEqual(1, client.Reads);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        await cut.InvokeAsync(() => panel.Parameters.Get(p => p.DraftChanged).InvokeAsync(false));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public async Task AnInheritedManagedAgentDoesNotExposeOwnerPolicyAdministration()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IManagementApiClient>(new FakeManagementClient());
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        context.Services.AddSingleton<IAgentRunnerRuntimeClient>(new FakeRuntimeClient());
        context.Services.AddSingleton<IConsoleNavigationContext>(new OwnerContext(ResourceScopeRef.Workspace(Guid.NewGuid())));
        ConfigureNavigation(context);
        var cut = context.Render<NamespacedAgentDetails>(p => p.Add(c => c.AgentNamespace, FakeManagementClient.PackNamespace.Value).Add(c => c.Name, "concierge"));
        Assert.IsEmpty(cut.FindAll("a[data-resource-section=access]"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        Assert.IsNotNull(cut.Find("[data-testid=pack-agent-definition-form]"));
        Assert.IsEmpty(cut.FindAll("[data-testid=pack-agent-definition-form] input:not([disabled]), [data-testid=pack-agent-definition-form] textarea:not([disabled])"));
    }

    private sealed class OwnerContext(ResourceScopeRef owner) : IConsoleNavigationContext
    {
        public string? ScopeRef => owner.Value;
        public string AddToUri(string uri) => uri;
    }

    private static void ConfigureNavigation(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var current = context.Services.GetRequiredService<NavigationManager>().Uri.Split('#')[0];
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var path in new[] { "overview", "definition/configuration", "definition/model", "definition/runtime", "access", "yaml" })
            module.Setup<string>("navigate", call => call.Arguments[0] is string uri && uri.EndsWith("#" + path, StringComparison.Ordinal)).SetResult(current + "#" + path);
    }

    private sealed class FakeRuntimeClient : IAgentRunnerRuntimeClient
    {
        private bool ready;
        public ResourceScopeRef? PreparedScope { get; private set; }
        public ResourceScopeRef? ObservedScope { get; private set; }
        public ResourceNamespace? PreparedNamespace { get; private set; }
        public string? PreparedAgentName { get; private set; }
        public long? PreparedGeneration { get; private set; }

        public Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(ResourceNamespace @namespace, string agentName, long generation, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRuntimeReadinessResponse(agentName, generation, ready, ready ? "Ready" : "agent_deployment_not_found", ready ? "deployment-concierge" : null, ready ? "revision-concierge" : null, null));

        public Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(ResourceNamespace @namespace, string agentName, long generation, CancellationToken cancellationToken)
        {
            ready = true;
            PreparedNamespace = @namespace;
            PreparedAgentName = agentName;
            PreparedGeneration = generation;
            return Task.FromResult(new PrepareAgentRuntimeResponse(agentName, generation, "deployment-concierge", "revision-concierge", "Ready"));
        }

        public Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, long generation, CancellationToken cancellationToken)
        {
            ObservedScope = scope;
            return GetAgentReadinessAsync(ns, name, generation, cancellationToken);
        }
        public Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, long generation, CancellationToken cancellationToken)
        {
            PreparedScope = scope;
            return PrepareAgentAsync(ns, name, generation, cancellationToken);
        }

        public Task<IReadOnlyList<ExecutionSummary>> GetExecutionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> CreateRunAsync(CreateRuntimeRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> GetRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeRun>> GetRunsAsync(string? agentResourceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeRunEvent>> GetRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<RuntimeRunEvent> ObserveRunAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> CancelRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuntimeRun> RetryRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgentRuntimeReadinessResponse> GetAgentReadinessAsync(string agentName, long generation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PrepareAgentRuntimeResponse> PrepareAgentAsync(string agentName, long generation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeManagementClient(bool packManaged = true) : IManagementApiClient
    {
        public static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        public static readonly Guid Uid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        public ResourceScopeRef? RequestedScope { get; private set; }
        public int Reads { get; private set; }
        public static readonly ResourceNamespace PackNamespace = new("agentstration.daily-life-assistant");
        private static readonly ResourceNamespace ProfileNamespace = new("shared.models");

        public ResourceNamespace RequestedNamespace { get; private set; }

        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
        {
            Reads++; RequestedNamespace = @namespace;
            var resource = new AgentResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Uid = Uid, ScopeRef = Scope,
                Kind = AgentResourceKinds.Agent,
                Metadata = new ResourceMetadata
                {
                    Name = name,
                    Namespace = @namespace,
                    Annotations = packManaged
                        ? new Dictionary<string, string>
                        {
                            [PackProvenanceAnnotations.Publisher] = "agentstration",
                            [PackProvenanceAnnotations.Name] = "daily-life-assistant",
                            [PackProvenanceAnnotations.Version] = "1.0.0"
                        }
                        : new Dictionary<string, string>()
                },
                Generation = 1,
                Governance = packManaged
                    ? ResourceGovernance.Pack("/instances/local/InstalledPack/daily-life-assistant", "agentstration/daily-life-assistant", "1.0.0")
                    : ResourceGovernance.User,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
                Definition = new AgentProperties
                {
                    DisplayName = "Concierge",
                    Instructions = "Help the user.",
                    ModelProfile = new ResourceReference("reasoning", @namespace: ProfileNamespace),
                    RuntimeProfile = new ResourceReference("maf-shared", @namespace: new ResourceNamespace("shared.platform"))
                }
            };
            return Task.FromResult(new ResourceSnapshot<AgentResource>(resource, "\"agent-etag\""));
        }

        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(ResourceNamespace ns, string name, ResourceScopeRef scope, CancellationToken cancellationToken)
        {
            RequestedScope = scope;
            return GetAgentAsync(ns, name, cancellationToken);
        }

        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePacksClient : IPacksClient
    {
        public Task<IReadOnlyList<PackProjectSourceDocument>> GetPackResourcesAsync(string publisher, string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PackProjectSourceDocument>>([new(
                "agents/concierge.yaml",
                AgentResourceKinds.Agent,
                "concierge",
                """
                apiVersion: agentstration.io/v1
                kind: Agent
                metadata:
                  name: concierge
                definition:
                  displayName: Concierge
                  description: Original source
                  handler: prompt-agent
                  instructions: Original instructions from the Pack.
                  modelProfile:
                    binding: model-reasoning
                  runtimeProfile:
                    binding: local-runtime
                  tools: []
                  behaviors: []
                  middleware: []
                  contextProviders: []
                  settings: {}
                """)]);

        public Task<ResourceSnapshot<InstalledPackResource>> GetPackAsync(string publisher, string name, CancellationToken cancellationToken)
        {
            var resource = new InstalledPackResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = PackKinds.InstalledPack,
                Metadata = new ResourceMetadata { Name = "13-agentstration-daily-life-assistant" },
                Definition = new InstalledPackProperties
                {
                    Publisher = publisher,
                    PackName = name,
                    Namespace = FakeManagementClient.PackNamespace,
                    Version = "1.0.0",
                    Source = "daily-life-assistant.pack.zip",
                    InstalledAt = DateTimeOffset.UtcNow,
                    State = InstalledPackState.Installed,
                    Bindings =
                    [
                        new PackBindingResolution(
                            "model-reasoning",
                            PackBindingTargetKind.ModelProfile,
                            new ResourceReference("reasoning", @namespace: new ResourceNamespace("shared.models"))),
                        new PackBindingResolution(
                            "local-runtime",
                            PackBindingTargetKind.RuntimeProfile,
                            new ResourceReference("maf-shared", @namespace: new ResourceNamespace("shared.platform")))
                    ]
                }
            };
            return Task.FromResult(new ResourceSnapshot<InstalledPackResource>(resource, "\"pack-etag\""));
        }

        public Task<IReadOnlyList<InstalledPackResource>> GetPacksAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PackInstallationPreview> PreviewAsync(byte[] archive, string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> InstallAsync(byte[] archive, string fileName, bool replaceExisting, bool removeDashboardReferences, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UninstallAsync(string publisher, string name, string etag, bool removeDashboardReferences, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> AttachSourceAsync(string publisher, string name, byte[] archive, string fileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<PackProjectResource>> ForkAsync(string publisher, string name, ForkPackCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackProjectResource>> GetProjectsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<PackProjectResource>> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<PackProjectResource>> UpdateProjectAsync(Guid projectId, UpdatePackProjectCommand command, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PackProjectBuildResource> BuildAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackProjectBuildResource>> GetBuildsAsync(Guid projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PackInstallationPreview> PreviewBuildAsync(Guid projectId, Guid buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<InstalledPackResource>> InstallBuildAsync(Guid projectId, Guid buildId, bool replaceExisting, IReadOnlyList<PackBindingSelection> bindings, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
