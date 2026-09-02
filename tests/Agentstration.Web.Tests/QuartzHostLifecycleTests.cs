using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class QuartzHostLifecycleTests
{
    [TestMethod]
    public async Task DefaultTestingDataDirectoryIsRemovedAfterHostShutdown()
    {
        var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });

        using (var client = factory.CreateClient())
        {
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }

        var hostedServiceNames = factory.Services.GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .ToArray();
        Assert.IsFalse(hostedServiceNames.Contains("AgentDeploymentReconciliationWorker", StringComparer.Ordinal));
        Assert.IsFalse(hostedServiceNames.Contains("FlowRunExecutionWorker", StringComparer.Ordinal));
        Assert.IsFalse(hostedServiceNames.Contains("QuartzHostedService", StringComparer.Ordinal));
        Assert.IsFalse(hostedServiceNames.Contains("TelemetryHostedService", StringComparer.Ordinal));

        var directory = factory.Services.GetRequiredService<IConfiguration>()["Data:Directory"];
        Assert.IsNotNull(directory);
        Assert.IsTrue(Directory.Exists(directory));

        await factory.DisposeAsync();

        for (var attempt = 0; attempt < 50 && Directory.Exists(directory); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task SchedulerDatabaseIsReleasedAfterEveryHostShutdown()
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-quartz-lifecycle-{Guid.NewGuid():N}");
            try
            {
                await using (var factory = Factory(directory))
                {
                    using var client = factory.CreateClient();
                    Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
                    Assert.IsTrue(File.Exists(Path.Combine(directory, "scheduler.db")));
                }

                Directory.Delete(directory, recursive: true);
                Assert.IsFalse(Directory.Exists(directory));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TestingHostCanExplicitlyEnableOpenTelemetry()
    {
        using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Testing:OpenTelemetryEnabled", "true");
        });

        var hostedServiceNames = factory.Services.GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .ToArray();

        Assert.IsTrue(hostedServiceNames.Contains("TelemetryHostedService", StringComparer.Ordinal));
    }

    private static WebApplicationFactory<global::Program> Factory(string dataDirectory) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Data:Directory", dataDirectory);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.UseSetting("Agentstration:Testing:HostedServicesEnabled", "true");
        });
}
