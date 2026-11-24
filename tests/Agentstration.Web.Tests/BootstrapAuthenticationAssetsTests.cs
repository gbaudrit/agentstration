using System.Net;
using System.Text.RegularExpressions;
using Agentstration.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class BootstrapAuthenticationAssetsTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AuthenticationStylesAndFormScriptRemainAvailableWhileBootstrapIsNotReady(bool failed)
    {
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseStaticWebAssets();
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.UseSetting("Agentstration:Authentication:Mode", "Local");
        });
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var startup = factory.Services.GetRequiredService<BootstrapStartupState>();
        startup.Begin();
        startup.EnableAdministration();
        if (failed) startup.Fail("model_dependency_timeout");

        // A fresh local instance uses the same authentication layout for first-admin setup.
        // Follow the login redirect so the test reads the actual rendered CSS URLs.
        using var login = await client.GetAsync("/login?ReturnUrl=%2F");
        using var page = login.StatusCode == HttpStatusCode.Redirect
            ? await client.GetAsync(login.Headers.Location)
            : await client.GetAsync("/login?ReturnUrl=%2F");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var styles = Stylesheet().Matches(html).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
        Assert.IsTrue(styles.Any(path => path.StartsWith("/auth.css", StringComparison.Ordinal)));
        foreach (var path in styles)
        {
            using var css = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, css.StatusCode, path);
            Assert.AreEqual("text/css", css.Content.Headers.ContentType?.MediaType, path);
        }
        using var authenticationCss = await client.GetAsync("/auth.css?v=bootstrap-regression");
        StringAssert.Contains(await authenticationCss.Content.ReadAsStringAsync(), ".auth-brand img");
        using var script = await client.GetAsync("/resource-name-fields.js");
        Assert.AreEqual(HttpStatusCode.OK, script.StatusCode);
        Assert.IsTrue(script.Content.Headers.ContentType?.MediaType is "text/javascript" or "application/javascript");
        // Inspection reaches normal authentication; execution remains fenced.
        using var inspection = await client.GetAsync("/api/tools");
        Assert.AreEqual(HttpStatusCode.Unauthorized, inspection.StatusCode);
        using var execution = await client.PostAsync("/api/tools/unknown/run", null);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, execution.StatusCode);
        using var disguisedExecution = await client.PostAsync("/api/tools/auth.css", null);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, disguisedExecution.StatusCode);
        Assert.IsFalse(startup.IsReady);
    }

    [GeneratedRegex("<link[^>]*rel=\"stylesheet\"[^>]*href=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Stylesheet();
}
