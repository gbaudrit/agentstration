using System.Security.Claims;
using Agentstration.Management.Abstractions;
using Agentstration.Web.Hosting;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ConsoleApiSessionHandlerTests
{
    private const string SessionCookie = ".Agentstration.Session";
    private static readonly Uri TrustedOrigin = new("http://agentstration-console:5100/");

    [TestMethod]
    public async Task AuthenticatedSessionAndWorkspaceAreForwardedToTrustedApi()
    {
        var workspaceId = Guid.NewGuid();
        var httpContext = AuthenticatedContext($"{SessionCookie}=chunks-2; {SessionCookie}C1=part-one; {SessionCookie}C2=part-two; unrelated=secret");
        var terminal = new CaptureHandler();
        using var handler = new ConsoleApiSessionHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new TestRequestContext(workspaceId),
            TrustedOrigin,
            SessionCookie)
        { InnerHandler = terminal };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(new Uri(TrustedOrigin, "/api/agents"));

        Assert.AreEqual("chunks-2", Cookie(terminal.Request!, SessionCookie));
        Assert.AreEqual("part-one", Cookie(terminal.Request!, $"{SessionCookie}C1"));
        Assert.AreEqual("part-two", Cookie(terminal.Request!, $"{SessionCookie}C2"));
        Assert.IsNull(Cookie(terminal.Request!, "unrelated"));
        Assert.AreEqual(workspaceId.ToString("D"), terminal.Request!.Headers.GetValues(PrincipalResolutionMiddleware.WorkspaceHeader).Single());
    }

    [TestMethod]
    public async Task SessionIsNeverForwardedOutsideTheConfiguredOrigin()
    {
        var httpContext = AuthenticatedContext($"{SessionCookie}=secret");
        var terminal = new CaptureHandler();
        using var handler = new ConsoleApiSessionHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new TestRequestContext(Guid.NewGuid()),
            TrustedOrigin,
            SessionCookie)
        { InnerHandler = terminal };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("https://untrusted.example/api/agents");

        Assert.IsFalse(terminal.Request!.Headers.Contains(HeaderNames.Cookie));
        Assert.IsFalse(terminal.Request.Headers.Contains(PrincipalResolutionMiddleware.WorkspaceHeader));
    }

    [TestMethod]
    public async Task UnauthenticatedOrExplicitlyAuthenticatedRequestsAreNotModified()
    {
        var anonymous = new DefaultHttpContext();
        anonymous.Request.Headers.Cookie = $"{SessionCookie}=stale";
        var terminal = new CaptureHandler();
        using var handler = new ConsoleApiSessionHandler(
            new HttpContextAccessor { HttpContext = anonymous },
            new TestRequestContext(Guid.NewGuid()),
            TrustedOrigin,
            SessionCookie)
        { InnerHandler = terminal };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(TrustedOrigin, "/api/agents"));
        request.Headers.TryAddWithoutValidation(HeaderNames.Cookie, "explicit=value");

        using var response = await client.SendAsync(request);

        Assert.AreEqual("value", Cookie(terminal.Request!, "explicit"));
        Assert.IsNull(Cookie(terminal.Request!, SessionCookie));
        Assert.IsFalse(terminal.Request!.Headers.Contains(PrincipalResolutionMiddleware.WorkspaceHeader));
    }

    private static DefaultHttpContext AuthenticatedContext(string cookies)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "principal")], "test"))
        };
        context.Request.Headers.Cookie = cookies;
        return context;
    }

    private static string? Cookie(HttpRequestMessage request, string name)
    {
        if (!request.Headers.TryGetValues(HeaderNames.Cookie, out var headers)) return null;
        return headers.SelectMany(header => header.Split(';', StringSplitOptions.TrimEntries))
            .Select(value => value.Split('=', 2))
            .Where(parts => parts.Length == 2 && string.Equals(parts[0], name, StringComparison.Ordinal))
            .Select(parts => parts[1])
            .SingleOrDefault();
    }

    private sealed class TestRequestContext(Guid workspaceId) : ICurrentRequestContext
    {
        public bool IsInitialized => true;
        public RequestContext Current { get; } = new(Guid.NewGuid(), Guid.NewGuid(), workspaceId);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
