using System.Net;
using System.Net.Http.Json;
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

        var rendered = context.Render<Home>();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.AreEqual("http://localhost/w/personal", navigation.Uri);
        Assert.AreEqual(1, rendered.FindAll(".state-panel[role='status']").Count);
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Agentstration.Workplace.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
