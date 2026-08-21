using System.Net;
using System.Security.Claims;
using Agentstration.Management.Abstractions;
using Agentstration.Web.Hosting;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ConsoleRealtimeSessionTests
{
    private static readonly Uri HubEndpoint = new("https://console.example.test/hubs/flow-runs");

    [TestMethod]
    public void AuthenticatedSessionAndWorkspaceAreForwardedToSameOriginHub()
    {
        var workspaceId = Guid.NewGuid();
        var httpContext = AuthenticatedContext(
            "https",
            "console.example.test",
            $"{AgentstrationAuthenticationDefaults.ApplicationCookie}=chunks-2; "
            + $"{AgentstrationAuthenticationDefaults.ApplicationCookie}C1=part-one; "
            + $"{AgentstrationAuthenticationDefaults.ApplicationCookie}C2=part-two; unrelated=secret");
        var options = new HttpConnectionOptions();
        var session = new ConsoleRealtimeSession(
            new HttpContextAccessor { HttpContext = httpContext },
            new TestRequestContext(workspaceId));

        session.Configure(HubEndpoint, options);

        var cookies = options.Cookies.GetCookies(HubEndpoint).Cast<Cookie>().ToDictionary(cookie => cookie.Name, cookie => cookie.Value, StringComparer.Ordinal);
        Assert.AreEqual("chunks-2", cookies[AgentstrationAuthenticationDefaults.ApplicationCookie]);
        Assert.AreEqual("part-one", cookies[$"{AgentstrationAuthenticationDefaults.ApplicationCookie}C1"]);
        Assert.AreEqual("part-two", cookies[$"{AgentstrationAuthenticationDefaults.ApplicationCookie}C2"]);
        Assert.IsFalse(cookies.ContainsKey("unrelated"));
        Assert.AreEqual(workspaceId.ToString("D"), options.Headers[PrincipalResolutionMiddleware.WorkspaceHeader]);
    }

    [TestMethod]
    public void SessionIsNotForwardedToAnotherOriginOrForAnonymousRequest()
    {
        var authenticated = AuthenticatedContext(
            "https",
            "console.example.test",
            $"{AgentstrationAuthenticationDefaults.ApplicationCookie}=secret");
        var foreignOptions = new HttpConnectionOptions();
        new ConsoleRealtimeSession(
                new HttpContextAccessor { HttpContext = authenticated },
                new TestRequestContext(Guid.NewGuid()))
            .Configure(new Uri("https://untrusted.example/hubs/flow-runs"), foreignOptions);

        var anonymousOptions = new HttpConnectionOptions();
        var anonymous = new DefaultHttpContext();
        anonymous.Request.Scheme = "https";
        anonymous.Request.Host = new HostString("console.example.test");
        anonymous.Request.Headers.Cookie = $"{AgentstrationAuthenticationDefaults.ApplicationCookie}=stale";
        new ConsoleRealtimeSession(
                new HttpContextAccessor { HttpContext = anonymous },
                new TestRequestContext(Guid.NewGuid()))
            .Configure(HubEndpoint, anonymousOptions);

        Assert.IsEmpty(foreignOptions.Cookies.GetCookies(new Uri("https://untrusted.example/")));
        Assert.IsFalse(foreignOptions.Headers.ContainsKey(PrincipalResolutionMiddleware.WorkspaceHeader));
        Assert.IsEmpty(anonymousOptions.Cookies.GetCookies(HubEndpoint));
        Assert.IsFalse(anonymousOptions.Headers.ContainsKey(PrincipalResolutionMiddleware.WorkspaceHeader));
    }

    private static DefaultHttpContext AuthenticatedContext(string scheme, string host, string cookies)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "principal")], "test"))
        };
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.Headers.Cookie = cookies;
        return context;
    }

    private sealed class TestRequestContext(Guid workspaceId) : ICurrentRequestContext
    {
        public bool IsInitialized => true;
        public RequestContext Current { get; } = new(Guid.NewGuid(), Guid.NewGuid(), workspaceId);
    }
}
