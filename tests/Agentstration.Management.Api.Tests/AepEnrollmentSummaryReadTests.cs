using System.Net;
using System.Net.Http.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AepEnrollmentSummaryReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task EnrollmentSummaryUsesItsExactAddressAndExistingSanitization()
    {
        await using var factory = Factory();
        _ = await GetBootstrapContextAsync(factory);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var id = Guid.NewGuid();
        var stored = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(ResourceScopeRef.Instance, new AepEnrollmentRequestResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.AepEnrollmentRequest,
            Metadata = new() { Name = id.ToString("N") },
            Definition = new()
            {
                InstanceId = id,
                TargetScopeRef = ResourceScopeRef.Instance,
                ExtensionId = "preview-extension",
                ExtensionName = "Saved enrollment",
                ExtensionVersion = "1.0.0",
                Endpoint = new("https://preview.invalid/aep"),
                CodeSalt = "fixture-private-salt",
                CodeDigest = "fixture-private-digest",
                CompletionDigest = "fixture-private-completion"
            }
        }, null, true, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        string Path(ResourceScopeRef scope, string ns) => $"/api/aep/enrollments/{id:D}/summary?scopeRef={Uri.EscapeDataString(scope.Value)}&namespace={ns}";
        using var response = await client.GetAsync(Path(ResourceScopeRef.Instance, "default"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var snapshot = await response.Content.ReadFromJsonAsync<AepEnrollmentRequestResource>();
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(stored.Value.Uid, snapshot.Uid);
        Assert.AreEqual(id, snapshot.Definition.InstanceId);
        Assert.AreEqual(stored.ETag, response.Headers.ETag?.ToString());
        Assert.IsNull(snapshot.Definition.CodeSalt);
        Assert.IsNull(snapshot.Definition.CodeDigest);
        Assert.IsNull(snapshot.Definition.CompletionDigest);
        using var otherOwner = await client.GetAsync(Path(ResourceScopeRef.Workspace(Guid.NewGuid()), "default"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherOwner.StatusCode);
        using var otherNamespace = await client.GetAsync(Path(ResourceScopeRef.Instance, "missing"));
        Assert.AreEqual(HttpStatusCode.NotFound, otherNamespace.StatusCode);
    }
}
