extern alias awpworker;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Worker = awpworker::Agentstration.Runtime.Worker.MicrosoftAgentFramework;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class RuntimeWorkerHealthEndpointTests
{
    [TestMethod]
    public async Task LivenessRemainsHealthyWhileRegistrationReadinessChanges()
    {
        await using var factory = new WebApplicationFactory<awpworker::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:RuntimeWorker:WorkerId", Guid.NewGuid().ToString());
            builder.UseSetting("Agentstration:RuntimeWorker:CredentialId", Guid.NewGuid().ToString());
            builder.UseSetting("Agentstration:RuntimeWorker:SharedKeyFile", "/unused-test-key");
            builder.UseSetting("Agentstration:RuntimeWorker:AllowInsecureHttp", "true");
            builder.ConfigureTestServices(services =>
            {
                // Test the real HTTP health routes without starting an authority or registration loop.
                foreach (var service in services.Where(value => value.ServiceType == typeof(IHostedService)
                    && value.ImplementationType == typeof(Worker.AwpRuntimeWorkerService)).ToArray())
                    services.Remove(service);
            });
        });
        using var client = factory.CreateClient();
        var readiness = factory.Services.GetRequiredService<Worker.RuntimeWorkerReadiness>();
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        readiness.MarkReady();
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        readiness.MarkNotReady();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
