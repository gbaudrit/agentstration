using System.Net;
using System.Text.RegularExpressions;
using Agentstration.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class BootstrapStartupPageTests
{
    [TestMethod]
    [DataRow("/", "fr-FR", "Démarrage de l’instance")]
    [DataRow("/login?ReturnUrl=%2Fmodelproviders", "en-US", "Starting the instance")]
    [DataRow("/extensions", "fr-FR", "Démarrage de l’instance")]
    public async Task BrowserNavigationBeforeLoginShowsOnlyLocalizedWaitingMessage(string path, string culture, string message)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var startup = factory.Services.GetRequiredService<BootstrapStartupState>();
        startup.Begin();
        startup.Fail("private-bootstrap-failure: tenant-secret, provider-secret");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(culture);

        using var page = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.AreEqual("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(page.Headers.CacheControl?.NoStore);
        var html = await page.Content.ReadAsStringAsync();
        var main = MainContent().Match(html).Groups[1].Value;
        Assert.AreEqual(message, WebUtility.HtmlDecode(Tags().Replace(main, "")).Trim());
        StringAssert.Contains(html, $"lang=\"{culture}\"");
        StringAssert.Contains(html, "/startup/ready");
        StringAssert.Contains(html, "window.location.replace(loginUrl)");
        Assert.IsFalse(html.Contains("private-bootstrap-failure", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("bootstrap_initializing", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<link", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("src=", StringComparison.Ordinal));

        using var probe = await client.GetAsync("/startup/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, probe.StatusCode);
        Assert.AreEqual(string.Empty, await probe.Content.ReadAsStringAsync());
        Assert.IsTrue(probe.Headers.CacheControl?.NoStore);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoginProbeBecomesAvailableIndependentlyOfFullReadiness(bool completed)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var startup = factory.Services.GetRequiredService<BootstrapStartupState>();
        startup.Begin();
        using var pending = await client.GetAsync("/startup/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        if (completed) startup.Complete();
        else startup.EnableAdministration();

        using var probe = await client.GetAsync("/startup/ready");
        Assert.AreEqual(HttpStatusCode.NoContent, probe.StatusCode);
        Assert.AreEqual(string.Empty, await probe.Content.ReadAsStringAsync());
        Assert.IsTrue(probe.Headers.CacheControl?.NoStore);
        using var page = await client.GetAsync("/startup?ReturnUrl=%2Fmodelproviders");
        Assert.AreEqual(HttpStatusCode.Redirect, page.StatusCode);
        Assert.AreEqual("/login?ReturnUrl=%2Fmodelproviders", page.Headers.Location?.OriginalString);
        using var login = await client.GetAsync("/login");
        Assert.IsTrue(login.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect);
        Assert.IsFalse((await login.Content.ReadAsStringAsync()).Contains("/startup/ready", StringComparison.Ordinal));
        Assert.AreEqual(completed, startup.IsReady);
    }

    [TestMethod]
    [DataRow("/api/tools", "text/html", "GET")]
    [DataRow("/", "application/json", "GET")]
    [DataRow("/login", "text/html", "POST")]
    [DataRow("/", "text/html;q=0,application/json", "GET")]
    public async Task WaitingPageDoesNotBypassStartupFenceForApisOrMutations(string path, string accept, string method)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        factory.Services.GetRequiredService<BootstrapStartupState>().Begin();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Accept.ParseAdd(accept);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "bootstrap_initializing");
        using var health = await client.GetAsync("/health/live");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
    }

    [TestMethod]
    [DataRow("https://example.com/", "/")]
    [DataRow("//example.com/", "/")]
    [DataRow("/modelproviders", "/modelproviders")]
    public async Task StartupRedirectPreservesOnlyLocalReturnUrls(string returnUrl, string expected)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await client.GetAsync($"/startup?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual($"/login?ReturnUrl={Uri.EscapeDataString(expected)}", response.Headers.Location?.OriginalString);
    }

    private static WebApplicationFactory<global::Program> CreateFactory() =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.UseSetting("Agentstration:Authentication:Mode", "Local");
        });

    [GeneratedRegex("<main[^>]*>(.*?)</main>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex MainContent();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();
}
