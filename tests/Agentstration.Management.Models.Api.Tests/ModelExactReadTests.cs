using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ModelExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SavedModelSummaryUsesExactInstanceNamespaceAndNameWithoutProviderLookup()
    {
        await using var factory = Factory();
        _ = await GetBootstrapContextAsync(factory);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var saved = await store.PutExactAsync(ResourceScopeRef.Instance, new ModelResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelResourceKinds.Model,
            Metadata = new() { Name = "preview-model", Namespace = ResourceNamespace.Parse("preview") },
            Definition = new()
            {
                DisplayName = "Saved model",
                Provider = new("absent-provider", ResourceScopeRef.Instance),
                ProviderUid = Guid.NewGuid(),
                ExternalId = "saved-id",
                ProviderStatus = "available",
                Specification = new(),
                Observation = new() { FirstObservedAt = DateTimeOffset.UnixEpoch, LastObservedAt = DateTimeOffset.UnixEpoch, LastAttemptedAt = DateTimeOffset.UnixEpoch }
            }
        }, null, true, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        string Path(ResourceScopeRef owner, string ns) => $"/api/models/preview-model?resourceNamespace={ns}&scopeRef={Uri.EscapeDataString(owner.Value)}";
        using var response = await client.GetAsync(Path(ResourceScopeRef.Instance, "preview"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var model = await response.Content.ReadFromJsonAsync<ModelResource>();
        Assert.IsNotNull(model);
        Assert.AreEqual(saved.Value.Uid, model.Uid);
        Assert.AreEqual(saved.ETag, response.Headers.ETag?.ToString());
        Assert.AreEqual("absent-provider", model.Definition.Provider.Name);
        using var otherNamespace = await client.GetAsync(Path(ResourceScopeRef.Instance, "default"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherNamespace.StatusCode);
        using var otherOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "preview"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherOwner.StatusCode);
    }
}
