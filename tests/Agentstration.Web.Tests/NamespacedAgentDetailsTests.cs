using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class NamespacedAgentDetailsTests
{
    [TestMethod]
    public async Task PageShowsPackBindingsAndDeploysTheExactNamespacedAgent()
    {
        using var context = new BunitContext();
        var agents = new FakeManagementClient();
        var runtime = new FakeRuntimeClient();
        context.Services.AddSingleton<IManagementApiClient>(agents);
        context.Services.AddSingleton<IPacksClient>(new FakePacksClient());
        context.Services.AddSingleton<IAgentRunnerRuntimeClient>(runtime);

        var rendered = context.Render<NamespacedAgentDetails>(parameters => parameters
            .Add(component => component.AgentNamespace, FakeManagementClient.PackNamespace.Value)
            .Add(component => component.Name, "concierge"));

        Assert.AreEqual(FakeManagementClient.PackNamespace, agents.RequestedNamespace);
        CollectionAssert.AreEqual(new[] { "Overview", "Definition", "Model", "Runtime", "YAML" }, rendered.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()).ToArray());
        Assert.AreEqual("true", rendered.Find("#agent-overview-tab").GetAttribute("aria-selected"));
        Assert.AreEqual(4, rendered.FindAll(".namespaced-agent-metrics .metric-card").Count);
        Assert.IsTrue(rendered.Find(".namespaced-agent-metrics").TextContent.Contains("Model profile", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find(".namespaced-agent-metrics").TextContent.Contains("Runtime profile", StringComparison.Ordinal));
        Assert.AreEqual(2, rendered.FindAll(".namespaced-agent-metrics .metric-card").Count(card => card.TextContent.Contains("Installation binding", StringComparison.Ordinal)));
        Assert.AreEqual(4, rendered.FindAll(".namespaced-agent-metrics .metric-help").Count);
        Assert.IsTrue(rendered.FindAll(".namespaced-agent-metrics .metric-tooltip").Any(tooltip => tooltip.TextContent.Contains("logical requirement", StringComparison.Ordinal)));
        Assert.IsTrue(rendered.Find(".namespaced-agent-metrics").TextContent.Contains("Not ready", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Find(".namespaced-agent-metrics").TextContent.Contains("agent_deployment_not_found", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("model-reasoning", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("local-runtime", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("definition is read-only in the Console", StringComparison.Ordinal));

        Assert.IsFalse(rendered.FindAll("button:not([role='tab'])").Any(button => button.TextContent.Trim() == "Definition"));
        await rendered.Find("#agent-definition-tab").ClickAsync(new());
        Assert.AreEqual("true", rendered.Find("#agent-definition-tab").GetAttribute("aria-selected"));
        var definition = rendered.Find("[data-testid='pack-agent-definition-form']");
        Assert.IsTrue(definition.QuerySelectorAll("input:not([disabled]), textarea:not([disabled])").Length == 0);
        Assert.IsTrue(definition.QuerySelectorAll("textarea").Any(field => field.GetAttribute("value")?.Contains("Original instructions from the Pack.", StringComparison.Ordinal) == true));
        Assert.IsTrue(definition.InnerHtml.Contains("binding:model-reasoning", StringComparison.Ordinal));
        await rendered.Find("#agent-overview-tab").ClickAsync(new());

        await rendered.Find("#agent-model-tab").ClickAsync(new());

        Assert.AreEqual("false", rendered.Find("#agent-overview-tab").GetAttribute("aria-selected"));
        Assert.AreEqual("true", rendered.Find("#agent-model-tab").GetAttribute("aria-selected"));
        var profileLinks = rendered.FindAll("a")
            .Where(link => link.GetAttribute("href")?.Contains("modelprofiles", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.IsTrue(profileLinks.All(link => link.GetAttribute("href") == "/modelprofiles/reasoning?namespace=shared.models"));
        Assert.IsTrue(rendered.Markup.Contains("model-reasoning", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("shared.models/reasoning", StringComparison.Ordinal));
        Assert.AreEqual(
            "/packs?publisher=agentstration&name=daily-life-assistant",
            rendered.FindAll("a").First(link => link.TextContent.Contains("Open Pack installation", StringComparison.Ordinal)).GetAttribute("href"));

        await rendered.Find("#agent-runtime-tab").ClickAsync(new());

        Assert.AreEqual("false", rendered.Find("#agent-model-tab").GetAttribute("aria-selected"));
        Assert.AreEqual("true", rendered.Find("#agent-runtime-tab").GetAttribute("aria-selected"));
        Assert.IsTrue(rendered.Markup.Contains("local-runtime", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("shared.platform/maf-shared", StringComparison.Ordinal));
        Assert.AreEqual(
            "/runtimeprofiles/maf-shared?namespace=shared.platform",
            rendered.FindAll("a").First(link => link.TextContent.Contains("Open effective runtime profile", StringComparison.Ordinal)).GetAttribute("href"));
        Assert.IsTrue(rendered.Markup.Contains("agentstration/daily-life-assistant", StringComparison.Ordinal));
        Assert.IsFalse(rendered.FindAll(".form-actions").Any());
        Assert.IsTrue(rendered.FindAll(".namespaced-agent-actions").Any());

        await rendered.FindAll("button").First(button => button.TextContent.Contains("Deploy generation 1", StringComparison.Ordinal)).ClickAsync(new());

        Assert.AreEqual(FakeManagementClient.PackNamespace, runtime.PreparedNamespace);
        Assert.AreEqual("concierge", runtime.PreparedAgentName);
        Assert.AreEqual(1, runtime.PreparedGeneration);
        Assert.IsTrue(rendered.Markup.Contains("Deployment ready", StringComparison.Ordinal));

        await rendered.Find("#agent-definition-tab").ClickAsync(new());

        Assert.AreEqual("true", rendered.Find("#agent-definition-tab").GetAttribute("aria-selected"));
        Assert.IsTrue(rendered.Find("[data-testid='pack-agent-definition-form']").QuerySelectorAll("input:not([disabled]), textarea:not([disabled])").Length == 0);
        await rendered.Find("#agent-yaml-tab").ClickAsync(new());
        var source = rendered.Find("[data-testid='pack-agent-yaml'] textarea");
        Assert.IsTrue(source.HasAttribute("readonly"));
        Assert.IsTrue(source.TextContent.Contains("binding: model-reasoning", StringComparison.Ordinal));
    }

    private sealed class FakeRuntimeClient : IAgentRunnerRuntimeClient
    {
        private bool ready;
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

    private sealed class FakeManagementClient : IManagementApiClient
    {
        public static readonly ResourceNamespace PackNamespace = new("agentstration.daily-life-assistant");
        private static readonly ResourceNamespace ProfileNamespace = new("shared.models");

        public ResourceNamespace RequestedNamespace { get; private set; }

        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
        {
            RequestedNamespace = @namespace;
            var resource = new AgentResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.Agent,
                Metadata = new ResourceMetadata
                {
                    Name = name,
                    Namespace = @namespace,
                    Annotations = new Dictionary<string, string>
                    {
                        [PackProvenanceAnnotations.Publisher] = "agentstration",
                        [PackProvenanceAnnotations.Name] = "daily-life-assistant",
                        [PackProvenanceAnnotations.Version] = "1.0.0"
                    }
                },
                Generation = 1,
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
                ResourceKinds.Agent,
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
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.InstalledPack,
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
}
