using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StorageApiTests : ModelManagementApiTestBase
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });

    [TestMethod]
    public async Task BuiltinCatalogIsGovernedAndLocalStorageUsesCanonicalIdentity()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var catalog = await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types");
        var type = catalog!.Value.Single(t => t.Name == StoragePlatformResourceProvisioner.LocalTypeName);
        Assert.AreEqual(ResourceOriginKind.BuiltIn, type.Governance.Origin.Kind);
        Assert.AreEqual(ResourceManagementAuthorityKind.Core, type.Governance.ManagedBy?.Kind);
        Assert.IsTrue(type.ObservedVersions.All(v => v.Availability == StorageAvailability.Ready));
        Assert.AreEqual("1.0.0", type.Definition.PreferredVersion);
        Assert.HasCount(1, type.Definition.Versions);
        Assert.IsTrue(type.Definition.Versions.Single(v => v.Version == "1.0.0").Capabilities.Write);

        using var createdResponse = await client.PostAsJsonAsync("/api/storages",
            new CreateStorageRequest("local-test", Properties(type), ResourceScopeRef.Instance));
        await Status(createdResponse, HttpStatusCode.Created);
        var created = (await createdResponse.Content.ReadFromJsonAsync<StorageResource>())!;
        Assert.AreEqual(ResourceScopeRef.Instance, created.ScopeRef);
        Assert.IsNull(created.Definition.ConnectedService);
        Assert.AreNotEqual(Guid.Empty, created.Uid);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        using var detail = await client.GetAsync(Path(created));
        await Status(detail, HttpStatusCode.OK);
        Assert.AreEqual(createdResponse.Headers.ETag, detail.Headers.ETag);
        var list = await client.GetFromJsonAsync<PagedResponse<StorageResource>>("/api/storages?take=1");
        Assert.HasCount(1, list!.Value);
        using var invalidPage = await client.GetAsync("/api/storages?take=101");
        await Problem(invalidPage, HttpStatusCode.UnprocessableEntity, "storage_page_invalid");
    }

    [TestMethod]
    public async Task HttpUpdatesRequireETagAndExplicitReconciliation()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var scope = await TenantScope(client);
        SelectConsoleContext(client, scope);
        var type = await CreateType(client, scope);
        using var response = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("concurrent", Properties(type), scope));
        await Status(response, HttpStatusCode.Created);
        var resource = (await response.Content.ReadFromJsonAsync<StorageResource>())!;
        using var missing = await client.PutAsJsonAsync(Path(resource), new PutStorageRequest(resource.Definition));
        await Problem(missing, (HttpStatusCode)428, "storage_etag_required");
        using var first = await Send(client, HttpMethod.Put, Path(resource), new PutStorageRequest(resource.Definition with { DisplayName = "Changed" }), resource.ETag!);
        await Status(first, HttpStatusCode.OK);
        using var stale = await Send(client, HttpMethod.Put, Path(resource), new PutStorageRequest(resource.Definition), resource.ETag!);
        await Problem(stale, HttpStatusCode.Conflict, "storage_version_conflict");
        using var noBinding = await Send(client, HttpMethod.Put, Path(resource), new PutStorageRequest(resource.Definition with { Type = resource.Definition.Type with { Version = "2.0.0" } }), first.Headers.ETag!.ToString());
        await Problem(noBinding, HttpStatusCode.Conflict, "storage_reconciliation_required");
        using var observe = await Send(client, HttpMethod.Post, Path(resource, "/reconcile"), new ReconcileStorageRequest(), first.Headers.ETag!.ToString());
        await Status(observe, HttpStatusCode.OK);
        using var disable = await Send(client, HttpMethod.Post, Path(resource, "/deactivate"), null, observe.Headers.ETag!.ToString());
        await Status(disable, HttpStatusCode.OK);
        Assert.AreEqual(StorageAvailability.Disabled, (await disable.Content.ReadFromJsonAsync<StorageResource>())!.ObservedState?.Availability);
        using var missingResource = await client.GetAsync($"/api/storages/missing?scopeRef={Uri.EscapeDataString(scope.Value)}");
        await Problem(missingResource, HttpStatusCode.NotFound, "storage_not_found");
    }

    [TestMethod]
    public async Task RemoteStorageRequiresManualMappingAndNeverResolvesSecretsDuringManagementOrResolution()
    {
        var secrets = new NeverResolveSecrets();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISecretResolver>();
            services.AddSingleton<ISecretResolver>(secrets);
        }));
        using var client = factory.CreateClient();
        var scope = await TenantScope(client);
        SelectConsoleContext(client, scope);
        var serviceVersion = new ConnectedServiceTypeVersionDescriptor("1.0.0", StorageDigests.Schema(Schema), Schema,
            [new("credential", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)],
            [new("token", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured, SourceInputId: "credential")]);
        using var serviceTypeResponse = await client.PostAsJsonAsync("/api/connected-service-types",
            new CreateConnectedServiceTypeRequest("account-type", new() { DisplayName = "Account", PreferredVersion = "1.0.0", Versions = [serviceVersion] }, scope));
        await Status(serviceTypeResponse, HttpStatusCode.Created);
        var serviceType = (await serviceTypeResponse.Content.ReadFromJsonAsync<ConnectedServiceTypeResource>())!;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(scope, new SecretResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Secret,
                Metadata = new() { Name = "credential" },
                ScopeRef = scope,
                Definition = new() { DisplayName = "Credential", Vault = new("vault", scope), Key = "fixture-key" }
            }, null, true, default);
        using var serviceResponse = await client.PostAsJsonAsync("/api/connected-services", new CreateConnectedServiceRequest("account", new()
        {
            DisplayName = "Account",
            Type = new()
            {
                Type = new(serviceType.Name, scope),
                TypeUid = serviceType.Uid,
                Version = "1.0.0",
                SchemaDigest = serviceVersion.SchemaDigest
            },
            Options = Empty,
            Bindings = [new() { InputId = "credential", Kind = ConnectedServiceBindingKind.Secret,
                Secret = new SecretReference(new(ResourceNamespace.Default, SecretResourceKinds.Secret, "credential"), scope) }]
        }, scope));
        await Status(serviceResponse, HttpStatusCode.Created);
        var service = (await serviceResponse.Content.ReadFromJsonAsync<ConnectedServiceResource>())!;
        var type = await CreateType(client, scope, remote: true);
        var properties = Properties(type) with
        {
            ConnectedService = new() { Service = new(service.Address, scope), ServiceUid = service.Uid, Generation = service.Generation },
            ConnectedServiceType = service.Definition.Type,
            Bindings = [new("connection", serviceVersion.Exports.Single())]
        };
        using var missingMapping = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("unbound", properties with { Bindings = [] }, scope));
        await Problem(missingMapping, HttpStatusCode.UnprocessableEntity, "storage_mapping_incomplete");
        using var first = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("first", properties, scope));
        await Status(first, HttpStatusCode.Created);
        using var second = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("second", properties, scope));
        await Status(second, HttpStatusCode.Created);
        var storage = (await first.Content.ReadFromJsonAsync<StorageResource>())!;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var resolved = await factory.Services.GetRequiredService<IStorageResolver>().ResolveAsync(new(
                new(scope, storage.Namespace, storage.Kind, storage.Name), storage.Uid, storage.Generation,
                storage.ObservedState!.BindingDigest!), new(scope, storage.Address));
            Assert.AreEqual("[REDACTED]", resolved.ToString());
        }
        Assert.AreEqual(0, secrets.Calls);
        using var deleteService = await Send(client, HttpMethod.Delete,
            $"/api/connected-services/account?scopeRef={Uri.EscapeDataString(scope.Value)}", null, service.ETag!);
        await Status(deleteService, HttpStatusCode.Conflict);
        using var disabled = await Send(client, HttpMethod.Post,
            $"/api/connected-services/account/deactivate?scopeRef={Uri.EscapeDataString(scope.Value)}", null, service.ETag!);
        await Status(disabled, HttpStatusCode.OK);
        var unavailable = await client.GetFromJsonAsync<StorageResource>(Path(storage));
        Assert.AreEqual(StorageAvailability.Stale, unavailable!.ObservedState?.Availability);
        Assert.AreEqual(service.Uid, unavailable.Definition.ConnectedService?.ServiceUid);
        Assert.AreEqual(0, secrets.Calls);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            await factory.Services.GetRequiredService<IResourceStore>().DeleteExactAsync(new(scope, service.Namespace,
                service.Kind, service.Name), disabled.Headers.ETag!.ToString(), default);
        using var recreated = await client.PostAsJsonAsync("/api/connected-services", new CreateConnectedServiceRequest(
            service.Name, service.Definition, scope));
        await Status(recreated, HttpStatusCode.Created);
        var replacement = (await recreated.Content.ReadFromJsonAsync<ConnectedServiceResource>())!;
        Assert.AreNotEqual(service.Uid, replacement.Uid);
        Assert.AreEqual(service.Generation, replacement.Generation);
        var staleStorage = await client.GetFromJsonAsync<StorageResource>(Path(storage));
        Assert.AreEqual(StorageAvailability.Stale, staleStorage!.ObservedState?.Availability);
    }

    [TestMethod]
    public async Task ExtensionTypesRemainGovernedAndBecomeUnavailableWithoutLosingTheCatalog()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var scope = await TenantScope(client);
        SelectConsoleContext(client, scope);
        StorageTypeResource type;
        StoredResource<ExtensionRegistrationResource> registration;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            registration = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(scope, new ExtensionRegistrationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ExtensionKinds.ExtensionRegistration,
                Metadata = new() { Name = "storage-extension" },
                ScopeRef = scope,
                Generation = 1,
                Definition = new() { DisplayName = "Storage extension", Endpoint = new("http://127.0.0.1:1"), ExpectedExtensionId = "extension.storage" }
            }, null, true, default);
            var address = new ScopedResourceAddress(scope, registration.Value.Namespace, registration.Value.Kind, registration.Value.Name);
            using var authority = factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Extension(
                $"{scope.Value}/{address.Kind}/{address.Namespace}/{address.Name}", "extension.storage", "1.0.0"));
            type = (await factory.Services.GetRequiredService<StorageTypeManagementService>().SynchronizeManagedAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = StorageResourceKinds.StorageType,
                Metadata = new() { Name = "object-store", Namespace = ResourceNamespace.Parse("extension.storage") },
                ScopeRef = scope,
                Definition = TypeProperties() with
                {
                    Versions = [TypeProperties().Versions.Single() with
                { Contribution = new(address, registration.Value.Uid, registration.Value.Generation, "object-store", "1.0.0", "sha256:fixture") }]
                }
            }, default)).Value;
        }
        var catalog = await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types");
        Assert.AreEqual(ResourceOriginKind.Extension, catalog!.Value.Single(t => t.Uid == type.Uid).Governance.Origin.Kind);
        var workspace = ResourceScopeRef.Workspace((await GetBootstrapContextAsync(factory)).WorkspaceId);
        var policyPath = $"/api/resource-access-policies/{type.Uid}";
        var policy = new ResourceAccessPolicy(new(type.Uid, scope, type.Namespace, type.Kind),
            [new(workspace, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]);
        using var grant = await client.PutAsJsonAsync(policyPath, policy);
        await Status(grant, HttpStatusCode.OK);
        var policyEtag = grant.Headers.ETag!.ToString();
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(type.Governance))
                type = (await factory.Services.GetRequiredService<StorageTypeManagementService>().SynchronizeManagedAsync(type with
                { Definition = type.Definition with { DisplayName = "Updated extension metadata" } }, default)).Value;
            registration = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(scope,
                registration.Value with { Definition = registration.Value.Definition with { Enabled = false } }, registration.ETag, false, default);
        }
        var offlineType = await client.GetFromJsonAsync<StorageTypeResource>($"/api/storage-types/{type.Name}?scopeRef={Uri.EscapeDataString(scope.Value)}&namespace={type.Namespace}");
        Assert.AreEqual(StorageAvailability.Unavailable, offlineType!.ObservedVersions.Single().Availability);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            registration = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(scope,
                registration.Value with { Definition = registration.Value.Definition with { Enabled = true } }, registration.ETag, false, default);
        using var retainedPolicy = await client.GetAsync(policyPath);
        Assert.AreEqual(policyEtag, retainedPolicy.Headers.ETag!.ToString());
        var returnedType = await client.GetFromJsonAsync<StorageTypeResource>($"/api/storage-types/{type.Name}?scopeRef={Uri.EscapeDataString(scope.Value)}&namespace={type.Namespace}");
        Assert.AreEqual(StorageAvailability.Ready, returnedType!.ObservedVersions.Single().Availability);
        Assert.AreEqual(type.Uid, returnedType.Uid);
        using var created = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("extension-store", Properties(type), scope));
        await Status(created, HttpStatusCode.Created);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            registration = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(scope,
                registration.Value with { Generation = 2 }, registration.ETag, false, default);
        var unavailable = await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types");
        Assert.AreEqual(StorageAvailability.Unavailable, unavailable!.Value.Single(t => t.Uid == type.Uid).ObservedVersions.Single().Availability);
        var storage = (await created.Content.ReadFromJsonAsync<StorageResource>())!;
        var observed = await client.GetFromJsonAsync<StorageResource>(Path(storage));
        Assert.AreEqual(storage.Uid, observed!.Uid);
        Assert.AreEqual(StorageAvailability.Unavailable, observed.ObservedState?.Availability);
        Assert.AreEqual(1, type.Definition.Versions.Single().Contribution?.ProviderGeneration);
        using var revoke = await Send(client, HttpMethod.Put, policyPath, policy with { Grants = [] }, policyEtag);
        await Status(revoke, HttpStatusCode.OK);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            using var authority = factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(type.Governance);
            await factory.Services.GetRequiredService<StorageTypeManagementService>().SynchronizeManagedAsync(type with
            { Definition = type.Definition with { DisplayName = "Retained after revocation" } }, default);
        }
        Assert.IsEmpty((await client.GetFromJsonAsync<ResourceAccessPolicy>(policyPath))!.Grants);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            await factory.Services.GetRequiredService<IResourceStore>().DeleteExactAsync(new(scope, registration.Value.Namespace,
                registration.Value.Kind, registration.Value.Name), registration.ETag, default);
        var missing = await client.GetFromJsonAsync<StorageResource>(Path(storage));
        Assert.AreEqual(storage.Uid, missing!.Uid);
        Assert.AreEqual(StorageAvailability.Unavailable, missing.ObservedState?.Availability);
    }

    [TestMethod]
    public async Task BuiltinTenantPropagatedUsePermitsWorkspaceCreationAndRevocationRefusesIt()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>("/api/resource-scopes/targets?kind=Storage"))!;
        var tenant = targets.Single(t => t.Kind == ResourceScopeKind.Tenant).ScopeRef;
        var workspace = targets.Single(t => t.Kind == ResourceScopeKind.Workspace).ScopeRef;
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var type = (await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types"))!.Value.Single(t => t.Name == "local");
        Assert.IsEmpty(type.Definition.UsePolicy.Grants);
        var policy = new ResourceAccessPolicy(new(type.Uid, ResourceScopeRef.Instance, type.Namespace, type.Kind),
            [new(tenant, [ResourceAccessAction.Discover], IncludeDescendants: true)]);
        var policyPath = $"/api/resource-access-policies/{type.Uid:D}";
        using var discoverOnly = await client.PutAsJsonAsync(policyPath, policy);
        await Status(discoverOnly, HttpStatusCode.OK);
        var policyEtag = discoverOnly.Headers.ETag!.ToString();
        SelectConsoleContext(client, workspace);
        var visible = await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types");
        Assert.IsTrue(visible!.Value.Any(t => t.Uid == type.Uid));
        using var denied = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("discover-only", Properties(type), workspace));
        await Problem(denied, HttpStatusCode.Forbidden, "storage_access_denied");
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var usable = policy with { Grants = [new(tenant, [ResourceAccessAction.Discover, ResourceAccessAction.Use], IncludeDescendants: true)] };
        using var grant = await Send(client, HttpMethod.Put, policyPath, usable, policyEtag);
        await Status(grant, HttpStatusCode.OK);
        policyEtag = grant.Headers.ETag!.ToString();
        SelectConsoleContext(client, workspace);
        using var created = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("tenant-shared-local", Properties(type), workspace));
        await Status(created, HttpStatusCode.Created);
        var storage = (await created.Content.ReadFromJsonAsync<StorageResource>())!;
        Assert.AreEqual(workspace, storage.ScopeRef);
        Assert.AreEqual(type.Uid, storage.Definition.Type.TypeUid);
        Assert.IsEmpty(storage.Definition.UsePolicy.Grants);
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        using var revoked = await Send(client, HttpMethod.Put, policyPath, policy, policyEtag);
        await Status(revoked, HttpStatusCode.OK);
        SelectConsoleContext(client, workspace);
        using var refusal = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("revoked-local", Properties(type), workspace));
        await Problem(refusal, HttpStatusCode.Forbidden, "storage_access_denied");
        var observed = (await client.GetFromJsonAsync<StorageResource>(Path(storage)))!;
        Assert.AreEqual(StorageAvailability.Unavailable, observed.ObservedState!.Availability);
        Assert.IsTrue(observed.ObservedState.Diagnostics.Any(d => d.Code == "storage_access_denied"));
    }

    [TestMethod]
    public async Task TypeDeletionSeesWorkspaceDependenciesAndRequiresHierarchicalGrants()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var targets = (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>("/api/resource-scopes/targets?kind=Storage"))!;
        var tenant = targets.Single(t => t.Kind == ResourceScopeKind.Tenant).ScopeRef;
        var workspace = targets.Single(t => t.Kind == ResourceScopeKind.Workspace).ScopeRef;
        SelectConsoleContext(client, tenant);
        using var typeResponse = await client.PostAsJsonAsync("/api/storage-types", new CreateStorageTypeRequest("ancestor", TypeProperties() with
        { UsePolicy = new() { Grants = [new(workspace)] } }, tenant));
        await Status(typeResponse, HttpStatusCode.Created);
        var type = (await typeResponse.Content.ReadFromJsonAsync<StorageTypeResource>())!;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            await factory.Services.GetRequiredService<IResourceAccessPolicyService>().PutAsync(new(
                new(type.Uid, tenant, type.Namespace, type.Kind, type.Generation),
                [new(workspace, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        SelectConsoleContext(client, workspace);
        using var storage = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("descendant", Properties(type), workspace));
        await Status(storage, HttpStatusCode.Created);
        SelectConsoleContext(client, tenant);
        using var deletion = await Send(client, HttpMethod.Delete, $"/api/storage-types/ancestor?scopeRef={Uri.EscapeDataString(tenant.Value)}", null, type.ETag!);
        await Problem(deletion, HttpStatusCode.Conflict, "storage_type_in_use");
    }

    [TestMethod]
    public async Task BootstrapSupportsScopedTypesAndStorageWithPinnedExistingType()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var scope = await TenantScope(client);
        await using var services = factory.Services.CreateAsyncScope();
        using var system = services.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var operation = new BootstrapResourceOperationContext("storage", "storage", BootstrapProfileScope.Tenant, new(TenantId: scope.TargetId));
        var handlers = services.ServiceProvider.GetServices<IBootstrapResourceHandler>().ToArray();
        var typeHandler = handlers.Single(h => h.Kind == StorageResourceKinds.StorageType);
        var typeDocument = new BootstrapResourceDocument
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.StorageType,
            Metadata = new() { Name = "bootstrap-storage-type" },
            Definition = JsonSerializer.SerializeToElement(TypeProperties())
        };
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await typeHandler.PlanAsync(typeDocument, operation, new(), default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await typeHandler.ApplyAsync(typeDocument, operation, default));
        var type = (await services.ServiceProvider.GetRequiredService<StorageTypeManagementService>()
            .GetExactAsync(scope, ResourceNamespace.Default, "bootstrap-storage-type", default))!.Value;
        var storageDocument = new BootstrapResourceDocument
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            Metadata = new() { Name = "bootstrap-storage" },
            Definition = JsonSerializer.SerializeToElement(Properties(type))
        };
        var handler = handlers.Single(h => h.Kind == StorageResourceKinds.Storage);
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await handler.PlanAsync(storageDocument, operation, new(), default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await handler.ApplyAsync(storageDocument, operation, default));
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await handler.ApplyAsync(storageDocument, operation, default));
        var stored = await services.ServiceProvider.GetRequiredService<StorageManagementService>().GetExactAsync(scope, ResourceNamespace.Default, "bootstrap-storage", default);
        Assert.IsNotNull(stored);
        Assert.AreEqual(scope, stored.Value.ScopeRef);
    }

    private static StorageTypeProperties TypeProperties(bool remote = false) => new()
    {
        DisplayName = "Storage type",
        PreferredVersion = "1.0.0",
        Versions =
        [new() { Version = "1.0.0", ConfigurationSchema = Schema, SchemaDigest = StorageDigests.Schema(Schema),
            EnvelopeSchema = Schema, EnvelopeSchemaDigest = StorageDigests.Schema(Schema), RequiresConnectedService = remote,
            Requirements = remote ? [new("connection", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)] : [] }]
    };
    private static StorageProperties Properties(StorageTypeResource type)
    {
        var version = type.Definition.Versions.Single(v => v.Version == "1.0.0");
        return new()
        {
            DisplayName = "Storage",
            Configuration = Empty,
            Envelope = Empty,
            Type = new()
            {
                Type = new(type.Name, type.ScopeRef, type.Namespace),
                TypeUid = type.Uid,
                Version = version.Version,
                SchemaDigest = version.SchemaDigest,
                ContractDigest = StorageDigests.Version(version)
            }
        };
    }
    private static async Task<StorageTypeResource> CreateType(HttpClient client, ResourceScopeRef scope, bool remote = false)
    {
        using var response = await client.PostAsJsonAsync("/api/storage-types", new CreateStorageTypeRequest("test-type", TypeProperties(remote), scope));
        await Status(response, HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<StorageTypeResource>())!;
    }
    private static async Task<ResourceScopeRef> TenantScope(HttpClient client) =>
        (await client.GetFromJsonAsync<ResourceScopeTargetResponse[]>("/api/resource-scopes/targets?kind=Storage"))!
        .Single(t => t.Kind == ResourceScopeKind.Tenant).ScopeRef;
    private static string Path(StorageResource resource, string suffix = "") =>
        $"/api/storages/{resource.Name}{suffix}?scopeRef={Uri.EscapeDataString(resource.ScopeRef!.Value.Value)}&namespace={resource.Namespace}";
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body, string etag)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }
    private static async Task Status(HttpResponseMessage response, HttpStatusCode status) =>
        Assert.AreEqual(status, response.StatusCode, await response.Content.ReadAsStringAsync());
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        await Status(response, status);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(code, json.RootElement.GetProperty("code").GetString());
    }
    private sealed class NeverResolveSecrets : ISecretResolver
    {
        public int Calls { get; private set; }
        public Task<ResolvedSecret?> ResolveAsync(SecretReference reference, SecretResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Management must never resolve Secret values.");
        }
    }
}
