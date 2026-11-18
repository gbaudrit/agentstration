using Agentstration.Identity.Contracts;
using Agentstration.Web;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class WebConsoleRegistrationTests
{
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
        services.AddSingleton<ICurrentRequestContext, SystemOperationRequestContext>();
        services.AddScoped<NavigationManager, TestNavigationManager>();
        services.AddAgentstrationWebConsole(configuration, new TestHostEnvironment());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();

        Assert.IsInstanceOfType<ManagementApiClient>(scope.ServiceProvider.GetRequiredService<IAgentRunnerManagementClient>());
        Assert.IsInstanceOfType<RuntimeApiClient>(scope.ServiceProvider.GetRequiredService<IAgentRunnerRuntimeClient>());
        Assert.IsInstanceOfType<ManagementApiClient>(scope.ServiceProvider.GetRequiredService<IManagementApiClient>());
        Assert.IsInstanceOfType<WorkApiClient>(scope.ServiceProvider.GetRequiredService<IWorkApiClient>());
        Assert.IsInstanceOfType<EntryAdministrationApiClient>(scope.ServiceProvider.GetRequiredService<IEntryAdministrationApiClient>());
        Assert.IsInstanceOfType<StoragesApiClient>(scope.ServiceProvider.GetRequiredService<IStoragesClient>());
        Assert.IsInstanceOfType<StoragesApiClient>(scope.ServiceProvider.GetRequiredService<IStorageTypesClient>());
        Assert.IsInstanceOfType<PacksApiClient>(scope.ServiceProvider.GetRequiredService<IPacksClient>());
        Assert.IsInstanceOfType<HttpAgentstrationEventStream>(scope.ServiceProvider.GetRequiredService<IAgentstrationEventStream>());
        Assert.IsInstanceOfType<ConsoleResourceSearchProvider>(scope.ServiceProvider.GetRequiredService<IResourceSearchProvider>());
    }

    [TestMethod]
    public void ConsoleReplacesComponentFallbacksAndPreservesTheConfiguredClock()
    {
        var services = new ServiceCollection();
        var clock = new TestTimeProvider();
        services.AddSingleton<TimeProvider>(clock);
        services.AddAgentstrationWebConsole(new ConfigurationBuilder().Build(), new TestHostEnvironment());
        // Reusable components can be composed before or after their host selections.
        services.AddAgentstrationWebComponents();

        foreach (var contract in new[]
        {
            typeof(TimeProvider),
            typeof(IConsoleContextProvider),
            typeof(IResourceSearchProvider),
            typeof(ICommandPaletteFallbackProvider),
            typeof(IPlatformStatusProvider),
            typeof(IUserPreferencesClient),
            typeof(IConsoleRealtimeConnectionConfigurator),
            typeof(IConsoleContextHttpClientFactory),
            typeof(IAgentstrationEventStream),
            typeof(IWorkOperationsRealtimeClient)
        })
        {
            Assert.HasCount(1, services.Where(descriptor => descriptor.ServiceType == contract).ToArray(), contract.FullName);
        }
        Assert.AreSame(clock, services.Single(descriptor => descriptor.ServiceType == typeof(TimeProvider)).ImplementationInstance);
        Assert.AreEqual(ServiceLifetime.Scoped, services.Single(descriptor => descriptor.ServiceType == typeof(IUserPreferencesClient)).Lifetime);
    }

    private sealed class TestTimeProvider : TimeProvider;

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = nameof(WebConsoleRegistrationTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/?context=%2Finstance");
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }
}
