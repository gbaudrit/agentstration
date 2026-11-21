using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class TriggerExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task ExplicitPhysicalOwnerMustMatchTheCurrentOperationalContext()
    {
        await using var factory = Factory();
        _ = await GetBootstrapContextAsync(factory);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var context = await client.GetFromJsonAsync<JsonElement>("/api/identity/context");
        var owner = ResourceScopeRef.Workspace(context.GetProperty("context").GetProperty("workspaceId").GetGuid());
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var stored = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(owner, new TriggerResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = TriggerResourceKinds.Trigger,
            ScopeRef = owner,
            Metadata = new() { Name = "preview-trigger" },
            Definition = new()
            {
                DisplayName = "Saved Trigger",
                Enabled = false,
                Source = new() { Schedule = new() { Type = TriggerScheduleType.Interval, Every = "PT1H" } },
                Target = new() { Flow = new() { Name = "saved-flow", ScopeRef = ResourceScopeRef.Instance } }
            }
        }, null, true, default);
        using var response = await client.GetAsync(Path(owner));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var resource = await response.Content.ReadFromJsonAsync<TriggerResource>();
        Assert.AreEqual(stored.Value.Uid, resource?.Uid);
        Assert.AreEqual(stored.ETag, response.Headers.ETag?.ToString());
        var otherNamespace = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(owner, stored.Value with
        {
            Uid = Guid.Empty,
            Metadata = stored.Value.Metadata with { Namespace = new("preview") }
        }, null, true, default);
        using var namespaced = await client.GetAsync($"/api/namespaces/preview/triggers/preview-trigger?scopeRef={Uri.EscapeDataString(owner.Value)}");
        Assert.AreEqual(HttpStatusCode.OK, namespaced.StatusCode, await namespaced.Content.ReadAsStringAsync());
        Assert.AreEqual(otherNamespace.Value.Uid, (await namespaced.Content.ReadFromJsonAsync<TriggerResource>())?.Uid);
        Assert.AreNotEqual(stored.Value.Uid, otherNamespace.Value.Uid);
        using var missingNamespace = await client.GetAsync($"/api/namespaces/missing/triggers/preview-trigger?scopeRef={Uri.EscapeDataString(owner.Value)}");
        Assert.AreEqual(HttpStatusCode.NotFound, missingNamespace.StatusCode);
        using var wrongOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid())));
        Assert.AreEqual(HttpStatusCode.NotFound, wrongOwner.StatusCode);
        using var logicalOwner = await client.GetAsync(Path(ResourceScopeRef.Instance));
        Assert.AreEqual(HttpStatusCode.NotFound, logicalOwner.StatusCode);
    }

    private static string Path(ResourceScopeRef owner) => $"/api/triggers/preview-trigger?scopeRef={Uri.EscapeDataString(owner.Value)}";
}
