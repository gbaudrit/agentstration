using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.Resources;
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
        await using var fixture = await StoreFixture.CreateAsync(new SystemOperationRequestContext());
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
        await using var fixture = await StoreFixture.CreateAsync();
        Assert.IsInstanceOfType<UnavailableRequestContext>(fixture.RequestContext);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Store.ListAsync<ExtensionResource>("MemoryProvider", 0, 10, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Store.PutAsync(Resource("blocked"), null, true, default));
    }

    [TestMethod]
    public async Task WorkspaceContextScopesReadsAndWritesWhileExplicitSystemContextIsGlobal()
    {
        var tenantId = Guid.NewGuid();
        var firstWorkspaceId = Guid.NewGuid();
        var secondWorkspaceId = Guid.NewGuid();
        var context = new TestRequestContext();
        context.UseSystem();
        await using var fixture = await StoreFixture.CreateAsync(context);
        await fixture.Store.PutAsync(Resource("first", tenantId, firstWorkspaceId), null, true, default);
        await fixture.Store.PutAsync(Resource("second", tenantId, secondWorkspaceId), null, true, default);

        context.UseWorkspace(new RequestContext(Guid.NewGuid(), tenantId, firstWorkspaceId));
        var scoped = await fixture.Store.ListAllAsync<ExtensionResource>("MemoryProvider", default);
        CollectionAssert.AreEqual(new[] { "first" }, scoped.Select(value => value.Value.Metadata.Name).ToArray());
        Assert.IsNull(await fixture.Store.GetAsync<ExtensionResource>(new ResourceKey("MemoryProvider", "second"), default));
        var workspaceOwned = await fixture.Store.PutAsync(Resource("workspace-owned"), null, true, default);
        Assert.AreEqual(tenantId, workspaceOwned.Value.TenantId);
        Assert.AreEqual(firstWorkspaceId, workspaceOwned.Value.WorkspaceId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Store.PutAsync(Resource("cross-scope", tenantId, secondWorkspaceId), null, true, default));

        context.UseSystem();
        Assert.HasCount(3, await fixture.Store.ListAllAsync<ExtensionResource>("MemoryProvider", default));
    }

    [TestMethod]
    public async Task ExactScopesAllowHomonymousResourcesAndKeepUidAsPhysicalIdentity()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var workspaceA = Guid.NewGuid();
        await using var fixture = await StoreFixture.CreateAsync(new SystemOperationRequestContext());
        var scopes = new[]
        {
            ResourceScope.Instance,
            ResourceScope.Tenant(tenantA),
            ResourceScope.Tenant(tenantB),
            ResourceScope.Workspace(tenantA, workspaceA)
        };

        var stored = new List<StoredResource<ExtensionResource>>();
        foreach (var scope in scopes)
            stored.Add(await fixture.Store.PutExactAsync(scope, Resource("shared"), null, true, default));

        Assert.AreEqual(4, stored.Select(value => value.Value.Uid).Distinct().Count());
        CollectionAssert.AreEqual(scopes, stored.Select(value => value.Value.OwnershipScope).ToArray());
        foreach (var value in stored)
        {
            var byAddress = await fixture.Store.GetExactAsync<ExtensionResource>(new ScopedResourceAddress(value.Value.OwnershipScope, ResourceNamespace.Default, "MemoryProvider", "shared"), default);
            var byUid = await fixture.Store.GetByUidAsync<ExtensionResource>(value.Value.Uid, default);
            Assert.AreEqual(value.Value.Uid, byAddress?.Value.Uid);
            Assert.AreEqual(value.Value.Uid, byUid?.Value.Uid);
        }
    }

    [TestMethod]
    public async Task VisibleQueriesAreDownwardOnlyAndDoNotMergeOrShadowHomonyms()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        await using var fixture = await StoreFixture.CreateAsync(new SystemOperationRequestContext());
        await fixture.Store.PutExactAsync(ResourceScope.Instance, Resource("shared"), null, true, default);
        await fixture.Store.PutExactAsync(ResourceScope.Tenant(tenantA), Resource("shared"), null, true, default);
        await fixture.Store.PutExactAsync(ResourceScope.Tenant(tenantB), Resource("shared"), null, true, default);
        await fixture.Store.PutExactAsync(ResourceScope.Workspace(tenantA, workspaceA), Resource("shared"), null, true, default);
        await fixture.Store.PutExactAsync(ResourceScope.Workspace(tenantA, workspaceB), Resource("shared"), null, true, default);

        var visible = await fixture.Store.ListVisibleAsync<ExtensionResource>(ResourceScope.Workspace(tenantA, workspaceA), "MemoryProvider", 0, 10, default);

        CollectionAssert.AreEquivalent(
            new[] { ResourceScope.Instance.Key, ResourceScope.Tenant(tenantA).Key, ResourceScope.Workspace(tenantA, workspaceA).Key },
            visible.Select(value => value.Value.OwnershipScope.Key).ToArray());
        Assert.IsTrue(visible.All(value => value.Value.Name == "shared"));
    }

    [TestMethod]
    public async Task TenantContextCanReadAncestorsButCannotCrossTenantOrWriteAnotherScope()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var context = new TestRequestContext();
        context.UseSystem();
        await using var fixture = await StoreFixture.CreateAsync(context);
        var instance = await fixture.Store.PutExactAsync(ResourceScope.Instance, Resource("instance"), null, true, default);
        var own = await fixture.Store.PutExactAsync(ResourceScope.Tenant(tenantA), Resource("own"), null, true, default);
        var foreign = await fixture.Store.PutExactAsync(ResourceScope.Tenant(tenantB), Resource("foreign"), null, true, default);

        context.UseTenant(Guid.NewGuid(), tenantA);
        var visible = await fixture.Store.ListVisibleAsync<ExtensionResource>(ResourceScope.Tenant(tenantA), "MemoryProvider", 0, 10, default);
        CollectionAssert.AreEquivalent(new[] { "instance", "own" }, visible.Select(value => value.Value.Name).ToArray());
        Assert.AreEqual(instance.Value.Uid, (await fixture.Store.GetByUidAsync<ExtensionResource>(instance.Value.Uid, default))?.Value.Uid);
        Assert.AreEqual(own.Value.Uid, (await fixture.Store.GetByUidAsync<ExtensionResource>(own.Value.Uid, default))?.Value.Uid);
        Assert.IsNull(await fixture.Store.GetByUidAsync<ExtensionResource>(foreign.Value.Uid, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Store.PutExactAsync(ResourceScope.Instance, Resource("blocked"), null, true, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Store.GetExactAsync<ExtensionResource>(new ScopedResourceAddress(ResourceScope.Tenant(tenantB), ResourceNamespace.Default, "MemoryProvider", "foreign"), default));
    }

    [TestMethod]
    public async Task ExistingUidCannotMoveToAnotherOwnershipScope()
    {
        var tenantId = Guid.NewGuid();
        await using var fixture = await StoreFixture.CreateAsync(new SystemOperationRequestContext());
        var stored = await fixture.Store.PutExactAsync(ResourceScope.Instance, Resource("fixed"), null, true, default);

        await Assert.ThrowsExactlyAsync<ControlPlaneConcurrencyException>(() => fixture.Store.PutExactAsync(
            ResourceScope.Tenant(tenantId),
            Resource("fixed") with { Uid = stored.Value.Uid },
            stored.ETag,
            false,
            default));
    }

    [TestMethod]
    public async Task RuntimeResolverReturnsOnlyExecutableRuntimeViewForExactGeneration()
    {
        await using var fixture = await StoreFixture.CreateAsync(new SystemOperationRequestContext());
        var packNamespace = new ResourceNamespace("agentstration.sample-pack");
        var agent = await fixture.Store.PutAsync(new AgentResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = "sql-expert", Namespace = packNamespace },
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
            Metadata = new ResourceMetadata { Name = "sql-expert--000003", Namespace = packNamespace },
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
            Metadata = new ResourceMetadata { Name = "sql-expert--g000003", Namespace = packNamespace },
            RevisionName = revision.Value.Metadata.Name,
            AgentName = "sql-expert",
            ModelProfileName = "reasoning-default",
            RuntimeProfileName = "maf-builtin",
            Environment = "local",
            HostingMode = AgentHostingMode.InProcess,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Ready,
            UpdatedAt = DateTimeOffset.UtcNow
        }, null, true, default);

        var resolver = new ControlPlaneRuntimeAgentResolver(fixture.Store, fixture.Queries);
        var resolved = await resolver.ResolveAsync(new RuntimeAgentReference("sql-expert", 3) { Namespace = packNamespace }, default);

        Assert.AreEqual(agent.Value.Uid, resolved.AgentId);
        Assert.AreEqual(deployment.Value.Uid.ToString("N"), resolved.DeploymentId);
        Assert.AreEqual("reasoning-default", resolved.ModelProfileName);
        Assert.AreEqual(packNamespace, resolved.ModelProfileNamespace);
        Assert.IsTrue(resolved.Ready);
        var missingAgent = await Assert.ThrowsExactlyAsync<RuntimeAgentResolutionException>(() =>
            resolver.ResolveAsync(new RuntimeAgentReference("missing-agent", 1) { Namespace = packNamespace }, default));
        Assert.AreEqual("agent_not_found", missingAgent.Code);
        var exception = await Assert.ThrowsExactlyAsync<RuntimeAgentResolutionException>(() =>
            resolver.ResolveAsync(new RuntimeAgentReference("sql-expert", 2) { Namespace = packNamespace }, default));
        Assert.AreEqual("agent_version_not_found", exception.Code);

        await fixture.Store.CreateImmutableAsync(new AgentRevision
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.AgentRevision,
            Metadata = new ResourceMetadata { Name = "sql-expert--000004", Namespace = packNamespace },
            AgentUid = agent.Value.Uid,
            AgentName = "sql-expert",
            AgentVersion = 4,
            DefinitionHash = "hash-4",
            CreatedAt = DateTimeOffset.UtcNow,
            ProvisioningState = ProvisioningState.Succeeded,
            Definition = Definition(agent.Value.Uid) with { AgentVersion = 4 }
        }, default);
        var missingDeployment = await Assert.ThrowsExactlyAsync<RuntimeAgentResolutionException>(() =>
            resolver.ResolveAsync(new RuntimeAgentReference("sql-expert", 4) { Namespace = packNamespace }, default));
        Assert.AreEqual("deployment_not_found", missingDeployment.Code);
    }

    [TestMethod]
    public async Task ConcurrentCreationCannotAllocateTheSameAgentRevisionTwice()
    {
        await using var fixture = await StoreFixture.CreateAsync(new SystemOperationRequestContext());
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
        RuntimeProfileName = "maf-builtin",
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

    private static ExtensionResource Resource(string name, Guid tenantId = default, Guid workspaceId = default) => new()
    {
        ApiVersion = "extensions.agentstration.io/v1",
        Kind = "MemoryProvider",
        Metadata = new ResourceMetadata { Name = name },
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        Definition = new ExtensionDefinition("sqlite")
    };

    private sealed class TestRequestContext : ICurrentRequestContext
    {
        private RequestContext? current;
        public bool IsInitialized => AccessMode == ControlPlaneAccessMode.Workspace;
        public ControlPlaneAccessMode AccessMode { get; private set; } = ControlPlaneAccessMode.Unavailable;
        public RequestContext Current => current ?? throw new InvalidOperationException("No workspace context is active.");
        public void UseSystem() { current = null; AccessMode = ControlPlaneAccessMode.System; }
        public void UseTenant(Guid principalId, Guid tenantId) { current = new RequestContext(principalId, tenantId, Guid.Empty); AccessMode = ControlPlaneAccessMode.Tenant; }
        public void UseWorkspace(RequestContext value) { current = value; AccessMode = ControlPlaneAccessMode.Workspace; }
    }

    private sealed class StoreFixture(
        string directory,
        ServiceProvider provider,
        IControlPlaneStore store,
        IAgentResourceQueries queries) : IAsyncDisposable
    {
        public IControlPlaneStore Store { get; } = store;
        public IAgentResourceQueries Queries { get; } = queries;
        public ICurrentRequestContext RequestContext => Provider.GetRequiredService<ICurrentRequestContext>();

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
