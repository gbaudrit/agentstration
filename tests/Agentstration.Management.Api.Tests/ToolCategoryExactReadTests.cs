using System.Net;
using System.Net.Http.Json;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ToolCategoryExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task ScopedCategoryGetKeepsNamespaceAndOwnerAndMissingTargetsDoNotFallBack()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, owner);
        var saved = new List<ToolCategoryResource>();
        foreach (var ns in new[] { "preview.categories", "other.categories" })
        {
            using var response = await client.PostAsJsonAsync("/api/toolcategories", new CreateToolCategoryRequest("same-category", new() { DisplayName = ns }, ns, owner));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            saved.Add((await response.Content.ReadFromJsonAsync<ToolCategoryResource>())!);
        }
        foreach (var expected in saved)
        {
            using var response = await client.GetAsync(Path(owner, expected.Namespace.Value));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var value = await response.Content.ReadFromJsonAsync<ToolCategoryResource>();
            Assert.IsNotNull(value);
            Assert.AreEqual(expected.Uid, value.Uid);
            Assert.AreEqual(owner, value.ScopeRef);
            Assert.IsNotNull(response.Headers.ETag);
        }
        using var missingNamespace = await client.GetAsync(Path(owner, "missing.namespace"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingNamespace.StatusCode);
        using var missingOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "preview.categories"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingOwner.StatusCode, await missingOwner.Content.ReadAsStringAsync());
    }

    private static string Path(ResourceScopeRef owner, string ns) =>
        $"/api/toolcategories/same-category?resourceNamespace={Uri.EscapeDataString(ns)}&scopeRef={Uri.EscapeDataString(owner.Value)}";
}
