using System.Net;
using Agentstration.Console.Web;
using Agentstration.Web.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Tests;

[TestClass]
public sealed class ConsoleHostTests
{
    [TestMethod]
    public async Task HealthEndpointsRemainAvailableWithoutTheAuthoritativeServer()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var route in new[] { "/health", "/health/live", "/health/ready" })
        {
            using var response = await client.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, route);
        }
    }

    [TestMethod]
    public async Task ConsoleShellOwnsRoutesAndSharedStaticAssets()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        var script = await client.GetStringAsync("/_content/Agentstration.Console.Components/app.js");
        var styles = await client.GetStringAsync("/_content/Agentstration.Console.Components/app.css");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "_framework/blazor.web.js");
        StringAssert.Contains(html, "_content/Agentstration.Console.Components/app.css");
        StringAssert.Contains(script, "agentstrationEnrollment");
        StringAssert.Contains(styles, ".task-summary-grid");
    }

    [TestMethod]
    public void ConsoleOwnsItsBrowserSessionAndIndependentApiOrigins()
    {
        using var factory = CreateFactory();
        var endpointOptions = factory.Services.GetRequiredService<IOptions<AgentstrationWebOptions>>().Value;
        var cookies = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ConsoleAuthenticationDefaults.Scheme);

        Assert.AreEqual("http://127.0.0.1:5101/", endpointOptions.ManagementApi.BaseAddress);
        Assert.AreEqual("http://127.0.0.1:5102/", endpointOptions.WorkApi.BaseAddress);
        Assert.AreEqual("http://127.0.0.1:5103/", endpointOptions.FlowApi.BaseAddress);
        Assert.AreEqual("http://127.0.0.1:5104/", endpointOptions.RuntimeApi.BaseAddress);
        Assert.AreEqual(ConsoleAuthenticationDefaults.Cookie, cookies.Cookie.Name);
        Assert.AreEqual("returnUrl", cookies.ReturnUrlParameter);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:ManagementApi:BaseAddress", "http://127.0.0.1:5101/");
            builder.UseSetting("Agentstration:ManagementApi:TimeoutSeconds", "1");
            builder.UseSetting("Agentstration:WorkApi:BaseAddress", "http://127.0.0.1:5102/");
            builder.UseSetting("Agentstration:WorkApi:TimeoutSeconds", "1");
            builder.UseSetting("Agentstration:FlowApi:BaseAddress", "http://127.0.0.1:5103/");
            builder.UseSetting("Agentstration:FlowApi:TimeoutSeconds", "1");
            builder.UseSetting("Agentstration:RuntimeApi:BaseAddress", "http://127.0.0.1:5104/");
            builder.UseSetting("Agentstration:RuntimeApi:TimeoutSeconds", "1");
        });
}
