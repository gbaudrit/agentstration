using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Agentstration.Console.Web;
using Agentstration.Console.Web.Security;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Http;

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
    public async Task ConsoleShellProtectsRoutesAndOwnsLocalizedLoginAndStaticAssets()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/");
        using var login = await client.GetAsync("/login");
        var html = await login.Content.ReadAsStringAsync();
        var script = await client.GetStringAsync("/_content/Agentstration.Console.Components/app.js");
        var styles = await client.GetStringAsync("/_content/Agentstration.Console.Components/app.css");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/login", response.Headers.Location?.AbsolutePath);
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        StringAssert.Contains(html, "Sign in");
        StringAssert.Contains(html, "__RequestVerificationToken");
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
        Assert.AreEqual(CookieSecurePolicy.Always, cookies.Cookie.SecurePolicy);
        Assert.IsInstanceOfType<IBffServerSessionStore>(cookies.SessionStore);
        var logging = factory.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(nameof(IIdentityAdministrationApiClient));
        Assert.IsTrue(logging.ShouldRedactHeaderValue("Authorization"));
        Assert.IsTrue(logging.ShouldRedactHeaderValue(BffWorkloadAuthentication.SignatureHeader));
    }

    [TestMethod]
    public async Task NamedApiClientsRequireAnActiveBffSession()
    {
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var clients = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        foreach (var name in new[]
        {
            CleanupApiClient.RuntimeClient, CleanupApiClient.FlowClient,
            CleanupApiClient.ManagementClient, CleanupApiClient.WorkClient,
            EntryAdministrationApiClient.AgentResourceCatalogClient,
            EntryAdministrationApiClient.FlowResourceCatalogClient
        })
        {
            using var client = clients.CreateClient(name);
            using var response = await client.GetAsync("api/identity/context");
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, name);
        }
    }

    [TestMethod]
    public async Task SeparatedManagementClientAttachesDelegationWithoutSharingTheBrowserTicket()
    {
        var identity = new BffSessionIdentityResponse(Guid.NewGuid(), "Console user",
            BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid(), "version");
        var capture = new DelegationCaptureHandler();
        await using var factory = CreateFactory(new StubSessionAuthorityClient(identity))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.Configure<BffWorkloadClientOptions>(options =>
                {
                    options.Enabled = true;
                    options.WorkloadId = "console-bff";
                    options.CredentialId = "test";
                    options.TargetInstanceId = "test";
                    options.SharedKeyFile = "unused-test-key";
                });
                services.AddSingleton<IBffDelegationClient>(new StubDelegationClient());
                services.Configure<HttpClientFactoryOptions>(nameof(IIdentityAdministrationApiClient), options =>
                    options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = capture));
            }));
        var principal = BffSessionClaims.Create(identity);
        var store = factory.Services.GetRequiredService<IBffServerSessionStore>();
        await store.StoreAsync(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
            principal, ConsoleAuthenticationDefaults.Scheme));
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        context.HttpContext = new DefaultHttpContext { User = principal };
        using var client = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IIdentityAdministrationApiClient));
        using var response = await client.GetAsync("api/identity/context");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("agd_management", capture.Authorization?.Parameter);
        Assert.AreEqual("Bearer", capture.Authorization?.Scheme);
        Assert.IsFalse(capture.HasCookie);
    }

    [TestMethod]
    public async Task LocalLoginCreatesOpaqueServerSessionAndLogoutInvalidatesIt()
    {
        var identity = new BffSessionIdentityResponse(
            Guid.NewGuid(), "Console user", BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid());
        var authority = new StubSessionAuthorityClient(identity);
        await using var factory = CreateFactory(authority);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var loginHtml = await client.GetStringAsync("/login");
        using var login = await client.PostAsync("/login", Form(
            Token(loginHtml),
            ("Input.UserName", "console-user"),
            ("Input.Password", "correct-password"),
            ("ReturnUrl", "https://attacker.invalid/escape")));

        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
        Assert.AreEqual("/", login.Headers.Location?.OriginalString ?? string.Empty);
        var cookiePrefix = $"{ConsoleAuthenticationDefaults.Cookie}=";
        var setCookie = login.Headers.GetValues("Set-Cookie")
            .Last(value => value.StartsWith(cookiePrefix, StringComparison.Ordinal)
                && !value.StartsWith($"{cookiePrefix};", StringComparison.Ordinal));
        Assert.IsFalse(setCookie.Contains(identity.PrincipalId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase));

        using var authenticated = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.IsTrue(authority.Validations > 0);

        var logoutHtml = await client.GetStringAsync("/logout");
        using var logout = await client.PostAsync("/logout", Form(Token(logoutHtml)));
        Assert.AreEqual(HttpStatusCode.Redirect, logout.StatusCode);
        using var afterLogout = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.Redirect, afterLogout.StatusCode);
    }

    [TestMethod]
    public async Task AuthorityRevocationCannotBeUndoneByReplayingTheBrowserCookie()
    {
        var identity = new BffSessionIdentityResponse(
            Guid.NewGuid(), "Console user", BffSessionAuthenticationMethods.Local, "local", Guid.NewGuid(), Guid.NewGuid());
        var authority = new StubSessionAuthorityClient(identity);
        await using var factory = CreateFactory(authority);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var loginHtml = await client.GetStringAsync("/login");
        using var login = await client.PostAsync("/login", Form(
            Token(loginHtml),
            ("Input.UserName", "console-user"),
            ("Input.Password", "correct-password")));
        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);

        using var authenticated = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
        authority.Active = false;
        using var revoked = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.Redirect, revoked.StatusCode);

        authority.Active = true;
        using var replayed = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.Redirect, replayed.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(IBffSessionAuthorityClient? authority = null) =>
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
            if (authority is not null)
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<IBffSessionAuthorityClient>(authority));
        });

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] values)
    {
        var fields = values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        fields["__RequestVerificationToken"] = token;
        return new(fields);
    }

    private static string Token(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.IsTrue(match.Success, "The antiforgery token was not rendered.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed class StubSessionAuthorityClient(BffSessionIdentityResponse identity) : IBffSessionAuthorityClient
    {
        public int Validations { get; private set; }
        public bool Active { get; set; } = true;

        public Task<BffLocalAuthenticationResult> AuthenticateLocalAsync(
            string userName,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BffLocalAuthenticationResult(BffLocalAuthenticationOutcome.Succeeded, identity));

        public Task<BffSessionValidationResponse> ValidateAsync(
            BffSessionValidationRequest request,
            CancellationToken cancellationToken)
        {
            Validations++;
            return Task.FromResult(new BffSessionValidationResponse(Active, Active ? identity : null));
        }
    }

    private sealed class StubDelegationClient : IBffDelegationClient
    {
        public Task<BffDelegationResponse?> IssueAsync(BffDelegationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<BffDelegationResponse?>(new("agd_management", DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    private sealed class DelegationCaptureHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public bool HasCookie { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            HasCookie = request.Headers.Contains("Cookie");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
