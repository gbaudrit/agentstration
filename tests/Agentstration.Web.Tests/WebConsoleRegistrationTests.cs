using Agentstration.Web;
using Agentstration.Web.Components;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = nameof(WebConsoleRegistrationTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
