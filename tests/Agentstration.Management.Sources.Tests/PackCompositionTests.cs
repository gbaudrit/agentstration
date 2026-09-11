using System.IO.Compression;
using System.Text.Json;
using Agentstration.Infrastructure.Packs;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class PackCompositionTests
{
    [TestMethod]
    public async Task PreviewClosesDependenciesAndCreatesModelBinding()
    {
        var catalog = new CatalogStub();
        var service = new PackCompositionService(null!, null!, null!, catalog, TimeProvider.System);

        var preview = await service.PreviewAsync(new([CatalogStub.Entry]), default);

        Assert.IsTrue(preview.CanCreate);
        CollectionAssert.AreEquivalent(
            new[] { "Entry/main-entry", "Flow/main-flow", "Agent/concierge" },
            preview.Resources.Select(resource => $"{resource.Resource.Kind}/{resource.Resource.Name}").ToArray());
        Assert.AreEqual(1, preview.Resources.Count(resource => resource.ExplicitlySelected));
        Assert.AreEqual(1, preview.Bindings.Count);
        Assert.AreEqual("model-reasoning", preview.Bindings[0].Name);
        Assert.AreEqual(PackBindingTargetKind.ModelProfile, preview.Bindings[0].TargetKind);
        Assert.AreEqual("concierge", preview.Bindings[0].UsedBy.Single().Name);
    }

    [TestMethod]
    public async Task CreateProjectStoresImmutableWorkspaceSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-pack-composition-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var provider = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>()
            .AddSqliteControlPlane($"Data Source={Path.Combine(directory, "management.db")};Pooling=False")
            .BuildServiceProvider();
        try
        {
            var store = provider.GetRequiredService<IControlPlaneStore>();
            await store.InitializeAsync(default);
            var artifacts = new FileSystemPackArtifactStore(Path.Combine(directory, "artifacts"));
            var service = new PackCompositionService(store, artifacts, new ZipPackArchiveReader(), new CatalogStub(), TimeProvider.System);

            var project = await service.CreateProjectAsync(new()
            {
                Publisher = "local",
                Name = "workspace-assistant",
                Version = "0.1.0",
                DisplayName = "Workspace Assistant",
                Resources = [CatalogStub.Entry]
            }, default);

            Assert.AreEqual(PackProjectSourceKind.WorkspaceSnapshot, project.Value.Definition.SourceKind);
            Assert.IsNull(project.Value.Definition.Origin);
            Assert.HasCount(3, project.Value.Definition.SourceResources);
            Assert.AreEqual(1, project.Value.Definition.SourceResources.Count(resource => resource.ExplicitlySelected));
            await using var source = await artifacts.OpenReadAsync(project.Value.Definition.SourceArtifact, default);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            Assert.IsNotNull(archive.GetEntry("pack.json"));
            Assert.IsNotNull(archive.GetEntry("agents/concierge.json"));
            using var agentDocument = await JsonDocument.ParseAsync(archive.GetEntry("agents/concierge.json")!.Open());
            Assert.AreEqual("model-reasoning", agentDocument.RootElement.GetProperty("definition").GetProperty("modelProfile").GetProperty("binding").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class CatalogStub : IPackWorkspaceResourceCatalog
    {
        public static readonly PackCompositionResourceKey Entry = new(ResourceKinds.Entry, "main-entry");
        private static readonly PackCompositionResourceKey Flow = new(ResourceKinds.Flow, "main-flow");
        private static readonly PackCompositionResourceKey Agent = new(ResourceKinds.Agent, "concierge");
        private static readonly PackCompositionResourceKey Model = new(ResourceKinds.ModelProfile, "reasoning");

        private static readonly IReadOnlyDictionary<ResourceAddress, PackCompositionResourceSnapshot> Values = new Dictionary<ResourceAddress, PackCompositionResourceSnapshot>
        {
            [Entry.Address] = Snapshot(Entry, "Main Entry", [Include(Flow, "flow")]),
            [Flow.Address] = Snapshot(Flow, "Main Flow", [Include(Agent, "agent")]),
            [Agent.Address] = Snapshot(Agent, "Concierge", [new() { Target = Model, Relationship = "modelProfile", Mode = PackCompositionDependencyMode.Binding, BindingTargetKind = PackBindingTargetKind.ModelProfile }]),
            [Model.Address] = new(new() { Resource = Model, DisplayName = "Reasoning", Availability = PackCompositionAvailability.BindingOnly }, [])
        };

        public Task<IReadOnlyList<PackCompositionCatalogItem>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PackCompositionCatalogItem>>(Values.Values.Select(value => value.Resource).ToArray());

        public Task<PackCompositionResourceSnapshot?> GetAsync(PackCompositionResourceKey resource, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(resource.Address));

        public Task<JsonElement> ExportAsync(PackCompositionResourceKey resource, IReadOnlyDictionary<ResourceAddress, string> bindings, CancellationToken cancellationToken)
        {
            object manifest = resource.Kind switch
            {
                ResourceKinds.Agent => new { apiVersion = ManagementApiVersions.CoreV1, kind = ResourceKinds.Agent, metadata = new { name = resource.Name }, definition = new { modelProfile = new { binding = bindings[Model.Address] } } },
                _ => new { apiVersion = ManagementApiVersions.CoreV1, kind = resource.Kind, metadata = new { name = resource.Name }, definition = new { } }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(manifest));
        }

        private static PackCompositionResourceSnapshot Snapshot(PackCompositionResourceKey key, string displayName, IReadOnlyList<PackCompositionDependency> dependencies) =>
            new(new() { Resource = key, DisplayName = displayName, DependencyCount = dependencies.Count }, dependencies);
        private static PackCompositionDependency Include(PackCompositionResourceKey target, string relationship) => new() { Target = target, Relationship = relationship };
    }
}
