using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Storage;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class StorageTypeAdministrationApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task PublicationAndLifecycleUseEtagsAndRetainPinnedStorageVersions()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var scope = ResourceScopeRef.Tenant(context.TenantId);
        SelectConsoleContext(client, scope);
        using var created = await client.PostAsJsonAsync("/api/storage-types", new CreateStorageTypeRequest("versioned", Definition(), scope));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var type = (await created.Content.ReadFromJsonAsync<StorageTypeResource>())!;
        var version = type.Definition.Versions.Single();
        using var storageResponse = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("consumer", new()
        {
            DisplayName = "Consumer", Type = new() { Type = new(type.Name, scope), TypeUid = type.Uid,
                Version = version.Version, SchemaDigest = version.SchemaDigest, ContractDigest = version.ContractDigest },
            Configuration = JsonSerializer.SerializeToElement(new { }), Envelope = JsonSerializer.SerializeToElement(new { })
        }, scope));
        Assert.AreEqual(HttpStatusCode.Created, storageResponse.StatusCode);
        var path = TypePath(type);
        using var missing = await client.PostAsJsonAsync(path.Replace("?", "/versions?", StringComparison.Ordinal),
            new PublishStorageTypeVersionRequest(version with { Version = "2.0.0" }));
        Assert.AreEqual((HttpStatusCode)428, missing.StatusCode);
        using var publication = await Send(client, HttpMethod.Post, path.Replace("?", "/versions?", StringComparison.Ordinal),
            new PublishStorageTypeVersionRequest(version with { Version = "2.0.0" }, true), type.ETag!);
        Assert.AreEqual(HttpStatusCode.OK, publication.StatusCode);
        var published = (await publication.Content.ReadFromJsonAsync<StorageTypeResource>())!;
        Assert.HasCount(2, published.Definition.Versions);
        Assert.AreEqual("2.0.0", published.Definition.PreferredVersion);
        using var duplicate = await Send(client, HttpMethod.Post, path.Replace("?", "/versions?", StringComparison.Ordinal),
            new PublishStorageTypeVersionRequest(version), published.ETag!);
        await Problem(duplicate, "storage_type_version_exists");
        using var stale = await Send(client, HttpMethod.Put, path, new PutStorageTypeRequest(published.Definition), type.ETag!);
        await Problem(stale, "storage_version_conflict");
        using var disable = await Send(client, HttpMethod.Put, path,
            new PutStorageTypeRequest(published.Definition with { Enabled = false }), published.ETag!);
        await Problem(disable, "storage_type_in_use");
        using var delete = await Send(client, HttpMethod.Delete, path, null, published.ETag!);
        await Problem(delete, "storage_type_in_use");
        var usages = await client.GetFromJsonAsync<ValueResponse<StorageUsage>>(path.Replace("?", "/usages?", StringComparison.Ordinal));
        Assert.AreEqual("consumer", usages!.Value.Single().Name);
        var storage = await client.GetFromJsonAsync<StorageResource>($"/api/storages/consumer?scopeRef={Uri.EscapeDataString(scope.Value)}");
        Assert.AreEqual("1.0.0", storage!.Definition.Type.Version);
        using var mutation = await Send(client, HttpMethod.Put, path, new PutStorageTypeRequest(published.Definition with
        {
            Versions = [version with { Capabilities = new() { Write = true } }]
        }), published.ETag!);
        await Problem(mutation, "storage_type_version_immutable", HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public async Task GenericDiscoverAndUseAreIndependentAndHiddenExactReadsStayHidden(bool discover, bool use)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var owner = ResourceScopeRef.Tenant(context.TenantId);
        var consumer = ResourceScopeRef.Workspace(context.WorkspaceId);
        SelectConsoleContext(client, owner);
        using var response = await client.PostAsJsonAsync("/api/storage-types", new CreateStorageTypeRequest("shared", Definition(), owner));
        var type = (await response.Content.ReadFromJsonAsync<StorageTypeResource>())!;
        var actions = new List<ResourceAccessAction>();
        if (discover) actions.Add(ResourceAccessAction.Discover);
        if (use) actions.Add(ResourceAccessAction.Use);
        var identity = new ResourceAccessIdentity(type.Uid, owner, type.Namespace, type.Kind);
        using var policy = await client.PutAsJsonAsync($"/api/resource-access-policies/{type.Uid}",
            new ResourceAccessPolicy(identity, actions.Count == 0 ? [] : [new(consumer, actions)]));
        Assert.AreEqual(HttpStatusCode.OK, policy.StatusCode);
        foreach (var action in Enum.GetValues<ResourceAccessAction>())
        {
            using var decisionResponse = await client.PostAsJsonAsync($"/api/resource-access-policies/{type.Uid}/evaluate", new EvaluateResourceAccessRequest(identity, consumer, action));
            var decision = (await decisionResponse.Content.ReadFromJsonAsync<ResourceAccessDecision>())!;
            Assert.AreEqual(action == ResourceAccessAction.Discover ? discover : use, decision.Allowed);
        }
        SelectConsoleContext(client, consumer);
        var list = await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types?take=1");
        Assert.AreEqual(discover, list!.Value.Any(value => value.Uid == type.Uid));
        if (discover)
        {
            var observed = list.Value.Single(value => value.Uid == type.Uid).ObservedUse!;
            Assert.AreEqual(consumer, observed.ConsumerScopeRef);
            Assert.AreEqual(use, observed.Use.Allowed);
        }
        using var exact = await client.GetAsync(TypePath(type));
        Assert.AreEqual(discover ? HttpStatusCode.OK : HttpStatusCode.NotFound, exact.StatusCode);
    }

    [TestMethod]
    public async Task BuiltinGrantsRemainIndependentFromProtectedDefinitionAndUsePolicy()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        await factory.Services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var types = await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types");
        var type = types!.Value.Single(value => value.Name == StoragePlatformResourceProvisioner.LocalTypeName);
        var identity = new ResourceAccessIdentity(type.Uid, type.ScopeRef!.Value, type.Namespace, type.Kind);
        var policyPath = $"/api/resource-access-policies/{type.Uid}";
        var policy = new ResourceAccessPolicy(identity, [new(workspace, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]);
        using var put = await client.PutAsJsonAsync(policyPath, policy);
        Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
        var etag = put.Headers.ETag!.ToString();
        using var overwriteWithoutEtag = await client.PutAsJsonAsync(policyPath, policy with { Grants = [] });
        Assert.AreEqual(HttpStatusCode.Conflict, overwriteWithoutEtag.StatusCode);
        using var deleteWithoutEtag = await client.DeleteAsync(policyPath);
        Assert.AreEqual(HttpStatusCode.Conflict, deleteWithoutEtag.StatusCode);
        using var denied = await Send(client, HttpMethod.Put, TypePath(type), new PutStorageTypeRequest(type.Definition with { DisplayName = "Forged" }), type.ETag!);
        Assert.AreEqual(HttpStatusCode.Conflict, denied.StatusCode);
        using var deniedVersion = await Send(client, HttpMethod.Post, TypePath(type).Replace("?", "/versions?", StringComparison.Ordinal),
            new PublishStorageTypeVersionRequest(type.Definition.Versions.Single() with { Version = "2.0.0" }), type.ETag!);
        Assert.AreEqual(HttpStatusCode.Conflict, deniedVersion.StatusCode);
        await factory.Services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        using var unchanged = await client.GetAsync(TypePath(type));
        Assert.AreEqual(type.ETag, unchanged.Headers.ETag!.ToString());
        using var savedPolicy = await client.GetAsync(policyPath);
        Assert.AreEqual(etag, savedPolicy.Headers.ETag!.ToString());
        SelectConsoleContext(client, workspace);
        Assert.IsTrue((await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types"))!.Value.Any(value => value.Uid == type.Uid));
        var version = type.Definition.Versions.Single();
        using var cannotBypass = await client.PostAsJsonAsync("/api/storages", new CreateStorageRequest("cannot-bypass", new()
        {
            DisplayName = "Cannot bypass", Type = new() { Type = new(type.Name, type.ScopeRef), TypeUid = type.Uid,
                Version = version.Version, SchemaDigest = version.SchemaDigest, ContractDigest = version.ContractDigest },
            Configuration = JsonSerializer.SerializeToElement(new { }), Envelope = JsonSerializer.SerializeToElement(new { })
        }, workspace));
        Assert.AreEqual(HttpStatusCode.Forbidden, cannotBypass.StatusCode);
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        using var revoke = await Send(client, HttpMethod.Put, policyPath, policy with { Grants = [] }, etag);
        Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode);
        await factory.Services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        SelectConsoleContext(client, workspace);
        Assert.IsFalse((await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types"))!.Value.Any(value => value.Uid == type.Uid));
    }

    [TestMethod]
    public async Task GenerationPinnedDiscoveryFailsClosedAfterMetadataUpdate()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var scope = ResourceScopeRef.Tenant(context.TenantId);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        SelectConsoleContext(client, scope);
        using var response = await client.PostAsJsonAsync("/api/storage-types", new CreateStorageTypeRequest("pinned", Definition(), scope));
        var type = (await response.Content.ReadFromJsonAsync<StorageTypeResource>())!;
        using var policy = await client.PutAsJsonAsync($"/api/resource-access-policies/{type.Uid}", new ResourceAccessPolicy(
            new(type.Uid, scope, type.Namespace, type.Kind, type.Generation), [new(workspace, [ResourceAccessAction.Discover])]));
        Assert.AreEqual(HttpStatusCode.OK, policy.StatusCode);
        SelectConsoleContext(client, workspace);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(TypePath(type))).StatusCode);
        SelectConsoleContext(client, scope);
        using var update = await Send(client, HttpMethod.Put, TypePath(type), new PutStorageTypeRequest(type.Definition with { DisplayName = "New metadata" }), type.ETag!);
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
        SelectConsoleContext(client, workspace);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(TypePath(type))).StatusCode);
        Assert.IsFalse((await client.GetFromJsonAsync<PagedResponse<StorageTypeResource>>("/api/storage-types"))!.Value.Any(value => value.Uid == type.Uid));
    }

    private static StorageTypeProperties Definition()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
        return new() { DisplayName = "Type", PreferredVersion = "1.0.0", Versions = [new()
        {
            Version = "1.0.0", ConfigurationSchema = schema, SchemaDigest = StorageDigests.Schema(schema),
            EnvelopeSchema = schema, EnvelopeSchemaDigest = StorageDigests.Schema(schema)
        }] };
    }
    private static string TypePath(StorageTypeResource type) => $"/api/storage-types/{type.Name}?scopeRef={Uri.EscapeDataString(type.ScopeRef!.Value.Value)}&namespace={type.Namespace}";
    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body, string etag)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.IfMatch.ParseAdd(etag);
        return await client.SendAsync(request);
    }
    private static async Task Problem(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.Conflict)
    {
        Assert.AreEqual(status, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(code, json.RootElement.GetProperty("code").GetString());
    }
}
