using Agentstration.Web;
using Agentstration.Web.Configuration;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class ApiAuthenticationRegistrationTests
{
    [TestMethod]
    public void OidcApisPreferBearerButAcceptTheTrustedConsoleSession()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Authentication:Mode"] = ApiAuthenticationOptions.Oidc,
            ["Agentstration:Authentication:Authority"] = "https://identity.example/",
            ["Agentstration:Authentication:Audience"] = "agentstration-api",
            ["Agentstration:Authentication:ClientId"] = "agentstration-console"
        }).Build();
        services.AddLogging();
        services.AddAgentstrationApi(configuration, new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        var selector = provider.GetRequiredService<IOptionsMonitor<PolicySchemeOptions>>()
            .Get(AgentstrationAuthenticationDefaults.PolicyScheme).ForwardDefaultSelector;
        Assert.IsNotNull(selector);

        var api = new DefaultHttpContext();
        api.Request.Path = "/api/agents";
        Assert.AreEqual(JwtBearerDefaults.AuthenticationScheme, selector(api));

        api.Request.Headers.Cookie = $"{AgentstrationAuthenticationDefaults.ApplicationCookie}=session";
        Assert.AreEqual(IdentityConstants.ApplicationScheme, selector(api));

        api.Request.Headers.Authorization = "Bearer access-token";
        Assert.AreEqual(JwtBearerDefaults.AuthenticationScheme, selector(api));
    }

    [TestMethod]
    public async Task CookieAuthenticationReturnsStatusCodeInsteadOfHtmlRedirectForHubs()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Authentication:Mode"] = ApiAuthenticationOptions.Local
        }).Build();
        services.AddLogging();
        services.AddAgentstrationApi(configuration, new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/hubs/flow-runs/negotiate";
        var redirect = new RedirectContext<CookieAuthenticationOptions>(
            httpContext,
            new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler)),
            options,
            new AuthenticationProperties(),
            "/login");

        await options.Events.OnRedirectToLogin(redirect);

        Assert.AreEqual(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
        Assert.IsFalse(httpContext.Response.Headers.ContainsKey("Location"));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = nameof(ApiAuthenticationRegistrationTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
