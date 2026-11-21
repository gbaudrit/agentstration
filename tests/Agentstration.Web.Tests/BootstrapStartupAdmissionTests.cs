using System.Security.Claims;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Web.Hosting;
using Agentstration.Web.Pages;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupAdmissionTests
{
    [TestMethod]
    [DataRow("/_blazor/negotiate")]
    [DataRow("/api/auth/protected")]
    [DataRow("/api/internal/bff/trust")]
    [DataRow("/health/private")]
    [DataRow("/css/private")]
    public async Task ProtectedEndpointsCannotSkipAdmissionByUsingAnInfrastructurePrefix(string path)
    {
        var context = AuthenticatedContext(path);
        var admission = new Admission(false);
        var invoked = false;
        await new BootstrapStartupAdmissionMiddleware(_ => { invoked = true; return Task.CompletedTask; })
            .InvokeAsync(context, PendingStartup(), admission);

        Assert.AreEqual(1, admission.Calls);
        Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    [DataRow("/_blazor/negotiate")]
    [DataRow("/api/extensionregistrations")]
    public async Task AdmittedAdministratorCanReachProtectedEndpoints(string path)
    {
        var context = AuthenticatedContext(path);
        var admission = new Admission(true);
        var invoked = false;
        await new BootstrapStartupAdmissionMiddleware(_ => { invoked = true; return Task.CompletedTask; })
            .InvokeAsync(context, PendingStartup(), admission);

        Assert.AreEqual(1, admission.Calls);
        Assert.IsTrue(invoked);
    }

    [TestMethod]
    [DataRow("/login", true)]
    [DataRow("/auth.css", true)]
    [DataRow("/health/live", true)]
    [DataRow("/logout", false)]
    [DataRow("/api/auth/logout", false)]
    [DataRow("/startup/progress", false)]
    public async Task DeniedSessionRetainsOnlyPublicEndpointsSessionExitAndTheDeniedProgressPage(string path, bool anonymousEndpoint)
    {
        var context = AuthenticatedContext(path);
        if (anonymousEndpoint)
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new AllowAnonymousAttribute()), path));
        var admission = new Admission(false);
        var invoked = false;
        await new BootstrapStartupAdmissionMiddleware(_ => { invoked = true; return Task.CompletedTask; })
            .InvokeAsync(context, PendingStartup(), admission);

        Assert.AreEqual(1, admission.Calls);
        Assert.IsTrue(invoked);
    }

    [TestMethod]
    public async Task VerifiedBffWorkloadIsNotTreatedAsAnInteractiveSession()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/internal/bff/trust";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([], AgentstrationAuthenticationDefaults.BffWorkloadScheme));
        var admission = new Admission(false);
        var invoked = false;
        await new BootstrapStartupAdmissionMiddleware(_ => { invoked = true; return Task.CompletedTask; })
            .InvokeAsync(context, PendingStartup(), admission);

        Assert.AreEqual(0, admission.Calls);
        Assert.IsTrue(invoked);
    }

    [TestMethod]
    [DataRow("//example.com", "")]
    [DataRow("/\\example.com", "")]
    [DataRow("/console", "/console")]
    [DataRow("", "")]
    public async Task WaitingPageUsesOnlyLocalUrlsIncludingWithAPathBase(string pathBase, string expectedBase)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = HttpMethods.Get;
        context.Request.PathBase = pathBase;
        context.Request.Path = "/startup";
        context.Request.QueryString = new QueryString("?ReturnUrl=https%3A%2F%2Fexample.com");
        using var body = new MemoryStream();
        context.Response.Body = body;
        var startup = new BootstrapStartupState();
        startup.Begin();
        var middleware = new BootstrapStartupPageMiddleware(_ => throw new AssertFailedException("Unexpected continuation."));
        var strings = provider.GetRequiredService<IStringLocalizer<AuthStrings>>();

        await middleware.InvokeAsync(context, startup, strings);
        body.Position = 0;
        using var reader = new StreamReader(body);
        var html = await reader.ReadToEndAsync();
        StringAssert.Contains(html, $"const readyUrl = \"{expectedBase}/startup/ready\";");
        StringAssert.Contains(html, $"const loginUrl = \"{expectedBase}/login?ReturnUrl=%2F\";");

        startup.EnableAdministration();
        await middleware.InvokeAsync(context, startup, strings);
        Assert.AreEqual(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.AreEqual($"{expectedBase}/login?ReturnUrl=%2F", context.Response.Headers.Location.ToString());
    }

    private static DefaultHttpContext AuthenticatedContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([], "cookie"));
        context.Features.Set(new ResolvedPrincipalFeature(Guid.NewGuid(), "Fixture"));
        return context;
    }

    private static BootstrapStartupState PendingStartup()
    {
        var startup = new BootstrapStartupState();
        startup.Begin();
        startup.EnableAdministration();
        return startup;
    }

    private sealed class Admission(bool allowed) : IAuthenticationAdmissionPolicy
    {
        public int Calls { get; private set; }
        public Task<bool> CanSignInAsync(Guid principalId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(allowed);
        }
    }
}
