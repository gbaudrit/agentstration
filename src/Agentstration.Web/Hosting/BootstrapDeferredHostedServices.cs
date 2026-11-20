using Agentstration.Infrastructure;

namespace Agentstration.Web.Hosting;

internal sealed class BootstrapDeferredHostedServices(
    BootstrapStartupState startup,
    IReadOnlyList<IHostedService> services) : BackgroundService
{
    private int startedCount;
    internal IReadOnlyList<IHostedService> Workers => services;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await startup.WaitUntilReadyAsync(stoppingToken); }
        catch (OperationCanceledException) when (startup.Failure is not null && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }
        // Keep original ordering: in particular Quartz's schema initializer must
        // finish before its scheduler starts accepting work.
        foreach (var service in services)
        {
            await service.StartAsync(stoppingToken);
            startedCount++;
        }
        var running = services.OfType<BackgroundService>().Select(service => service.ExecuteTask)
            .OfType<Task>().ToList();
        while (running.Count > 0)
        {
            var completed = await Task.WhenAny(running).WaitAsync(stoppingToken);
            await completed;
            running.Remove(completed);
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        for (var index = startedCount - 1; index >= 0; index--)
            await services[index].StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        for (var index = services.Count - 1; index >= 0; index--)
            (services[index] as IDisposable)?.Dispose();
    }
}

internal static class BootstrapDeferredHostedServiceRegistration
{
    internal static void DeferBootstrapWorkers(this IServiceCollection services)
    {
        var workers = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType != typeof(ModelProviderDiscoveryWorker)
            && IsApplicationWorker(descriptor)).ToArray();
        if (workers.Length == 0) return;
        foreach (var worker in workers) services.Remove(worker);
        services.AddSingleton<IHostedService>(provider => new BootstrapDeferredHostedServices(
            provider.GetRequiredService<BootstrapStartupState>(), workers.Select(descriptor =>
                (IHostedService)(descriptor.ImplementationInstance
                    ?? descriptor.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!))).ToArray()));
    }

    private static bool IsApplicationWorker(ServiceDescriptor descriptor)
    {
        var assembly = (descriptor.ImplementationType
            ?? descriptor.ImplementationFactory?.Method.DeclaringType
            ?? descriptor.ImplementationInstance?.GetType())?.Assembly.GetName().Name;
        return assembly?.StartsWith("Agentstration.", StringComparison.Ordinal) == true
            || assembly?.StartsWith("Quartz", StringComparison.Ordinal) == true;
    }
}
