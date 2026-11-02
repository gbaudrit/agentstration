using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class HostFilteringTests
{
    [TestMethod]
    [DataRow(false, "localhost", HttpStatusCode.OK)]
    [DataRow(false, "192.168.1.50", HttpStatusCode.BadRequest)]
    [DataRow(false, "agentstration-lan", HttpStatusCode.BadRequest)]
    [DataRow(true, "192.168.1.50", HttpStatusCode.OK)]
    [DataRow(true, "agentstration-lan", HttpStatusCode.OK)]
    public async Task LanHostOverrideAcceptsNetworkAddressesWhileDefaultStaysLocal(
        bool lanEnabled, string host, HttpStatusCode expectedStatus)
    {
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            if (lanEnabled)
            {
                builder.UseSetting("AllowedHosts", "*");
            }
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        Assert.AreEqual(expectedStatus, response.StatusCode);
    }
}
