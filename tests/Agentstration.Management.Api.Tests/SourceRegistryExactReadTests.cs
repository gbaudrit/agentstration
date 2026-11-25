using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Sources;
using Agentstration.Sources.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceRegistryExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task RegistrationPreviewReadReturnsSavedIdentityAndETagWithoutOwnerOrNamespaceFallback()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context);
        var stored = await factory.Services.GetRequiredService<SourceRegistryManagementService>().CreateAsync("preview-registry", new()
        {
            DisplayName = "Saved preview registry",
            IndexUrl = new("https://registry.invalid/index.json"),
            Enabled = false
        }, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        using var response = await client.GetAsync(Path(ResourceScopeRef.Instance, "default"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var resource = await response.Content.ReadFromJsonAsync<SourceRegistryRegistrationResource>();
        Assert.IsNotNull(resource);
        Assert.AreEqual(stored.Value.Uid, resource.Uid);
        Assert.AreEqual(stored.ETag, response.Headers.ETag?.ToString());
        Assert.IsFalse(resource.Definition.Enabled);
        using var missingNamespace = await client.GetAsync(Path(ResourceScopeRef.Instance, "missing"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingNamespace.StatusCode);
        using var missingOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "default"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingOwner.StatusCode);
    }

    private static string Path(ResourceScopeRef owner, string ns) =>
        $"/api/sourceregistries/preview-registry/registration?namespace={ns}&scopeRef={Uri.EscapeDataString(owner.Value)}";
}
