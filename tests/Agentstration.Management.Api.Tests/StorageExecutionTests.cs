using System.Text;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StorageExecutionTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task WorkspaceDefaultsAreFunctionalAndDifferentStoragesNeverShareContentAuthority()
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        var services = factory.Services;
        await services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        using var caller = services.GetRequiredService<IRequestContextScopeFactory>().Push(bootstrap);
        var scope = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
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
    public async Task ProvisioningPreservesDefaultIdentityDisablementAndIntentionalTypeRevocation()
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        var services = factory.Services;
        var provisioning = services.GetRequiredService<IWorkspacePlatformResourceProvisioner>();
        await provisioning.EnsureAllAsync(default);
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var scope = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
        var resources = services.GetRequiredService<IResourceStore>();
        var address = new ScopedResourceAddress(scope, ResourceNamespace.Default, StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName);
        var before = (await resources.GetExactAsync<StorageResource>(address, default))!;
        await provisioning.EnsureAllAsync(default);
        var repeated = (await resources.GetExactAsync<StorageResource>(address, default))!;
        Assert.AreEqual(before.Value.Uid, repeated.Value.Uid);
        Assert.AreEqual(before.ETag, repeated.ETag);
        var type = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(ResourceScopeRef.Instance,
            ResourceNamespace.Default, "local", default))!;
        var policies = services.GetRequiredService<IResourceAccessPolicyService>();
        var policy = (await policies.GetAsync(type.Value.Uid, default))!;
        _ = await policies.PutAsync(policy.Policy with { Grants = policy.Policy.Grants.Where(g => g.TargetScopeRef != scope).ToArray() }, policy.ETag, default);
        await provisioning.EnsureAllAsync(default);
        Assert.IsFalse((await policies.EvaluateAsync(new(type.Value.Uid, ResourceScopeRef.Instance, type.Value.Namespace, type.Value.Kind),
            scope, ResourceAccessAction.Use, default)).Allowed);
        var denied = await Assert.ThrowsAsync<StorageException>(() => services.GetRequiredService<IStorageContentGateway>()
            .ListAsync(Target(before.Value, scope), "notes/", 10));
        Assert.AreEqual("storage_access_denied", denied.Code);
        var revokedPolicy = (await policies.GetAsync(type.Value.Uid, default))!;
        _ = await policies.PutAsync(policy.Policy, revokedPolicy.ETag, default);
        using (services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("storage.local-default", "1.0.0")))
            _ = await services.GetRequiredService<StorageManagementService>().PutExactAsync(scope, ResourceNamespace.Default,
                before.Value.Name, before.Value.Definition with { Enabled = false }, before.ETag, default);
        await provisioning.EnsureAllAsync(default);
        var disabled = (await resources.GetExactAsync<StorageResource>(address, default))!;
        Assert.AreEqual(before.Value.Uid, disabled.Value.Uid);
        Assert.IsFalse(disabled.Value.Definition.Enabled);
    }

    [TestMethod]
    public async Task MetadataOnlyLocalVersionRemainsImmutableAndExistingPinsAreNotAdopted()
    {
        await using var factory = Factory();
        _ = await GetBootstrapContextAsync(factory);
        var services = factory.Services;
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var provisioner = services.GetRequiredService<StoragePlatformResourceProvisioner>();
        await provisioner.EnsureAsync(default);
        var type = (await services.GetRequiredService<StorageTypeManagementService>().GetExactAsync(ResourceScopeRef.Instance,
            ResourceNamespace.Default, "local", default))!.Value;
        var metadata = type.Definition.Versions.Single(v => v.Version == "1.0.0");
        var resource = await services.GetRequiredService<StorageManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "metadata-pin" },
            Definition = new()
            {
                DisplayName = "Metadata pin",
                Type = new()
                {
                    Type = new(type.Name, type.ScopeRef, type.Namespace),
                    TypeUid = type.Uid,
                    Version = metadata.Version,
                    SchemaDigest = metadata.SchemaDigest,
                    ContractDigest = metadata.ContractDigest
                },
                Configuration = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
                Envelope = System.Text.Json.JsonSerializer.SerializeToElement(new { })
            }
        }, default);
        await provisioner.EnsureAsync(default);
        var context = await services.GetRequiredService<IStorageResolver>().ResolveAsync(Revision(resource.Value),
            new(ResourceScopeRef.Instance, resource.Value.Address));
        Assert.AreEqual("1.0.0", context.Type.Version);
        Assert.IsFalse(context.Capabilities.Write);
        Assert.IsFalse(context.Capabilities.Read);
        Assert.IsFalse(type.Definition.Versions.Single(v => v.Version == "2.0.0").Capabilities.StableVersions);
    }

    private static StorageRevisionReference Revision(StorageResource resource) => new(
        new(resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Name), resource.Uid, resource.Generation,
        StorageDigests.Binding(resource.Definition));

    [TestMethod]
    public async Task ContentInvocationRequiresExecutionPermissionExactContextAndAValidLocalEnvelope()
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        var services = factory.Services;
        var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
        using var caller = scopes.Push(bootstrap);
        var scope = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
        var resource = (await services.GetRequiredService<IResourceStore>().GetExactAsync<StorageResource>(
            new(scope, ResourceNamespace.Default, StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!.Value;
        var target = Target(resource, scope);
        var gateway = services.GetRequiredService<IStorageContentGateway>();
        var foreign = await Assert.ThrowsAsync<StorageException>(() => gateway.ListAsync(target with
        { Consumer = target.Consumer with { ConsumerScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } }, "notes/", 10));
        Assert.AreEqual("storage_access_denied", foreign.Code);
        using (scopes.Push(bootstrap with
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
    public async Task DefaultProvisioningHandlesNewOperationalAndCollidingWorkspacesExplicitly()
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        var services = factory.Services;
        var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
        using var system = scopes.PushSystem();
        var identities = services.GetRequiredService<IIdentityStore>();
        var resources = services.GetRequiredService<IResourceStore>();
        var provisioner = services.GetRequiredService<LocalStorageDefaultsProvisioner>();
        var workspace = new Workspace(Guid.NewGuid(), bootstrap.TenantId, "new-local-workspace", "New local Workspace",
            WorkspaceStatus.Initializing, DateTimeOffset.UtcNow);
        await identities.AddWorkspaceAsync(workspace, default);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => provisioner.EnsureAsync(workspace, default)));
        var address = new ScopedResourceAddress(ResourceScopeRef.Workspace(workspace.Id), ResourceNamespace.Default,
            StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName);
        var original = (await resources.GetExactAsync<StorageResource>(address, default))!;
        await provisioner.EnsureAsync(workspace, default);
        Assert.AreEqual(original.ETag, (await resources.GetExactAsync<StorageResource>(address, default))!.ETag);

        var operational = (await identities.ListWorkspacesAsync(default)).Where(w => w.Kind != WorkspaceKind.Business).ToArray();
        Assert.IsTrue(operational.Any(w => w.Kind == WorkspaceKind.InstanceOperations));
        Assert.IsTrue(operational.Any(w => w.Kind == WorkspaceKind.TenantOperations));
        foreach (var target in operational)
        {
            var owner = target.Kind == WorkspaceKind.InstanceOperations ? ResourceScopeRef.Instance : ResourceScopeRef.Tenant(target.TenantId);
            var storage = (await resources.GetExactAsync<StorageResource>(new(owner, ResourceNamespace.Default,
                StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!.Value;
            Assert.AreEqual(target.Id.ToString("N"), storage.Metadata.Annotations[LocalStorageDefaultsProvisioner.WorkspaceAnnotation]);
            Assert.AreEqual(ResourceScopeRef.Workspace(target.Id), storage.Definition.UsePolicy.Grants.Single().ScopeRef);
            using var businessCaller = scopes.Push(bootstrap);
            var denied = await Assert.ThrowsAsync<StorageException>(() => services.GetRequiredService<IStorageResolver>()
                .ResolveAsync(Revision(storage), new(ResourceScopeRef.Workspace(bootstrap.WorkspaceId), storage.Address)));
            Assert.AreEqual("storage_not_found", denied.Code);
        }

        var colliding = workspace with { Id = Guid.NewGuid(), Name = "colliding-local-workspace" };
        await identities.AddWorkspaceAsync(colliding, default);
        var foreign = await resources.PutExactAsync(ResourceScopeRef.Workspace(colliding.Id), original.Value with
        {
            Uid = Guid.NewGuid(),
            ScopeRef = ResourceScopeRef.Workspace(colliding.Id),
            Governance = ResourceGovernance.User,
            Metadata = original.Value.Metadata with { Annotations = new Dictionary<string, string>() }
        }, null, true, default);
        var collision = await Assert.ThrowsAsync<StorageException>(() => provisioner.EnsureAsync(colliding, default));
        Assert.AreEqual("storage_default_collision", collision.Code);
        Assert.AreEqual(foreign.ETag, (await resources.GetExactAsync<StorageResource>(address with { ScopeRef = ResourceScopeRef.Workspace(colliding.Id) }, default))!.ETag);
    }

    private static StorageContentRequest Target(StorageResource resource, ResourceScopeRef scope) => new(
        Revision(resource), new(scope, resource.Address), new(Write: true, Prefix: "notes/"));
}
