using Agentstration.Infrastructure;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupLifecycleTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplicationStartedWaitsForBootstrapOrRetainedAdministration(bool failed)
    {
        var startup = new BootstrapStartupState();
        startup.Begin();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(startup);
            services.AddSingleton<BootstrapStartupLifecycle>();
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BootstrapStartupLifecycle>());
        }).Build();
        var starting = host.StartAsync();
        await host.Services.GetRequiredService<BootstrapStartupLifecycle>().ListenerStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.IsFalse(starting.IsCompleted);
        Assert.IsFalse(lifetime.ApplicationStarted.IsCancellationRequested);
        if (failed)
        {
            startup.EnableAdministration();
            startup.Fail("required_contribution_unavailable");
        }
        else startup.Complete();
        await starting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.AreEqual(!failed, startup.IsReady);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task CancellationDuringBootstrapDoesNotPublishApplicationStarted()
    {
        var startup = new BootstrapStartupState();
        startup.Begin();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(startup);
            services.AddSingleton<BootstrapStartupLifecycle>();
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BootstrapStartupLifecycle>());
        }).Build();
        using var cancellation = new CancellationTokenSource();
        var starting = host.StartAsync(cancellation.Token);
        await host.Services.GetRequiredService<BootstrapStartupLifecycle>().ListenerStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => starting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.IsFalse(startup.IsReady);
    }
}
