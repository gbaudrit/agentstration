using System.Net;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Workplace.Client;
using Agentstration.Workplace.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Agentstration.Workplace.Web.Tests;

[TestClass]
public sealed class WorkplaceApiClientTests
{
    [TestMethod]
    public async Task NamespacedEntryUsesNamespacedReadAndSubmissionRoutes()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100/") };
        var client = new WorkplaceApiClient(http);
        var entryId = new EntryId("main", new ResourceNamespace("agentstration.daily-life-assistant"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetEntryAsync(entryId, default));
        Assert.AreEqual("/api/namespaces/agentstration.daily-life-assistant/entries/main", handler.RequestUri?.AbsolutePath);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SubmitAsync("personal", entryId, new Dictionary<string, System.Text.Json.JsonElement>(), default));
        Assert.AreEqual("/api/workspaces/personal/namespaces/agentstration.daily-life-assistant/entries/main/interactions", handler.RequestUri?.AbsolutePath);
    }

    [TestMethod]
    public async Task WorkplaceForwardsOnlyAgentstrationSessionCookiesToTheConfiguredApiOrigin()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = ".Agentstration.Identity.Application=chunks-2; .Agentstration.Identity.ApplicationC1=part; agentstration.workspace=workspace-id; unrelated=secret";
        var capture = new CaptureHandler(HttpStatusCode.OK);
        var handler = new WorkplaceApiSessionHandler(
            new HttpContextAccessor { HttpContext = context },
            new Uri("http://localhost:5100/"),
            ".Agentstration.Identity.Application",
            "agentstration.workspace")
        {
            InnerHandler = capture
        };
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:5100/api/workplace/workspaces"), default);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(
            ".Agentstration.Identity.Application=chunks-2; .Agentstration.Identity.ApplicationC1=part; agentstration.workspace=workspace-id",
            capture.Cookie);
        Assert.IsFalse(capture.Cookie?.Contains("unrelated", StringComparison.Ordinal) == true);
    }

    [TestMethod]
    public async Task WorkplaceDoesNotForwardSessionCookiesToAnotherOrigin()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = ".Agentstration.Identity.Application=session";
        var capture = new CaptureHandler(HttpStatusCode.OK);
        var handler = new WorkplaceApiSessionHandler(
            new HttpContextAccessor { HttpContext = context },
            new Uri("http://localhost:5100/"),
            ".Agentstration.Identity.Application",
            "agentstration.workspace")
        {
            InnerHandler = capture
        };
        using var invoker = new HttpMessageInvoker(handler);

        _ = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:5200/api/workplace/workspaces"), default);

        Assert.IsNull(capture.Cookie);
    }

    [TestMethod]
    public void WorkplaceRealtimeForwardsOnlySessionAndWorkspaceCookiesToTheConfiguredHubOrigin()
    {
        var endpoint = new Uri("http://localhost:5100/hubs/workplace");
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = ".Agentstration.Identity.Application=chunks-2; .Agentstration.Identity.ApplicationC1=part; agentstration.workspace=workspace-id; unrelated=secret";
        var session = new WorkplaceRealtimeSession(
            new HttpContextAccessor { HttpContext = context },
            endpoint,
            ".Agentstration.Identity.Application",
            "agentstration.workspace");
        var options = new HttpConnectionOptions();

        session.Configure(endpoint, options);

        var cookies = options.Cookies.GetCookies(endpoint).Cast<Cookie>()
            .ToDictionary(cookie => cookie.Name, cookie => cookie.Value, StringComparer.Ordinal);
        Assert.AreEqual("chunks-2", cookies[".Agentstration.Identity.Application"]);
        Assert.AreEqual("part", cookies[".Agentstration.Identity.ApplicationC1"]);
        Assert.AreEqual("workspace-id", cookies["agentstration.workspace"]);
        Assert.IsFalse(cookies.ContainsKey("unrelated"));
    }

    [TestMethod]
    public void WorkplaceRealtimeDoesNotForwardSessionCookiesToAnotherOrigin()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = ".Agentstration.Identity.Application=session; agentstration.workspace=workspace-id";
        var session = new WorkplaceRealtimeSession(
            new HttpContextAccessor { HttpContext = context },
            new Uri("http://localhost:5100/hubs/workplace"),
            ".Agentstration.Identity.Application",
            "agentstration.workspace");
        var options = new HttpConnectionOptions();

        session.Configure(new Uri("http://localhost:5200/hubs/workplace"), options);

        Assert.IsEmpty(options.Cookies.GetCookies(new Uri("http://localhost:5200/")));
    }

    private sealed class CaptureHandler(HttpStatusCode statusCode = HttpStatusCode.NotFound) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Cookie { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
            return Task.FromResult(new HttpResponseMessage(statusCode) { RequestMessage = request });
        }
    }
}
