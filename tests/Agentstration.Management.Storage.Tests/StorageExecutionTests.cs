using System.Text;
using Agentstration.Agents;
using Agentstration.Flows.Application;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StorageExecutionTests
{
    [TestMethod]
    public async Task InstanceDefaultIsFunctionalAndDifferentStoragesNeverShareContentAuthority()
    {
        await using var factory = await FixtureAsync();
        var bootstrap = factory.Context;
        var services = factory.Services;
        await services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        bootstrap = await InstanceContextAsync(services, bootstrap);
        using var caller = services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(bootstrap);
        var scope = ResourceScopeRef.Instance;
        var store = services.GetRequiredService<IResourceStore>();
        var resource = (await store.GetExactAsync<StorageResource>(new(scope, ResourceNamespace.Default,
            StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!;
        var request = Target(resource.Value, scope);
        var gateway = services.GetRequiredService<IStorageContentGateway>();
        var write = new StorageWrite("test-write", DateTimeOffset.UtcNow.AddMinutes(1), "notes/a",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("opaque MiXeD/été")));
        var result = await gateway.WriteAsync(request, write);
        var retried = await gateway.WriteAsync(request, write);
        Assert.AreEqual(result.Version, retried.Version);
        Assert.AreEqual("opaque MiXeD/été", Encoding.UTF8.GetString(Convert.FromBase64String(
            (await gateway.ReadAsync(request, result.Reference, 0, 65536)).ContentBase64)));
        var second = await services.GetRequiredService<StorageManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            Metadata = new() { Name = "separate-local" },
            ScopeRef = scope,
            Definition = resource.Value.Definition with { DisplayName = "Separate" }
        }, default);
        Assert.AreNotEqual(resource.Value.Uid, second.Value.Uid);
        Assert.HasCount(0, (await gateway.ListAsync(Target(second.Value, scope), "notes/", 10)).Objects);
        var foreign = await Assert.ThrowsAsync<StorageException>(() => gateway.ReadAsync(Target(second.Value, scope), result.Reference, 0, 10));
        Assert.AreEqual("storage_content_invalid_reference", foreign.Code);
        await gateway.WriteAsync(request, write with { RequestId = "second", Key = "notes/b" });
        var page = await gateway.ListAsync(request, "notes/", 1);
        Assert.IsNotNull(page.Cursor);
        var replay = await Assert.ThrowsAsync<StorageException>(() => gateway.ListAsync(request, "notes/", 2, page.Cursor));
        Assert.AreEqual("storage_content_invalid_reference", replay.Code);
        Assert.DoesNotContain(services.GetRequiredService<LocalStorageOptions>().RootDirectory, result.Reference, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ProvisioningPreservesDefaultIdentityDisablementAndExistingAccessDecisions()
    {
        await using var factory = await FixtureAsync();
        var bootstrap = factory.Context;
        var services = factory.Services;
        var provisioning = services.GetRequiredService<IWorkspacePlatformResourceProvisioner>();
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var scope = ResourceScopeRef.Instance;
        var resources = services.GetRequiredService<IResourceStore>();
        var address = new ScopedResourceAddress(scope, ResourceNamespace.Default, StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName);
        var before = (await resources.GetExactAsync<StorageResource>(address, default))!;
        var type = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(scope,
            ResourceNamespace.Default, "local", default))!;
        var policies = services.GetRequiredService<IResourceAccessPolicyService>();
        var consumer = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
        var target = new ResourceAccessIdentity(type.Value.Uid, scope, type.Value.Namespace, type.Value.Kind);
        var allowed = await policies.PutAsync(new(target, [new(consumer, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        var revoked = await policies.PutAsync(allowed.Policy with { Grants = [] }, allowed.ETag, default);
        await provisioning.EnsureAllAsync(default);
        Assert.AreEqual(before.ETag, (await resources.GetExactAsync<StorageResource>(address, default))!.ETag);
        Assert.AreEqual(type.ETag, (await resources.GetExactAsync<StorageTypeResource>(new(scope, type.Value.Namespace, type.Value.Kind, type.Value.Name), default))!.ETag);
        Assert.AreEqual(revoked.ETag, (await policies.GetAsync(type.Value.Uid, default))!.ETag);
        Assert.IsFalse((await policies.EvaluateAsync(target, consumer, ResourceAccessAction.Use, default)).Allowed);
        using (services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("storage.local-default", "1.0.0")))
            _ = await services.GetRequiredService<StorageManagementService>().PutExactAsync(scope, ResourceNamespace.Default,
                before.Value.Name, before.Value.Definition with { Enabled = false }, before.ETag, default);
        await provisioning.EnsureAllAsync(default);
        var disabled = (await resources.GetExactAsync<StorageResource>(address, default))!;
        Assert.AreEqual(before.Value.Uid, disabled.Value.Uid);
        Assert.IsFalse(disabled.Value.Definition.Enabled);
    }

    [TestMethod]
    public async Task FunctionalLocalV1AndExistingPinsRemainUnchangedAfterSynchronization()
    {
        await using var factory = await FixtureAsync();
        var services = factory.Services;
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var provisioner = services.GetRequiredService<StoragePlatformResourceProvisioner>();
        await provisioner.EnsureAsync(default);
        var type = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(ResourceScopeRef.Instance,
            ResourceNamespace.Default, "local", default))!.Value;
        var version = type.Definition.Versions.Single(v => v.Version == "1.0.0");
        Assert.HasCount(1, type.Definition.Versions);
        Assert.AreEqual(version.Version, type.Definition.PreferredVersion);
        var resource = await services.GetRequiredService<StorageManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "local-pin" },
            Definition = new()
            {
                DisplayName = "Local pin",
                Type = new()
                {
                    Type = new(type.Name, type.ScopeRef, type.Namespace),
                    TypeUid = type.Uid,
                    Version = version.Version,
                    SchemaDigest = version.SchemaDigest,
                    ContractDigest = version.ContractDigest
                },
                Configuration = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
                Envelope = System.Text.Json.JsonSerializer.SerializeToElement(new { })
            }
        }, default);
        await provisioner.EnsureAsync(default);
        var context = await services.GetRequiredService<IStorageResolver>().ResolveAsync(Revision(resource.Value),
            new(ResourceScopeRef.Instance, resource.Value.Address));
        Assert.AreEqual("1.0.0", context.Type.Version);
        Assert.IsTrue(context.Capabilities.Write);
        Assert.IsTrue(context.Capabilities.Read);
        Assert.IsFalse(context.Capabilities.StableVersions);
        var synchronized = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(ResourceScopeRef.Instance,
            ResourceNamespace.Default, "local", default))!.Value;
        Assert.AreEqual(type.ETag, synchronized.ETag);
        Assert.AreEqual(version.ContractDigest, synchronized.Definition.Versions.Single(v => v.Version == "1.0.0").ContractDigest);
    }

    private static StorageRevisionReference Revision(StorageResource resource) => new(
        new(resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Name), resource.Uid, resource.Generation,
        StorageDigests.Binding(resource.Definition));

    [TestMethod]
    public async Task ContentInvocationRequiresExecutionPermissionExactContextAndAValidLocalEnvelope()
    {
        await using var factory = await FixtureAsync();
        var bootstrap = factory.Context;
        var services = factory.Services;
        var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
        bootstrap = await InstanceContextAsync(services, bootstrap);
        using var caller = scopes.PushInstance(bootstrap);
        var scope = ResourceScopeRef.Instance;
        var resource = (await services.GetRequiredService<IResourceStore>().GetExactAsync<StorageResource>(
            new(scope, ResourceNamespace.Default, StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!.Value;
        var target = Target(resource, scope);
        var gateway = services.GetRequiredService<IStorageContentGateway>();
        var foreign = await Assert.ThrowsAsync<StorageException>(() => gateway.ListAsync(target with
        { Consumer = target.Consumer with { ConsumerScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } }, "notes/", 10));
        Assert.AreEqual("storage_access_denied", foreign.Code);
        using (scopes.PushInstance(bootstrap with
        {
            Restriction = new(Guid.NewGuid(), bootstrap.WorkspaceId,
            new HashSet<string>(StringComparer.Ordinal) { AuthorizationPermissions.ResourcesRead })
        }))
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => gateway.ListAsync(target, "notes/", 10));
        var invalid = await Assert.ThrowsAsync<StorageException>(() => services.GetRequiredService<StorageManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            ScopeRef = scope,
            Metadata = new() { Name = "invalid-envelope" },
            Definition = resource.Definition with
            { Envelope = System.Text.Json.JsonSerializer.SerializeToElement(new { prefix = "../outside" }) }
        }, default));
        Assert.AreEqual("storage_configuration_invalid", invalid.Code);
        var widening = await Assert.ThrowsAsync<StorageException>(() => gateway.ListAsync(target with
        { Policy = target.Policy with { MaximumPageSize = 101 } }, "notes/", 10));
        Assert.AreEqual("storage_content_invalid_policy", widening.Code);
    }

    [TestMethod]
    public async Task RestrictedWorkspaceDefaultsRemainUnavailableWithoutChangingPoliciesOrBlockingOtherProvisioning()
    {
        await using var factory = await FixtureAsync();
        var bootstrap = factory.Context;
        var services = factory.Services;
        var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
        using var system = scopes.PushSystem();
        var identities = services.GetRequiredService<IIdentityStore>();
        var resources = services.GetRequiredService<IResourceStore>();
        var provisioner = services.GetRequiredService<LocalStorageDefaultsProvisioner>();
        var typeAddress = new ScopedResourceAddress(ResourceScopeRef.Instance, ResourceNamespace.Default, StorageResourceKinds.StorageType, "local");
        var typeBefore = (await resources.GetExactAsync<StorageTypeResource>(typeAddress, default))!;
        Assert.IsEmpty(typeBefore.Value.Definition.UsePolicy.Grants);
        var policies = services.GetRequiredService<IResourceAccessPolicyService>();
        Assert.IsNull(await policies.GetAsync(typeBefore.Value.Uid, default));
        var workspace = new Workspace(Guid.NewGuid(), bootstrap.TenantId, "new-local-workspace", "New local Workspace",
            WorkspaceStatus.Initializing, DateTimeOffset.UtcNow);
        await identities.AddWorkspaceAsync(workspace, default);
        var initial = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => provisioner.EnsureAsync(workspace, default)));
        Assert.IsTrue(initial.All(r => r.Availability == StorageAvailability.Unavailable));
        // An explicit generic grant makes the default usable without adding embedded grants.
        var existing = await policies.PutAsync(new(new(typeBefore.Value.Uid, ResourceScopeRef.Instance, ResourceNamespace.Default,
            StorageResourceKinds.StorageType), [new(ResourceScopeRef.Workspace(workspace.Id), [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => provisioner.EnsureAsync(workspace, default)));
        Assert.IsTrue(results.All(r => r.Availability == StorageAvailability.Ready));
        var address = new ScopedResourceAddress(ResourceScopeRef.Workspace(workspace.Id), ResourceNamespace.Default,
            StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName);
        var sharedDefault = (await resources.GetExactAsync<StorageResource>(address, default))!;
        Assert.IsEmpty(sharedDefault.Value.Definition.UsePolicy.Grants);
        await services.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(workspace, default);
        Assert.AreEqual(WorkspaceStatus.Active, (await identities.GetWorkspaceAsync(workspace.TenantId, workspace.Id, default))!.Status);
        Assert.AreEqual(typeBefore.ETag, (await resources.GetExactAsync<StorageTypeResource>(typeAddress, default))!.ETag);
        Assert.AreEqual(existing.ETag, (await policies.GetAsync(typeBefore.Value.Uid, default))!.ETag);

        var operational = (await identities.ListWorkspacesAsync(default)).Where(w => w.Kind != WorkspaceKind.Business).ToArray();
        Assert.IsTrue(operational.Any(w => w.Kind == WorkspaceKind.InstanceOperations));
        Assert.IsTrue(operational.Any(w => w.Kind == WorkspaceKind.TenantOperations));
        StorageResource? prototype = null;
        foreach (var targetWorkspace in operational)
        {
            var status = await provisioner.EnsureAsync(targetWorkspace, default);
            var owner = LocalStorageDefaultsProvisioner.OwnerScope(targetWorkspace);
            var storage = (await resources.GetExactAsync<StorageResource>(new(owner, ResourceNamespace.Default,
                StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))?.Value;
            if (targetWorkspace.Kind == WorkspaceKind.TenantOperations)
            {
                Assert.AreEqual(StorageAvailability.Unavailable, status.Availability);
                Assert.IsNull(storage);
            }
            else
            {
                Assert.AreEqual(StorageAvailability.Ready, status.Availability);
                Assert.IsNotNull(storage);
                Assert.AreEqual(targetWorkspace.Id.ToString("N"), storage.Metadata.Annotations[LocalStorageDefaultsProvisioner.WorkspaceAnnotation]);
                Assert.IsEmpty(storage.Definition.UsePolicy.Grants);
                prototype = storage;
                using var businessCaller = scopes.Push(bootstrap);
                var denied = await Assert.ThrowsAsync<StorageException>(() => services.GetRequiredService<IStorageResolver>()
                    .ResolveAsync(Revision(storage), new(ResourceScopeRef.Workspace(bootstrap.WorkspaceId), storage.Address)));
                Assert.AreEqual("storage_not_found", denied.Code);
            }
        }
        Assert.IsNotNull(prototype);
        var colliding = workspace with { Id = Guid.NewGuid(), Name = "colliding-local-workspace" };
        await identities.AddWorkspaceAsync(colliding, default);
        var foreign = await resources.PutExactAsync(ResourceScopeRef.Workspace(colliding.Id), prototype with
        {
            Uid = Guid.NewGuid(),
            ScopeRef = ResourceScopeRef.Workspace(colliding.Id),
            Governance = ResourceGovernance.User,
            Metadata = prototype.Metadata with { Annotations = new Dictionary<string, string>() }
        }, null, true, default);
        var collision = await Assert.ThrowsAsync<StorageException>(() => provisioner.EnsureAsync(colliding, default));
        Assert.AreEqual("storage_default_collision", collision.Code);
        Assert.AreEqual(foreign.ETag, (await resources.GetExactAsync<StorageResource>(address with { ScopeRef = ResourceScopeRef.Workspace(colliding.Id) }, default))!.ETag);
    }

    private static async Task<StorageFixture> FixtureAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-storage-execution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string Connection(string name) => $"Data Source={Path.Combine(directory, name + ".db")};Pooling=False";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new LocalBootstrapOptions { GrantPlatformAdministrator = true });
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
        services.AddAgentstration(directory,
            controlPlaneConnectionString: Connection("management"),
            workPlaneConnectionString: Connection("work"),
            flowConnectionString: Connection("flow"),
            runtimeConnectionString: Connection("runtime"),
            enableHostedServices: false);
        var provider = services.BuildServiceProvider();
        try
        {
            using var system = provider.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            await provider.GetRequiredService<IAgentstrationStorageInitializer>().InitializeAsync(default);
            await provider.GetRequiredService<AgentManagementService>().InitializeAsync(default);
            await provider.GetRequiredService<FlowService>().InitializeAsync(default);
            await provider.GetRequiredService<FlowRunService>().InitializeAsync(default);
            var context = await provider.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            await provider.GetRequiredService<IOperationalTopologyProvisioner>().EnsureAllAsync(default);
            await provider.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
            return new(directory, provider, context);
        }
        catch
        {
            await provider.DisposeAsync();
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private sealed class StorageFixture(string directory, ServiceProvider services, RequestContext context) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public RequestContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<RequestContext> InstanceContextAsync(IServiceProvider services, RequestContext bootstrap)
    {
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var workspace = await services.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
        return bootstrap with { TenantId = workspace.TenantId, WorkspaceId = workspace.Id };
    }

    private static StorageContentRequest Target(StorageResource resource, ResourceScopeRef scope) => new(
        Revision(resource), new(scope, resource.Address), new(Write: true, Prefix: "notes/"));
}
