using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Runtime.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ControlPlaneStoreHardeningTests
{
    [TestMethod]
    public async Task StoreAppliesCommonSystemStateToUnknownResourceKindWithoutAdapterSwitch()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var stored = await fixture.Store.PutAsync(new ExtensionResource
        {
            ApiVersion = "extensions.agentstration.io/v1",
            Kind = "MemoryProvider",
            Metadata = new ResourceMetadata { Name = "local-memory" },
            Definition = new ExtensionDefinition("sqlite")
        }, null, true, default);

        Assert.AreNotEqual(Guid.Empty, stored.Value.Uid);
        Assert.IsFalse(string.IsNullOrWhiteSpace(stored.ETag));
        Assert.AreEqual(stored.ETag, stored.Value.ETag);
        Assert.AreEqual(stored.ETag, stored.Value.Status.ResourceVersion);

        var loaded = await fixture.Store.GetAsync<ExtensionResource>(new ResourceKey("MemoryProvider", "local-memory"), default);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("sqlite", loaded.Value.Definition.Provider);
    }

    [TestMethod]
    public async Task MissingRequestContextCannotImplicitlyReadAcrossScopes()
    {
        await using var fixture = await StoreFixture.CreateAsync(new UnavailableRequestContext());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Store.ListAsync<ExtensionResource>("MemoryProvider", 0, 10, default));
    }

    [TestMethod]
    public async Task RuntimeResolverReturnsOnlyExecutableRuntimeViewForExactGeneration()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var agent = await fixture.Store.PutAsync(new AgentResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = "sql-expert" },
            Generation = 3,
            Definition = new AgentProperties
            {
                DisplayName = "SQL Expert",
                Instructions = "Help with SQL.",
                ModelProfile = new ResourceReference("reasoning-default")
            }
        }, null, true, default);
        var revision = await fixture.Store.CreateImmutableAsync(new AgentRevision
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentRevision,
            Metadata = new ResourceMetadata { Name = "sql-expert--000003" },
            AgentUid = agent.Value.Uid,
            AgentName = "sql-expert",
            AgentVersion = 3,
            DefinitionHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
            ProvisioningState = ProvisioningState.Succeeded,
            Definition = Definition(agent.Value.Uid)
        }, default);
        var deployment = await fixture.Store.PutAsync(new AgentDeployment
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentDeployment,
            Metadata = new ResourceMetadata { Name = "sql-expert--g000003" },
            RevisionName = revision.Value.Metadata.Name,
            AgentName = "sql-expert",
            ModelProfileName = "reasoning-default",
            RuntimeProfileName = "maf-default",
            Environment = "local",
            HostingMode = AgentHostingMode.InProcess,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Ready,
            UpdatedAt = DateTimeOffset.UtcNow
        }, null, true, default);

        var resolver = new ControlPlaneRuntimeAgentResolver(fixture.Store, fixture.Queries);
        var resolved = await resolver.ResolveAsync(new RuntimeAgentReference("sql-expert", 3), default);

        Assert.AreEqual(agent.Value.Uid, resolved.AgentId);
        Assert.AreEqual(deployment.Value.Uid.ToString("N"), resolved.DeploymentId);
        Assert.AreEqual("reasoning-default", resolved.ModelProfileName);
        Assert.IsTrue(resolved.Ready);
        var exception = await Assert.ThrowsExactlyAsync<RuntimeAgentResolutionException>(() =>
            resolver.ResolveAsync(new RuntimeAgentReference("sql-expert", 2), default));
        Assert.AreEqual("agent_version_not_found", exception.Code);
    }

    [TestMethod]
    public async Task ConcurrentCreationCannotAllocateTheSameAgentRevisionTwice()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var agentId = Guid.NewGuid();
        var revision = new AgentRevision
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentRevision,
            Metadata = new ResourceMetadata { Name = "sql-expert--000003" },
            AgentUid = agentId,
            AgentName = "sql-expert",
            AgentVersion = 3,
            DefinitionHash = "hash",
            CreatedAt = DateTimeOffset.UnixEpoch,
            ProvisioningState = ProvisioningState.Succeeded,
            Definition = Definition(agentId)
        };

        var attempts = await Task.WhenAll(CreateAsync(), CreateAsync());

        Assert.AreEqual(1, attempts.Count(succeeded => succeeded));
        Assert.HasCount(1, await fixture.Store.ListAsync<AgentRevision>(ResourceKinds.AgentRevision, 0, 10, default));

        async Task<bool> CreateAsync()
        {
            try
            {
                _ = await fixture.Store.CreateImmutableAsync(revision, default);
                return true;
            }
            catch (ControlPlaneConcurrencyException)
            {
                return false;
            }
        }
    }

    private static ResolvedAgentDefinition Definition(Guid agentId) => new()
    {
        AgentId = agentId,
        AgentKey = "sql-expert",
        DisplayName = "SQL Expert",
        Description = "SQL specialist",
        AgentVersion = 3,
        EffectiveInstructions = "Help with SQL.",
        ModelProfileName = "reasoning-default",
        RuntimeProfileName = "maf-default",
        EffectiveToolNames = [],
        MiddlewareIds = [],
        ContextProviderIds = [],
        Capabilities = [],
        Handler = "prompt-agent",
        DefinitionHash = "hash"
    };

    private sealed record ExtensionDefinition(string Provider);

    private sealed record ExtensionResource : Resource
    {
        public ExtensionDefinition Definition { get; init; } = null!;
    }

    private sealed class UnavailableRequestContext : ICurrentRequestContext
    {
        public bool IsInitialized => false;
        public RequestContext Current => throw new InvalidOperationException("No request context is available.");
    }

    private sealed class StoreFixture(
        string directory,
        ServiceProvider provider,
        IControlPlaneStore store,
        IAgentResourceQueries queries) : IAsyncDisposable
    {
        public IControlPlaneStore Store { get; } = store;
        public IAgentResourceQueries Queries { get; } = queries;

        public static async Task<StoreFixture> CreateAsync(ICurrentRequestContext? context = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "agentstration-store-hardening", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            if (context is not null) services.AddSingleton<ICurrentRequestContext>(context);
            services.AddSqliteControlPlane($"Data Source={Path.Combine(directory, "management.db")}");
            var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<IControlPlaneStore>();
            await store.InitializeAsync(default);
            return new StoreFixture(directory, provider, store, provider.GetRequiredService<IAgentResourceQueries>());
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }

        private ServiceProvider Provider { get; } = provider;
    }
}
