using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceSummaryExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SummaryReadsOnlyTheExactSourceAndItsMatchingConfiguration()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var expected = new List<SourceResource>();
        foreach (var publisher in new[] { "preview-publisher", "other-publisher" })
        {
            var source = (await store.CreateImmutableAsync(new SourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SourceResourceKinds.Source,
                ScopeRef = ResourceScopeRef.Instance,
                Metadata = new() { Name = "homonym", Namespace = new(publisher) },
                Definition = new() { Publisher = publisher }
            }, default)).Value;
            await store.PutExactAsync(ResourceScopeRef.Instance, new SourceConfigurationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SourceResourceKinds.SourceConfiguration,
                ScopeRef = ResourceScopeRef.Instance,
                Metadata = source.Metadata,
                Definition = new() { SourceUid = source.Uid, DisplayName = publisher }
            }, null, true, default);
            expected.Add(source);
        }
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        foreach (var source in expected)
        {
            using var response = await client.GetAsync(Path(ResourceScopeRef.Instance, source.Namespace.Value));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<SourceSavedSummarySnapshot>();
            Assert.IsNotNull(result);
            Assert.AreEqual(source.Uid, result.Resource.Uid);
            Assert.AreEqual(source.Uid, result.Configuration.Definition.SourceUid);
            Assert.AreEqual(source.Namespace, result.Configuration.Namespace);
        }
        using var missingNamespace = await client.GetAsync(Path(ResourceScopeRef.Instance, "missing-publisher"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingNamespace.StatusCode);
        using var missingOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "preview-publisher"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingOwner.StatusCode);
    }

    private static string Path(ResourceScopeRef owner, string publisher) =>
        $"/api/sources/{publisher}/homonym/summary?scopeRef={Uri.EscapeDataString(owner.Value)}";
}
