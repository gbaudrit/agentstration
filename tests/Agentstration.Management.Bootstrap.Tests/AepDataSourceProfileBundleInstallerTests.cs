using Agentstration.Aep.Abstractions;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Management.Bootstrap.Tests;

[TestClass]
public sealed class AepDataSourceProfileBundleInstallerTests
{
    [TestMethod]
    public async Task InstallsBundleThroughHostHandlersAndResolvesToolBindings()
    {
        var flow = new RecordingHandler("Flow");
        var profile = new RecordingHandler("DataSourceProfile");
        var installer = new AepDataSourceProfileBundleInstaller([flow, profile]);
        var toolScope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var tool = new ResourceReference("crawl", toolScope, new("extensions"));
        var bundle = new AepDataSourceProfileBundleContribution(
            "crawl4ai-web",
            "Crawl4AI Web",
            "1.0.0",
            [FlowManifest, ProfileManifest],
            RequiredTools: ["web.crawl"]);

        var result = await installer.InstallAsync(bundle, new(WorkspaceId: Guid.NewGuid()),
            new Dictionary<string, ResourceReference> { ["web.crawl"] = tool }, default);

        Assert.HasCount(2, result.Resources);
        Assert.HasCount(1, flow.Applied);
        Assert.HasCount(1, profile.Applied);
        var binding = profile.Applied[0].Definition.GetProperty("toolBindings")[0].GetProperty("tool");
        Assert.AreEqual("crawl", binding.GetProperty("Name").GetString());
        Assert.AreEqual("extensions", binding.GetProperty("Namespace").GetString());
        Assert.AreEqual(toolScope.Value, binding.GetProperty("ScopeRef").GetString());
    }

    [TestMethod]
    public async Task RejectsMissingToolBindingBeforePlanningResources()
    {
        var profile = new RecordingHandler("DataSourceProfile");
        var installer = new AepDataSourceProfileBundleInstaller([profile]);
        var bundle = new AepDataSourceProfileBundleContribution(
            "crawl4ai-web",
            "Crawl4AI Web",
            "1.0.0",
            [ProfileManifest],
            RequiredTools: ["web.crawl"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            bundle, new(WorkspaceId: Guid.NewGuid()), new Dictionary<string, ResourceReference>(), default));

        StringAssert.Contains(exception.Message, "web.crawl");
        Assert.IsEmpty(profile.Planned);
    }

    [TestMethod]
    [DataRow(BootstrapProfileScope.Instance)]
    [DataRow(BootstrapProfileScope.Tenant)]
    [DataRow(BootstrapProfileScope.Workspace)]
    public async Task PreservesExactEnrollmentScope(BootstrapProfileScope scope)
    {
        var profile = new RecordingHandler("DataSourceProfile");
        var installer = new AepDataSourceProfileBundleInstaller([profile]);
        var bundle = new AepDataSourceProfileBundleContribution(
            "crawl4ai-web", "Crawl4AI Web", "1.0.0", [ProfileManifest]);
        var target = scope switch
        {
            BootstrapProfileScope.Instance => new BootstrapApplicationTarget(),
            BootstrapProfileScope.Tenant => new BootstrapApplicationTarget(TenantId: Guid.NewGuid()),
            _ => new BootstrapApplicationTarget(WorkspaceId: Guid.NewGuid())
        };

        _ = await installer.InstallAsync(bundle, target,
            new Dictionary<string, ResourceReference>
            {
                ["web.crawl"] = new("crawl", ResourceScopeRef.Instance, ResourceNamespace.Default)
            }, default);

        Assert.AreEqual(scope, profile.Operations.Single().ProfileScope);
    }

    [TestMethod]
    public async Task RejectsManifestScopeOverride()
    {
        var profile = new RecordingHandler("DataSourceProfile");
        var installer = new AepDataSourceProfileBundleInstaller([profile]);
        var manifest = ProfileManifest.Replace("definition:", "scopeRef: /instance\ndefinition:", StringComparison.Ordinal);
        var bundle = new AepDataSourceProfileBundleContribution(
            "crawl4ai-web", "Crawl4AI Web", "1.0.0", [manifest]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            bundle, new(WorkspaceId: Guid.NewGuid()), new Dictionary<string, ResourceReference>(), default));

        StringAssert.Contains(exception.Message, "cannot override");
        Assert.IsEmpty(profile.Planned);
    }

    private const string FlowManifest = """
        apiVersion: agentstration.io/v1
        kind: Flow
        metadata: { name: crawl4ai-web-acquisition }
        definition: { displayName: Crawl4AI acquisition }
        """;

    private const string ProfileManifest = """
        apiVersion: agentstration.io/v1
        kind: DataSourceProfile
        metadata: { name: crawl4ai-web }
        definition:
          displayName: Crawl4AI Web
          toolBindings:
            - name: crawl
              capability: web.crawl
              tool: { binding: web.crawl }
        """;

    private sealed class RecordingHandler(string kind) : IBootstrapResourceHandler
    {
        public string Kind => kind;
        public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;
        public List<BootstrapResourceDocument> Planned { get; } = [];
        public List<BootstrapResourceDocument> Applied { get; } = [];
        public List<BootstrapResourceOperationContext> Operations { get; } = [];
        public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken)
        {
            Planned.Add(resource);
            Operations.Add(operation);
            return Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));
        }

        public Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            Applied.Add(resource);
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }
}
