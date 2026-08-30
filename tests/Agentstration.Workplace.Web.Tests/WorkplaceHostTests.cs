using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Agentstration.Web.Components.State;
using Agentstration.Work.Contracts;
using Agentstration.Workplace.Client;
using Agentstration.Workplace.Web.Components.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Workplace.Web.Tests;

[TestClass]
public sealed class WorkplaceHostTests
{
    [TestMethod]
    public void FirstVisitKeepsLoadingWhileRedirectingToTheCanonicalWorkspace()
    {
        using var context = new BunitContext();
        using var httpClient = new HttpClient(new WorkspaceListHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };
        context.Services.AddSingleton<IWorkplaceApiClient>(new WorkplaceApiClient(httpClient));
        context.Services.AddSingleton(new WorkplaceRealtimeClient(new Uri("http://localhost/hubs/workplace"), null));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        context.Services.AddSingleton<WorkplaceContextState>();

        var rendered = context.Render<Home>();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.AreEqual("http://localhost/w/personal", navigation.Uri);
        Assert.AreEqual(1, rendered.FindAll(".state-panel[role='status']").Count);
    }

    [TestMethod]
    public void WorkspaceArrivalSelectsTheConfiguredDefaultDashboard()
    {
        using var context = new BunitContext();
        using var httpClient = new HttpClient(new DefaultDashboardHandler()) { BaseAddress = new Uri("http://localhost/") };
        context.Services.AddSingleton<IWorkplaceApiClient>(new WorkplaceApiClient(httpClient));
        context.Services.AddSingleton(new WorkplaceRealtimeClient(new Uri("http://localhost/hubs/workplace"), null));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        context.Services.AddSingleton<WorkplaceContextState>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("w/personal");

        context.Render<Home>(parameters => parameters.Add(value => value.WorkspaceName, "personal"));

        Assert.AreEqual("http://localhost/w/personal/d/travel", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    public void UnavailableStateRetryUsesANativePageReloadWithoutBlazorInteractivity()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("w/personal");

        var rendered = context.Render<Agentstration.Workplace.Components.ServiceUnavailableState>();

        var retry = rendered.Find("form");
        Assert.AreEqual("get", retry.GetAttribute("method"));
        Assert.AreEqual("http://localhost/w/personal", retry.GetAttribute("action"));
        Assert.AreEqual("false", retry.GetAttribute("data-enhance"));
        Assert.AreEqual("submit", retry.QuerySelector("button")?.GetAttribute("type"));
    }

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

    [TestMethod]
    public async Task FirstVisitExplainsThatAgentstrationConsoleMustBeStarted()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:ApiBaseUrl", "http://127.0.0.1:1/");
            builder.UseSetting("Agentstration:WorkplaceHubUrl", "http://127.0.0.1:1/hubs/workplace");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "Start the Console and complete first-time setup if prompted, then try again.");
        Assert.IsFalse(html.Contains("local Work API", StringComparison.Ordinal));
        StringAssert.Contains(html, "_content/Agentstration.Web.Components/Agentstration.Web.Components.bundle.scp.css");
        StringAssert.Contains(html, "_content/Agentstration.Workplace.Components/Agentstration.Workplace.Components.bundle.scp.css");
    }

    [TestMethod]
    public async Task ComponentStyleBundlesAreServedByTheWorkplaceHost()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var sharedStyles = await client.GetStringAsync("/_content/Agentstration.Web.Components/Agentstration.Web.Components.bundle.scp.css");
        var workplaceStyles = await client.GetStringAsync("/_content/Agentstration.Workplace.Components/Agentstration.Workplace.Components.bundle.scp.css");
        var hostStyles = await client.GetStringAsync("/Agentstration.Workplace.Web.styles.css");
        var darkLogo = await client.GetByteArrayAsync("/_content/Agentstration.Web.Components/images/agentstration-workplace-lockup-dark.png");

        StringAssert.Contains(sharedStyles, ".ui-icon");
        StringAssert.Contains(workplaceStyles, ".mobile-appbar");
        StringAssert.Contains(workplaceStyles, ".service-unavailable");
        StringAssert.Contains(workplaceStyles, ".navigation-group h2");
        StringAssert.Contains(workplaceStyles, "grid-template-columns:repeat(3,minmax(0,1fr))");
        StringAssert.Contains(workplaceStyles, ".mobile-profile");
        StringAssert.Contains(workplaceStyles, ".composer-symbol");
        StringAssert.Contains(workplaceStyles, "flex-direction:column");
        StringAssert.Contains(workplaceStyles, ".side-nav[b-");
        StringAssert.Contains(workplaceStyles, ".mobile-brand-logo-dark");
        StringAssert.Matches(workplaceStyles, new Regex(@"\.entry-renderer\[b-[^\]]+\]\s+form", RegexOptions.CultureInvariant));
        StringAssert.Contains(hostStyles, ".mobile-dashboard-cards");
        StringAssert.Contains(hostStyles, "grid-auto-flow:column");
        StringAssert.Contains(hostStyles, "grid-auto-columns:4.85rem");
        Assert.IsTrue(darkLogo.Length > 10_000);
    }

    [TestMethod]
    public async Task WorkplaceComponentStylesAreNotEmptyWhenGzipIsRequested()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/_content/Agentstration.Workplace.Components/Agentstration.Workplace.Components.bundle.scp.css");
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(body.Length > 1_000, $"Expected a non-empty Workplace CSS bundle, but received {body.Length} bytes.");
    }

    private sealed class WorkspaceListHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("http://localhost/api/workplace/workspaces", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new WorkplaceWorkspaceResponse(Guid.NewGuid(), "personal", "Personal") })
            });
        }
    }

    private sealed class DefaultDashboardHandler : HttpMessageHandler
    {
        private static readonly Guid WorkspaceId = Guid.Parse("525118a7-00e9-49ae-aa6c-d42c382f1a8b");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload = request.RequestUri?.AbsolutePath switch
            {
                "/api/workspaces/personal" => new WorkplaceWorkspaceResponse(WorkspaceId, "personal", "Personal", UserDisplayName: "Alex"),
                "/api/workspaces/personal/dashboards" => new[]
                {
                    Dashboard("home", "Home", isDefault: false),
                    Dashboard("travel", "Travel", isDefault: true)
                },
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        }

        private static WorkplaceDashboardResponse Dashboard(string name, string displayName, bool isDefault) =>
            new(name, WorkspaceId, name, "Dashboard", "v1", displayName, null, isDefault, [], 1, DateTimeOffset.UnixEpoch);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Agentstration.Workplace.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
