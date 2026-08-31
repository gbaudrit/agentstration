using System.Net;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class QuartzHostLifecycleTests
{
    [TestMethod]
    public async Task UnconfiguredTestingHostDeletesItsOwnedDataDirectoryAfterShutdown()
    {
        string directory;
        await using (var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        }))
        {
            using var client = factory.CreateClient();
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            directory = factory.Services.GetRequiredService<TestingDataDirectoryCleanupService>().DirectoryPath;
            Assert.IsTrue(Directory.Exists(directory));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "control-plane.db")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "work-plane.db")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "flow-plane.db")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "runtime-plane.db")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "identity.db")));
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

    private static WebApplicationFactory<global::Program> Factory(string dataDirectory) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Data:TestingDirectory", dataDirectory);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
}
