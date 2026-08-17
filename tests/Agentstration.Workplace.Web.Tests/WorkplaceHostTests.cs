using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Workplace.Web.Tests;

[TestClass]
public sealed class WorkplaceHostTests
{
    [TestMethod]
    public async Task NotificationsShowsRecoverableStateWhenWorkApiIsUnavailable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:ApiBaseUrl", "http://127.0.0.1:1/");
            builder.UseSetting("Agentstration:WorkplaceHubUrl", "http://127.0.0.1:1/hubs/workplace");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/w/personal/notifications");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "Notifications could not be loaded from the local Work API.");
        StringAssert.Contains(html, "Try again");
    }
}
