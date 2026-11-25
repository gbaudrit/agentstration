using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class RuntimeWorkerSummaryReadTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SummaryReadsOneEnrollmentAndPreservesTheNativeWorkerId()
    {
        await using var factory = Factory();
        _ = await GetBootstrapContextAsync(factory);
        var id = Guid.NewGuid();
        await factory.Services.GetRequiredService<AwpWorkerIdentityService>().AnnounceAsync(
            new(id, "Saved worker", AwpWorkerAuthentication.ProtocolVersion), default);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/runtime/workers/{id:D}/summary");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var summary = await response.Content.ReadFromJsonAsync<RuntimeWorkerSummaryResponse>();
        Assert.IsNotNull(summary);
        Assert.AreEqual(id, summary.WorkerId);
        Assert.AreEqual("Saved worker", summary.DisplayName);
        Assert.AreEqual("Pending", summary.EnrollmentState);
        Assert.IsNull(summary.SessionId);
        Assert.IsEmpty(summary.Capabilities);
        using var missing = await client.GetAsync($"/api/runtime/workers/{Guid.NewGuid():D}/summary");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
