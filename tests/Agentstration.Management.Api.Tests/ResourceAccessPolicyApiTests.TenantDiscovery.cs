using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class ResourceAccessPolicyApiTests
{
    [TestMethod]
    [DataRow("modelproviders", false)]
    [DataRow("modelproviders", true)]
    [DataRow("modelprofiles", false)]
    [DataRow("modelprofiles", true)]
    public async Task TenantDiscoveryOpensTheSameResourceAndOnlyPropagatesWhenRequested(string collection, bool includeDescendants)
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        Resource resource;
        ResourceAccessIdentity identity;
        using (scopes.PushInstance(context))
        {
            resource = collection == "modelproviders"
                ? (await store.ListAllAsync<ModelProviderResource>(ModelResourceKinds.ModelProvider, default)).First().Value
                : (await store.ListAllAsync<ModelProfileResource>(ModelResourceKinds.ModelProfile, default)).First().Value;
            identity = new(resource.Uid, ResourceScopeRef.Instance, resource.Namespace, resource.Kind, resource.Generation);
            await policies.PutAsync(new(identity, [new(ResourceScopeRef.Tenant(context.TenantId),
                [ResourceAccessAction.Discover], includeDescendants)]), (await policies.GetAsync(resource.Uid, default))?.ETag, default);
        }
        using var client = factory.CreateClient();
        var path = $"/api/{collection}/{Uri.EscapeDataString(resource.Name)}?scopeRef=%2Finstance&resourceNamespace={Uri.EscapeDataString(resource.Namespace.Value)}";
        SelectConsoleContext(client, ResourceScopeRef.Tenant(context.TenantId));
        using var tenantRead = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, tenantRead.StatusCode, await tenantRead.Content.ReadAsStringAsync());
        var saved = await tenantRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(resource.Uid, saved.GetProperty("uid").GetGuid());
        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/{collection}");
        Assert.IsTrue(listed.GetProperty("value").EnumerateArray().Any(value => value.GetProperty("name").GetString() == resource.Name));
        using var useDecision = await client.PostAsJsonAsync($"/api/resource-access-policies/{resource.Uid}/evaluate",
            new EvaluateResourceAccessRequest(identity, ResourceScopeRef.Tenant(context.TenantId), ResourceAccessAction.Use));
        Assert.AreEqual(HttpStatusCode.OK, useDecision.StatusCode);
        Assert.IsFalse((await useDecision.Content.ReadFromJsonAsync<ResourceAccessDecision>())!.Allowed);
        SelectConsoleContext(client, ResourceScopeRef.Workspace(context.WorkspaceId));
        using var workspaceRead = await client.GetAsync(path);
        Assert.AreEqual(includeDescendants ? HttpStatusCode.OK : HttpStatusCode.NotFound, workspaceRead.StatusCode);
        using (scopes.PushInstance(context))
            await policies.PutAsync(new(identity, []), (await policies.GetAsync(resource.Uid, default))?.ETag, default);
        SelectConsoleContext(client, ResourceScopeRef.Tenant(context.TenantId));
        using var revoked = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.NotFound, revoked.StatusCode);
    }
}
