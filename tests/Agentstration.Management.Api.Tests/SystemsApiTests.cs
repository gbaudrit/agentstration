using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SystemsApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task LogicalMembershipPersistsWithExactIdentitiesAndEtagsWithoutChangingMembers()
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        var owner = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
        var ns = new ResourceNamespace("systems-fixture");
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        StoredResource<ModelProfileResource> member;
        using (scopes.PushSystem()) member = await store.PutExactAsync(owner, Profile(owner, ns), null, true, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        var response = await client.PostAsJsonAsync("/api/systems/", new CreateSystemRequest("my-system", "Payments", ScopeRef: owner, Namespace: ns));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        var system = (await response.Content.ReadFromJsonAsync<SystemView>())!;
        var path = $"/api/systems/my-system?scopeRef={Uri.EscapeDataString(owner.Value)}&namespace={ns.Value}";
        var membership = new SystemMember(member.Value.Uid, owner, ns, member.Value.Kind, member.Value.Name);
        using (var missing = await client.PostAsJsonAsync(path.Replace("?", "/members?", StringComparison.Ordinal), new AddSystemMemberRequest(membership)))
            Assert.AreEqual((HttpStatusCode)428, missing.StatusCode);
        var original = system;
        system = await MutateAsync(client, HttpMethod.Post, path.Replace("?", "/members?", StringComparison.Ordinal), system.ETag, new AddSystemMemberRequest(membership));
        Assert.HasCount(1, system.Members);
        using (var stale = Request(HttpMethod.Put, path, original.ETag, new RenameSystemRequest("New label")))
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
        var renamed = await MutateAsync(client, HttpMethod.Put, path, system.ETag, new RenameSystemRequest("<img src=x onerror=alert(1)>"));
        Assert.AreEqual(original.Uid, renamed.Uid);
        Assert.AreEqual(system.Generation + 1, renamed.Generation);
        Assert.AreEqual("<img src=x onerror=alert(1)>", renamed.DisplayName);
        var readBack = (await client.GetFromJsonAsync<SystemView>(path))!;
        Assert.AreEqual(membership, readBack.Members.Single());
        var graph = (await client.GetFromJsonAsync<SystemGraphPage>($"/api/system-graph?namespace={ns.Value}"))!;
        var edge = graph.Relations.Single(item => item.Kind == "system-membership");
        Assert.AreEqual(SystemGraphIds.Definition(original.Uid), edge.Source);
        Assert.AreEqual(SystemGraphIds.Definition(member.Value.Uid), edge.Target);
        var impact = (await client.GetFromJsonAsync<SystemGraphImpactResult>($"/api/system-graph/impact?namespace={ns.Value}&root={Uri.EscapeDataString(edge.Target)}"))!;
        Assert.IsFalse(impact.Nodes.Any(node => node.Identity.ResourceUid == original.Uid));
        using (var request = Request(HttpMethod.Delete, path, renamed.ETag, null))
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        using (scopes.PushSystem())
        {
            var unchanged = await store.GetExactAsync<ModelProfileResource>(new(owner, ns, member.Value.Kind, member.Value.Name), default);
            Assert.AreEqual(member.ETag, unchanged!.ETag);
            Assert.AreEqual(member.Value.Uid, unchanged.Value.Uid);
        }
    }

    [TestMethod]
    public async Task RevokedMemberIsFilteredAndCannotBeProbedOrReboundDuringPartialEdits()
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        var owner = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
        var ns = new ResourceNamespace("systems-visibility");
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        StoredResource<ModelProfileResource> member;
        using (scopes.PushSystem())
        {
            member = await store.PutExactAsync(ResourceScopeRef.Instance, Profile(ResourceScopeRef.Instance, ns), null, true, default);
            await policies.PutAsync(new(new(member.Value.Uid, ResourceScopeRef.Instance, ns, member.Value.Kind), [new(owner, [ResourceAccessAction.Discover])]), null, default);
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        var system = (await (await client.PostAsJsonAsync("/api/systems/", new CreateSystemRequest("visible-system", "Visible", ScopeRef: owner, Namespace: ns))).Content.ReadFromJsonAsync<SystemView>())!;
        var path = $"/api/systems/visible-system?scopeRef={Uri.EscapeDataString(owner.Value)}&namespace={ns.Value}";
        var membersPath = path.Replace("?", "/members?", StringComparison.Ordinal);
        var membership = new SystemMember(member.Value.Uid, ResourceScopeRef.Instance, ns, member.Value.Kind, member.Value.Name);
        system = await MutateAsync(client, HttpMethod.Post, membersPath, system.ETag, new AddSystemMemberRequest(membership));
        var graph = (await client.GetFromJsonAsync<SystemGraphPage>($"/api/system-graph?namespace={ns.Value}"))!;
        Assert.IsTrue(graph.Relations.Any(edge => edge.Kind == "system-membership")); // Discover is sufficient for logical membership.
        using (scopes.PushSystem())
        {
            var policy = (await policies.GetAsync(member.Value.Uid, default))!;
            await policies.PutAsync(new(new(member.Value.Uid, ResourceScopeRef.Instance, ns, member.Value.Kind), []), policy.ETag, default);
        }
        var filtered = (await client.GetFromJsonAsync<SystemView>(path))!;
        Assert.IsEmpty(filtered.Members);
        graph = (await client.GetFromJsonAsync<SystemGraphPage>($"/api/system-graph?namespace={ns.Value}"))!;
        Assert.IsFalse(graph.Nodes.Any(node => node.Identity.ResourceUid == member.Value.Uid));
        Assert.IsEmpty(graph.Relations.Where(edge => edge.Kind == "system-membership"));
        using (var probe = Request(HttpMethod.Post, membersPath, filtered.ETag, new AddSystemMemberRequest(membership)))
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(probe)).StatusCode);
        var renamed = await MutateAsync(client, HttpMethod.Put, path, filtered.ETag, new RenameSystemRequest("Renamed"));
        Assert.IsEmpty(renamed.Members);
        using (scopes.PushSystem())
        {
            var persisted = (await store.GetExactAsync<SystemResource>(new(owner, ns, SystemResourceKinds.System, "visible-system"), default))!;
            Assert.AreEqual(membership, persisted.Value.Definition.Members.Single());
        }
    }

    [TestMethod]
    public async Task DeletedRecreatedAndForeignIdentitiesCannotBeAddedByNameOrUid()
    {
        await using var factory = Factory();
        var bootstrap = await GetBootstrapContextAsync(factory);
        var owner = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
        var ns = new ResourceNamespace("systems-identities");
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        StoredResource<ModelProfileResource> old;
        using (scopes.PushSystem())
        {
            old = await store.PutExactAsync(owner, Profile(owner, ns), null, true, default);
            await store.DeleteExactAsync(new(owner, ns, old.Value.Kind, old.Value.Name), old.ETag, default);
            var replacement = await store.PutExactAsync(owner, Profile(owner, ns), null, true, default);
            Assert.AreNotEqual(old.Value.Uid, replacement.Value.Uid);
        }
        using var client = factory.CreateClient(); SelectConsoleContext(client, owner);
        var system = (await (await client.PostAsJsonAsync("/api/systems/", new CreateSystemRequest("identity-system", "Identity", ScopeRef: owner, Namespace: ns))).Content.ReadFromJsonAsync<SystemView>())!;
        var path = $"/api/systems/identity-system/members?scopeRef={Uri.EscapeDataString(owner.Value)}&namespace={ns.Value}";
        foreach (var member in new[] { new SystemMember(old.Value.Uid, owner, ns, old.Value.Kind, old.Value.Name),
            new SystemMember(old.Value.Uid, ResourceScopeRef.Tenant(Guid.NewGuid()), ns, old.Value.Kind, old.Value.Name) })
        {
            using var request = Request(HttpMethod.Post, path, system.ETag, new AddSystemMemberRequest(member));
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(request)).StatusCode);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string etag, object? body)
    {
        var request = new HttpRequestMessage(method, path); request.Headers.IfMatch.ParseAdd(etag);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
    private static async Task<SystemView> MutateAsync(HttpClient client, HttpMethod method, string path, string etag, object body)
    {
        using var request = Request(method, path, etag, body);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SystemView>())!;
    }
    private static ModelProfileResource Profile(ResourceScopeRef owner, ResourceNamespace ns) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ModelResourceKinds.ModelProfile,
        ScopeRef = owner,
        Metadata = new() { Name = "same-name", Namespace = ns },
        Generation = 1,
        Definition = new() { DisplayName = "Member", Provider = new("metadata-only", ResourceScopeRef.Instance), Model = new() { Name = "deterministic" } }
    };
}
