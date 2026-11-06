using Agentstration.Application.Work;
using Agentstration.Flows.Application;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ResourceManagement;
using Agentstration.ResourcePlanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

// Real application composition and private SQLite stores; no HTTP host or workers.
internal sealed class ResourceServiceFixture : IAsyncDisposable
{
    private readonly string directory;
    public ServiceProvider Services { get; }
    public RequestContext Context { get; }

    private ResourceServiceFixture(string directory, ServiceProvider services, RequestContext context)
    {
        this.directory = directory;
        Services = services;
        Context = context;
    }

    public static async Task<ResourceServiceFixture> CreateAsync(
        TimeProvider? clock = null, Action<IServiceCollection>? configure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-services-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string Connection(string name) => $"Data Source={Path.Combine(directory, name + ".db")};Pooling=False";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new LocalBootstrapOptions { GrantPlatformAdministrator = true });
        services.AddAgentstration(directory,
            controlPlaneConnectionString: Connection("management"),
            workPlaneConnectionString: Connection("work"),
            flowConnectionString: Connection("flow"),
            runtimeConnectionString: Connection("runtime"),
            enableHostedServices: false);
        if (clock is not null)
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(clock);
        }
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        try
        {
            using (provider.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            {
                await provider.GetRequiredService<IAgentstrationStorageInitializer>().InitializeAsync(default);
                await provider.GetRequiredService<IResourceStore>().InitializeAsync(default);
                await provider.GetRequiredService<FlowService>().InitializeAsync(default);
                await provider.GetRequiredService<WorkplaceService>().InitializeAsync(default);
                await provider.GetRequiredService<ResourcePlanService>().InitializeAsync(default);
            }
            var context = await provider.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            return new(directory, provider, context);
        }
        catch
        {
            await provider.DisposeAsync();
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }
}
