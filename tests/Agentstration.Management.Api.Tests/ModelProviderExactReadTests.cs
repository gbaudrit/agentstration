using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ModelProviderExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task ScopedGetDistinguishesHomonymsAndNeverFallsBackToAnotherOwnerOrNamespace()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context);
        var service = factory.Services.GetRequiredService<ModelProviderManagementService>();
        var baseline = await service.GetAsync(ResourceNamespace.Default, "ollama-local", default);
        Assert.IsNotNull(baseline);
        var properties = baseline.Value.Definition with { Extension = baseline.Value.Definition.Extension with { Namespace = ResourceNamespace.Default } };
        var resources = new List<ModelProviderResource>();
        foreach (var ns in new[] { new ResourceNamespace("preview.provider"), new ResourceNamespace("other.provider") })
        {
            var stored = await service.CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ModelResourceKinds.ModelProvider,
                Metadata = new() { Name = "preview-homonym", Namespace = ns },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = properties with { DisplayName = ns.Value }
            }, default);
            resources.Add(stored.Value);
        }
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        foreach (var expected in resources)
        {
            using var response = await client.GetAsync(Path(expected.ScopeRef!.Value, expected.Namespace.Value));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var value = await response.Content.ReadFromJsonAsync<ModelProviderResource>();
            Assert.IsNotNull(value);
            Assert.AreEqual(expected.Uid, value.Uid);
            Assert.AreEqual(expected.ScopeRef, value.ScopeRef);
            Assert.IsNotNull(response.Headers.ETag);
        }
        SelectConsoleContext(client, workspace);
        using var hidden = await client.GetAsync(Path(ResourceScopeRef.Instance, "preview.provider"));
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode, "An unshared Instance resource remains hidden from the Workspace context.");
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        using var missingNamespace = await client.GetAsync(Path(ResourceScopeRef.Instance, "missing.namespace"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingNamespace.StatusCode);
        using var missingOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "preview.provider"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingOwner.StatusCode);
    }

    private static string Path(ResourceScopeRef owner, string ns) =>
        $"/api/modelproviders/preview-homonym?resourceNamespace={Uri.EscapeDataString(ns)}&scopeRef={Uri.EscapeDataString(owner.Value)}";
}
