using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class QuartzHostLifecycleTests
{
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
            builder.UseSetting("Data:Directory", dataDirectory);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });
}
