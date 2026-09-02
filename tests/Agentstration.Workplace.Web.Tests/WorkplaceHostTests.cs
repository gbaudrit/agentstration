using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Work;
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
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
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
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
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
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
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
    public async Task ConversationRouteIsHandledAsADedicatedWorkplacePage()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:ApiBaseUrl", "http://127.0.0.1:1/");
            builder.UseSetting("Agentstration:WorkplaceHubUrl", "http://127.0.0.1:1/hubs/workplace");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/w/personal/d/home/conversations/11111111-1111-1111-1111-111111111111");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(html.Contains("Page not found", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ActivityListsConversationsWithLinksToTheirDedicatedPage()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        using var httpClient = new HttpClient(new ActivityHandler()) { BaseAddress = new Uri("http://localhost/") };
        context.Services.AddSingleton<IWorkplaceApiClient>(new WorkplaceApiClient(httpClient));
        context.Services.AddSingleton<WorkplaceContextState>();

        var rendered = context.Render<Tasks>(parameters => parameters.Add(value => value.WorkspaceName, "personal"));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, rendered.FindAll("[role='tab']").Count);
            var conversation = rendered.Find(".conversation-list-card");
            Assert.AreEqual("/w/personal/d/home/conversations/11111111-1111-1111-1111-111111111111", conversation.GetAttribute("href"));
            StringAssert.Contains(conversation.TextContent, "Prepare the quarterly review");
            StringAssert.Contains(conversation.TextContent, "Reprendre");
        });

        rendered.Find("#tasks-tab").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("true", rendered.Find("#tasks-tab").GetAttribute("aria-selected"));
            Assert.AreEqual(1, rendered.FindAll("#tasks-panel").Count);
            Assert.AreEqual(0, rendered.FindAll("#conversations-panel").Count);
        });
    }

    [TestMethod]
    public void HomeLinksCompactAlternativeEntriesToADedicatedStartPage()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        using var httpClient = new HttpClient(new HomeEntriesHandler()) { BaseAddress = new Uri("http://localhost/") };
        context.Services.AddSingleton<IWorkplaceApiClient>(new WorkplaceApiClient(httpClient));
        context.Services.AddSingleton(new WorkplaceRealtimeClient(new Uri("http://127.0.0.1:1/hubs/workplace"), null));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        context.Services.AddSingleton<WorkplaceContextState>();

        var rendered = context.Render<Home>(parameters => parameters
            .Add(value => value.WorkspaceName, "personal")
            .Add(value => value.DashboardName, "home"));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Start here", rendered.Find(".primary-entry-container textarea").GetAttribute("placeholder"));
            Assert.AreEqual(1, rendered.FindAll(".mobile-entry-options a").Count);
            Assert.AreEqual(0, rendered.FindAll(".mobile-tools-toggle").Count);
            var alternative = rendered.Find(".mobile-entry-options a");
            StringAssert.Contains(alternative.TextContent, "Quick question");
            Assert.AreEqual("/w/personal/d/home/start/default/quick", alternative.GetAttribute("href"));
        }, TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public void DedicatedStartPageLoadsTheSelectedEntryWithoutTheDashboardCatalog()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        using var httpClient = new HttpClient(new HomeEntriesHandler()) { BaseAddress = new Uri("http://localhost/") };
        context.Services.AddSingleton<IWorkplaceApiClient>(new WorkplaceApiClient(httpClient));
        context.Services.AddSingleton<WorkplaceContextState>();

        var rendered = context.Render<EntryStart>(parameters => parameters
            .Add(value => value.WorkspaceName, "personal")
            .Add(value => value.DashboardName, "home")
            .Add(value => value.EntryNamespace, "default")
            .Add(value => value.EntryName, "quick"));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find(".entry-start-header").TextContent, "Quick question");
            Assert.AreEqual("Ask quickly", rendered.Find(".entry-start-form textarea").GetAttribute("placeholder"));
            Assert.AreEqual("/w/personal/d/home", rendered.Find(".entry-start-back").GetAttribute("href"));
            Assert.AreEqual(0, rendered.FindAll(".mobile-entry-options").Count);
        });
    }

    [TestMethod]
    public async Task ComponentStyleBundlesAreServedByTheWorkplaceHost()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var sharedStyles = await client.GetStringAsync("/_content/Agentstration.Web.Components/Agentstration.Web.Components.bundle.scp.css");
        var workplaceStyles = await client.GetStringAsync("/_content/Agentstration.Workplace.Components/Agentstration.Workplace.Components.bundle.scp.css");
        var hostStyles = await client.GetStringAsync("/Agentstration.Workplace.Web.styles.css");
        var appStyles = await client.GetStringAsync("/app.css");
        var darkLogo = await client.GetByteArrayAsync("/_content/Agentstration.Web.Components/images/agentstration-workplace-lockup-dark.png");

        StringAssert.Contains(sharedStyles, ".ui-icon");
        StringAssert.Contains(workplaceStyles, ".mobile-appbar");
        StringAssert.Contains(workplaceStyles, ".service-unavailable");
        StringAssert.Contains(workplaceStyles, ".navigation-group h2");
        StringAssert.Contains(workplaceStyles, "grid-template-columns:repeat(3,minmax(0,1fr))");
        StringAssert.Contains(workplaceStyles, ".mobile-profile");
        StringAssert.Contains(workplaceStyles, ".composer-core");
        StringAssert.Contains(workplaceStyles, "composer-orbit");
        StringAssert.Contains(workplaceStyles, "object-fit:contain");
        StringAssert.Contains(workplaceStyles, "flex-direction:column");
        StringAssert.Contains(workplaceStyles, "bottom:calc(88px + env(safe-area-inset-bottom))");
        StringAssert.Contains(workplaceStyles, "align-content:start");
        StringAssert.Contains(workplaceStyles, ".side-nav[b-");
        StringAssert.Contains(workplaceStyles, ".mobile-brand-logo");
        StringAssert.Matches(workplaceStyles, new Regex(@"\.entry-renderer\[b-[^\]]+\]\s+form", RegexOptions.CultureInvariant));
        StringAssert.Contains(hostStyles, ".mobile-dashboard-cards");
        StringAssert.Contains(hostStyles, "grid-auto-flow:column");
        StringAssert.Contains(hostStyles, "grid-auto-columns:4.85rem");
        StringAssert.Contains(hostStyles, ".mobile-entry-options");
        StringAssert.Contains(hostStyles, ".entry-start-page");
        StringAssert.Contains(hostStyles, "align-content:start");
        Assert.IsFalse(appStyles.Contains(".workplace-shell>.sidebar{display:none}", StringComparison.Ordinal));
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

    private sealed class ActivityHandler : HttpMessageHandler
    {
        private static readonly Guid WorkspaceId = Guid.Parse("525118a7-00e9-49ae-aa6c-d42c382f1a8b");
        private static readonly Guid ConversationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = new DateTimeOffset(2026, 9, 2, 9, 30, 0, TimeSpan.Zero);
            var message = new ConversationMessage(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                new WorkspaceId(WorkspaceId),
                new InteractionId(ConversationId),
                null,
                ConversationRole.User,
                "Prepare the quarterly review",
                now);
            object payload = request.RequestUri?.PathAndQuery switch
            {
                "/api/workspaces/personal" => new WorkplaceWorkspaceResponse(WorkspaceId, "personal", "Personal"),
                "/api/workspaces/personal/dashboard" => new WorkplaceDashboardResponse("home", WorkspaceId, "home", "Dashboard", "v1", "Home", null, true, [], 1, now),
                "/api/workspaces/personal/interactions?take=50" => new InteractionPageResponse([
                    new InteractionResponse(ConversationId, WorkspaceId, "discover", InteractionStatus.Idle, now, now, new Dictionary<string, JsonElement>(), [], [message], null, null, null, 1)
                ]),
                "/api/workspaces/personal/tasks" => new WorkTaskPageResponse([]),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        }
    }

    private sealed class HomeEntriesHandler : HttpMessageHandler
    {
        private static readonly Guid WorkspaceId = Guid.Parse("525118a7-00e9-49ae-aa6c-d42c382f1a8b");
        private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 30, 0, TimeSpan.Zero);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload = request.RequestUri?.AbsolutePath switch
            {
                "/api/workspaces/personal" => new WorkplaceWorkspaceResponse(WorkspaceId, "personal", "Personal"),
                "/api/workspaces/personal/dashboards" => new[]
                {
                    new WorkplaceDashboardResponse("home", WorkspaceId, "home", "Dashboard", "v1", "Home", null, true,
                    [
                        new DashboardEntryReferenceResponse("main", DashboardItemRole.Primary, 0),
                        new DashboardEntryReferenceResponse("quick", DashboardItemRole.Standard, 10)
                    ], 1, Now)
                },
                "/api/entries/main" => Entry("main", "Main request", "Start here"),
                "/api/entries/quick" => Entry("quick", "Quick question", "Ask quickly"),
                "/api/workspaces/personal/tasks" => new WorkTaskPageResponse([]),
                "/api/workspaces/personal/notifications/unread-count" => new UnreadNotificationCountResponse(0),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        }

        private static EntryResponse Entry(string id, string displayName, string placeholder) => new(
            WorkspaceId,
            id,
            id,
            WorkResourceTypes.Entries,
            WorkplaceApiVersions.CoreV1,
            displayName,
            $"Description for {displayName}",
            new EntryPresentation { Placeholder = placeholder, Icon = "sparkle" },
            new EntryResolvedTarget($"{id}-flow", "v1"),
            new EntryBehavior(),
            1,
            Now);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Agentstration.Workplace.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
