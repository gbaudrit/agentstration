using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class LegacyVerticalRemovalTests
{
    private static readonly string[] LegacyTypeNames =
    [
        "ApiEndpoints", "PlatformMcpTools", "ItemProcessingWorker", "MissionSchedulerWorker",
        "DemoData", "WorkspaceService", "IngestionService", "MemoryService", "MissionService",
        "ContentProcessingWorkflow", "IPlatformStore", "JsonFilePlatformStore", "InMemoryPlatformStore"
    ];

    [TestMethod]
    public async Task LegacyRoutesAndPagesAreNotMappedWhileWorkplaceRoutesRemain()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        Assert.IsTrue(routes.Contains("/api/workspaces/{workspaceName}/dashboard", StringComparer.Ordinal));
        Assert.IsTrue(routes.Contains("/api/workspaces/{workspaceName}/entries/{entryName}/interactions", StringComparer.Ordinal));
        Assert.IsTrue(routes.Contains("/api/workspaces/{workspaceName}/interactions/{interactionId:guid}", StringComparer.Ordinal));

        var forbiddenRoutes = new[]
        {
            "/api/workspaces", "/api/workspaces/{workspaceId:guid}/inboxes",
            "/api/workspaces/{workspaceId:guid}/inboxes/{inboxId:guid}/items",
            "/api/workspaces/{workspaceId:guid}/items/{itemId:guid}",
            "/api/workspaces/{workspaceId:guid}/memory/search",
            "/api/workspaces/{workspaceId:guid}/missions",
            "/api/workspaces/{workspaceId:guid}/missions/{missionId:guid}",
            "/api/workspaces/{workspaceId:guid}/missions/{missionId:guid}/run",
            "/api/workspaces/{workspaceId:guid}/missions/{missionId:guid}/runs",
            "/ingest", "/missions"
        };
        Assert.IsFalse(routes.Any(route => forbiddenRoutes.Contains(route, StringComparer.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task CompositionContainsNoLegacyServicesOrWorkers()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var webTypes = typeof(global::Program).Assembly.GetTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var infrastructureTypes = typeof(Agentstration.Infrastructure.DependencyInjection).Assembly.GetTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var applicationTypes = typeof(Agentstration.Application.Work.WorkplaceService).Assembly.GetTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        Assert.IsFalse(LegacyTypeNames.Any(name => webTypes.Contains(name) || infrastructureTypes.Contains(name) || applicationTypes.Contains(name)));

        var hostedServices = factory.Services.GetServices<IHostedService>().Select(service => service.GetType().Name).ToArray();
        Assert.IsFalse(hostedServices.Contains("ItemProcessingWorker", StringComparer.Ordinal));
        Assert.IsFalse(hostedServices.Contains("MissionSchedulerWorker", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task StartupDoesNotCreateLegacyDataJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-no-legacy-json-{Guid.NewGuid():N}");
        try
        {
            await using (var factory = Factory(directory))
            {
                using var client = factory.CreateClient();
                Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
                Assert.IsFalse(File.Exists(Path.Combine(directory, "data.json")));
                Assert.IsFalse(Directory.EnumerateFiles(directory, "data.json", SearchOption.AllDirectories).Any());
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static WebApplicationFactory<global::Program> Factory(string? dataDirectory = null) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            if (dataDirectory is not null) builder.UseSetting("Data:TestingDirectory", dataDirectory);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
}
