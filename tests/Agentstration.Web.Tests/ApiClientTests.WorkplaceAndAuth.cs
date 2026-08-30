using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.Security;
using Agentstration.Work;
using Agentstration.Work.Contracts;
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

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public void OidcApisPreferBearerButAcceptTheTrustedConsoleSession()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Authentication:Mode"] = Agentstration.Web.Configuration.AuthenticationOptions.Oidc,
            ["Agentstration:Authentication:Authority"] = "https://identity.example/",
            ["Agentstration:Authentication:Audience"] = "agentstration-api",
            ["Agentstration:Authentication:ClientId"] = "agentstration-console"
        }).Build();
        services.AddLogging();
        services.AddAgentstrationWebConsole(configuration, new TestHostEnvironment());
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
            ["Agentstration:Authentication:Mode"] = Agentstration.Web.Configuration.AuthenticationOptions.Local
        }).Build();
        services.AddLogging();
        services.AddAgentstrationWebConsole(configuration, new TestHostEnvironment());
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

    [TestMethod]
    public void ConsoleUsesCanonicalHttpClients()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:ManagementApi:BaseAddress"] = "http://localhost:5080/",
            ["Agentstration:RuntimeApi:BaseAddress"] = "http://localhost:5080/"
        }).Build();
        services.AddLogging();
        services.AddAgentstrationWebConsole(configuration, new TestHostEnvironment());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsInstanceOfType<ManagementApiClient>(scope.ServiceProvider.GetRequiredService<IAgentRunnerManagementClient>());
        Assert.IsInstanceOfType<RuntimeApiClient>(scope.ServiceProvider.GetRequiredService<IAgentRunnerRuntimeClient>());
        Assert.IsInstanceOfType<ManagementApiClient>(scope.ServiceProvider.GetRequiredService<IManagementApiClient>());
        Assert.IsInstanceOfType<WorkApiClient>(scope.ServiceProvider.GetRequiredService<IWorkApiClient>());
        Assert.IsInstanceOfType<EntryAdministrationApiClient>(scope.ServiceProvider.GetRequiredService<IEntryAdministrationApiClient>());
        Assert.IsInstanceOfType<PacksApiClient>(scope.ServiceProvider.GetRequiredService<IPacksClient>());
        Assert.IsInstanceOfType<HttpAgentstrationEventStream>(scope.ServiceProvider.GetRequiredService<IAgentstrationEventStream>());
        Assert.IsInstanceOfType<ConsoleResourceSearchProvider>(scope.ServiceProvider.GetRequiredService<IResourceSearchProvider>());
    }

    [TestMethod]
    public async Task WorkClientMapsPublicContractToConsoleModel()
    {
        var timestamp = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var response = new WorkTaskOperationsPageResponse(
            [new WorkTaskOperationsSummary(Guid.NewGuid(), "personal", "review", Guid.NewGuid(), "Review API", null, WorkTaskStatus.Running, timestamp, timestamp, timestamp, null, "flowrun-1", null, 0, 0, 0, 1, "Work started", null)],
            1, 100, 1);
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new WorkApiClient(httpClient);

        var items = await client.GetWorkItemsAsync(CancellationToken.None);

        Assert.HasCount(1, items);
        Assert.AreEqual("Review API", items[0].Title);
        Assert.AreEqual("Running", items[0].Status);
        Assert.AreEqual("personal", items[0].Owner);
    }

    [TestMethod]
    public async Task WorkClientRespondsToTaskScopedPendingActionWithoutInteractionToken()
    {
        var taskId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        var contract = new PendingActionContract(actionId, workspaceId, null, taskId, "run-1", PendingActionKind.ConfirmationRequired, PendingActionStatus.Completed, "Approve", null, [], DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, 2);
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            method = request.Method;
            path = request.RequestUri!.AbsolutePath;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(contract) };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var actual = await new WorkApiClient(httpClient).RespondTaskPendingActionAsync(taskId, actionId, new Dictionary<string, JsonElement> { ["confirmed"] = JsonSerializer.SerializeToElement(true) }, default);

        Assert.AreEqual(HttpMethod.Post, method);
        Assert.AreEqual($"/api/tasks/{taskId}/pending-actions/{actionId}/respond", path);
        StringAssert.Contains(body, "confirmed");
        Assert.AreEqual(actionId, actual.Id);
        Assert.IsNull(actual.InteractionId);
    }

    [TestMethod]
    public async Task WorkClientExposesSafeErrorIdentifier()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var client = new WorkApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<AgentstrationApiException>(() => client.GetWorkItemsAsync(CancellationToken.None));

        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.ErrorId));
    }

    [TestMethod]
    public async Task WorkClientListsWorkplaceWorkspacesThroughUnambiguousApiRoute()
    {
        string? requestPath = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<WorkplaceWorkspaceResponse>()) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new WorkApiClient(httpClient);

        _ = await client.GetWorkspacesAsync(CancellationToken.None);

        Assert.AreEqual("/api/workplace/workspaces", requestPath);
    }

}

