using System.Text.Json;
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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StorageSharedTypeAuthorizationTests
{
    [TestMethod]
    public async Task TenantPropagatedUseAndRevocationSurviveSynchronizationAndRestartWithoutEmbeddedGrants()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-shared-local", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        StorageResource stored;
        ResourceAccessPolicyResponse grant;
        RequestContext context;
        StorageObject content;
        try
        {
            using (var host = await OpenAsync(directory))
            {
                var services = host.Services;
                var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
                using (scopes.PushSystem())
                    context = await services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
                var types = services.GetRequiredService<StorageTypeManagementService>();
                var policies = services.GetRequiredService<IResourceAccessPolicyService>();
                var management = services.GetRequiredService<StorageManagementService>();
                StorageTypeResource type;
                using (scopes.PushSystem())
                {
                    type = (await types.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "local", default))!.Value;
                    Assert.IsEmpty(type.Definition.UsePolicy.Grants);
                    grant = await policies.PutAsync(new(new(type.Uid, ResourceScopeRef.Instance, type.Namespace, type.Kind),
                        [new(ResourceScopeRef.Tenant(context.TenantId), [ResourceAccessAction.Discover], IncludeDescendants: true)]), null, default);
                }
                using (scopes.Push(context))
                {
                    var discoverOnly = await Assert.ThrowsAsync<StorageException>(() => management.CreateAsync(Desired(type, context.WorkspaceId), default));
                    Assert.AreEqual("storage_access_denied", discoverOnly.Code);
                }
                using (scopes.PushSystem())
                    grant = await policies.PutAsync(grant.Policy with
                    {
                        Grants = [new(ResourceScopeRef.Tenant(context.TenantId), [ResourceAccessAction.Discover, ResourceAccessAction.Use], IncludeDescendants: true)]
                    }, grant.ETag, default);
                using (scopes.Push(context))
                {
                    stored = (await management.CreateAsync(Desired(type, context.WorkspaceId), default)).Value;
                    Assert.IsEmpty(stored.Definition.UsePolicy.Grants);
                    content = await services.GetRequiredService<IStorageContentGateway>().WriteAsync(Target(stored),
                        new("shared-write", DateTimeOffset.UtcNow.AddMinutes(4), "proof/content", Convert.ToBase64String([1, 2, 3])));
                }
                using (scopes.PushSystem())
                {
                    var identities = services.GetRequiredService<IIdentityStore>();
                    var foreignTenant = new Tenant(Guid.NewGuid(), "foreign-storage-tenant", "Foreign", TenantStatus.Active, DateTimeOffset.UtcNow);
                    var foreignWorkspace = new Workspace(Guid.NewGuid(), foreignTenant.Id, "foreign-storage-workspace", "Foreign", WorkspaceStatus.Active, DateTimeOffset.UtcNow);
                    await identities.AddTenantAsync(foreignTenant, default);
                    await identities.AddWorkspaceAsync(foreignWorkspace, default);
                    var denied = await Assert.ThrowsAsync<StorageException>(() => management.CreateAsync(Desired(type, foreignWorkspace.Id), default));
                    Assert.AreEqual("storage_access_denied", denied.Code);
                    await services.GetRequiredService<StoragePlatformResourceProvisioner>().EnsureAsync(default);
                    var originalWorkspace = (await identities.GetWorkspaceAsync(context.TenantId, context.WorkspaceId, default))!;
                    Assert.AreEqual(StorageAvailability.Ready,
                        (await services.GetRequiredService<LocalStorageDefaultsProvisioner>().EnsureAsync(originalWorkspace, default)).Availability);
                    Assert.AreEqual(grant.ETag, (await policies.GetAsync(type.Uid, default))!.ETag);
                    Assert.IsEmpty((await types.GetExactAsync(ResourceScopeRef.Instance, type.Namespace, type.Name, default))!.Value.Definition.UsePolicy.Grants);
                }
            }
            using (var restarted = await OpenAsync(directory))
            {
                var services = restarted.Services;
                var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
                var policies = services.GetRequiredService<IResourceAccessPolicyService>();
                var management = services.GetRequiredService<StorageManagementService>();
                using (scopes.Push(context))
                {
                    var restored = (await management.GetExactAsync(stored.ScopeRef!.Value, stored.Namespace, stored.Name, default))!.Value;
                    Assert.AreEqual(stored.Uid, restored.Uid);
                    Assert.AreEqual(stored.Generation, restored.Generation);
                    _ = await management.ResolveAsync(Revision(stored), Target(stored).Consumer);
                    var read = await services.GetRequiredService<IStorageContentGateway>().ReadAsync(Target(stored), content.Reference, 0, 3);
                    CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, Convert.FromBase64String(read.ContentBase64));
                }
                using (scopes.PushSystem())
                {
                    Assert.AreEqual(grant.ETag, (await policies.GetAsync(grant.Policy.Resource.Uid, default))!.ETag);
                    grant = await policies.PutAsync(grant.Policy with
                    {
                        Grants = [new(ResourceScopeRef.Tenant(context.TenantId), [ResourceAccessAction.Discover], IncludeDescendants: true)]
                    }, grant.ETag, default);
                    await services.GetRequiredService<StoragePlatformResourceProvisioner>().EnsureAsync(default);
                }
                using (scopes.Push(context))
                {
                    var denied = await Assert.ThrowsAsync<StorageException>(() => management.ResolveAsync(Revision(stored), Target(stored).Consumer));
                    Assert.AreEqual("storage_access_denied", denied.Code);
                    await Assert.ThrowsAsync<StorageException>(() => services.GetRequiredService<IStorageContentGateway>().ReadAsync(Target(stored), content.Reference, 0, 3));
                }
            }
            using (var revokedRestart = await OpenAsync(directory))
            {
                var services = revokedRestart.Services;
                using var caller = services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
                var denied = await Assert.ThrowsAsync<StorageException>(() => services.GetRequiredService<StorageManagementService>()
                    .ResolveAsync(Revision(stored), Target(stored).Consumer));
                Assert.AreEqual("storage_access_denied", denied.Code);
                Assert.AreEqual(grant.ETag, (await services.GetRequiredService<IResourceAccessPolicyService>().GetAsync(grant.Policy.Resource.Uid, default))!.ETag);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<IHost> OpenAsync(string directory)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new LocalBootstrapOptions { GrantPlatformAdministrator = true });
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
        builder.Services.AddAgentstration(new AgentstrationCompositionOptions { DataDirectory = directory, EnableHostedServices = false });
        var host = builder.Build();
        var services = host.Services;
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        await services.GetRequiredService<IAgentstrationStorageInitializer>().InitializeAsync(default);
        await services.GetRequiredService<AgentManagementService>().InitializeAsync(default);
        await services.GetRequiredService<FlowService>().InitializeAsync(default);
        await services.GetRequiredService<FlowRunService>().InitializeAsync(default);
        _ = await services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await services.GetRequiredService<IOperationalTopologyProvisioner>().EnsureAllAsync(default);
        await services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        return host;
    }

    private static StorageResource Desired(StorageTypeResource type, Guid workspace)
    {
        var version = type.Definition.Versions.Single(v => v.Version == StoragePlatformResourceProvisioner.ContentTypeVersion);
        return new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            ScopeRef = ResourceScopeRef.Workspace(workspace),
            Metadata = new() { Name = "tenant-shared-local" },
            Definition = new()
            {
                DisplayName = "Shared local",
                Type = new()
                {
                    Type = new(type.Name, type.ScopeRef, type.Namespace),
                    TypeUid = type.Uid,
                    Version = version.Version,
                    SchemaDigest = version.SchemaDigest,
                    ContractDigest = version.ContractDigest
                },
                Configuration = JsonSerializer.SerializeToElement(new { }),
                Envelope = JsonSerializer.SerializeToElement(new { write = true })
            }
        };
    }
    private static StorageRevisionReference Revision(StorageResource resource) => new(new(resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Name),
        resource.Uid, resource.Generation, StorageDigests.Binding(resource.Definition));
    private static StorageContentRequest Target(StorageResource resource) => new(Revision(resource), new(resource.ScopeRef!.Value, resource.Address), new(Write: true, Prefix: "proof/"));
}
