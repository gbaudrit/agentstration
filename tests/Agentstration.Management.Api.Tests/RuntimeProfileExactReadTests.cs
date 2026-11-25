using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class RuntimeProfileExactReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task ScopedGetDistinguishesHomonymsAndNeverFallsBackToAnotherOwnerOrNamespace()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context);
        var service = factory.Services.GetRequiredService<RuntimeProfileManagementService>();
        var resources = new List<RuntimeProfileResource>();
        foreach (var ns in new[] { new ResourceNamespace("preview.runtime"), new ResourceNamespace("other.runtime") })
        {
            var stored = await service.CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = RuntimeProfileResourceKinds.RuntimeProfile,
                Metadata = new() { Name = "preview-homonym", Namespace = ns },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new() { DisplayName = ns.Value, RuntimeType = "microsoft-agent-framework" }
            }, default);
            resources.Add(stored.Value);
        }
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        foreach (var expected in resources)
        {
            using var response = await client.GetAsync(Path(expected.ScopeRef!.Value, expected.Namespace.Value));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var value = await response.Content.ReadFromJsonAsync<RuntimeProfileResource>();
            Assert.IsNotNull(value);
            Assert.AreEqual(expected.Uid, value.Uid);
            Assert.AreEqual(expected.ScopeRef, value.ScopeRef);
            Assert.IsNotNull(response.Headers.ETag);
        }
        SelectConsoleContext(client, workspace);
        using var hidden = await client.GetAsync(Path(ResourceScopeRef.Instance, "preview.runtime"));
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode, "An unshared Instance resource remains hidden from the Workspace context.");
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        using var missingNamespace = await client.GetAsync(Path(ResourceScopeRef.Instance, "missing.namespace"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingNamespace.StatusCode);
        using var missingOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "preview.runtime"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingOwner.StatusCode);
    }

    private static string Path(ResourceScopeRef owner, string ns) =>
        $"/api/runtimeprofiles/preview-homonym?resourceNamespace={Uri.EscapeDataString(ns)}&scopeRef={Uri.EscapeDataString(owner.Value)}";
}
