using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Packs.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class PackProjectExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SavedProjectReadUsesItsExactOwnerNamespaceAndUidWithoutOpeningArtifacts()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        var id = Guid.NewGuid();
        var stored = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(owner, new PackProjectResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = PackAuthoringKinds.PackProject,
            Uid = id,
            ScopeRef = owner,
            Metadata = new() { Name = id.ToString("N") },
            Definition = new() { Publisher = "preview", PackName = "project", Version = "1.0.0-dev.1", SourceArtifact = new("not-present", "saved-hash", 0, "fixture.zip") }
        }, null, true, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        string Path(ResourceScopeRef scope, string ns) => $"/api/pack-projects/resources/{id:N}?scopeRef={Uri.EscapeDataString(scope.Value)}&namespace={ns}";
        using var response = await client.GetAsync(Path(owner, "default"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var resource = await response.Content.ReadFromJsonAsync<PackProjectResource>();
        Assert.IsNotNull(resource);
        Assert.AreEqual(stored.Value.Uid, resource.Uid);
        Assert.AreEqual(id.ToString("N"), resource.Name);
        Assert.AreEqual(owner, resource.ScopeRef);
        Assert.AreEqual("saved-hash", resource.Definition.SourceArtifact.Sha256);
        Assert.AreEqual(stored.ETag, response.Headers.ETag?.ToString());
        using var otherOwner = await client.GetAsync(Path(ResourceScopeRef.Instance, "default"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherOwner.StatusCode);
        using var otherNamespace = await client.GetAsync(Path(owner, "missing"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherNamespace.StatusCode);
    }
}
