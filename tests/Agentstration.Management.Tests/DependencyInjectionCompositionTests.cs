using Agentstration.Flow.Storage.Abstractions;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Agents;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Local;
using Agentstration.Secrets.Abstractions;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DependencyInjectionCompositionTests
{
    [TestMethod]
    public void DeterministicSqliteCompositionHasExplicitSingleServiceBindings()
    {
        var services = Compose(AgentstrationStorageProvider.Sqlite, "Deterministic");

        AssertSingleServiceContracts(services);
        Assert.AreEqual(1, Count<IChatClientResolver>(services));
        Assert.AreEqual(2, Count<IAgentDeploymentProvisioner>(services));
        Assert.AreEqual(2, Count<ISecretVaultProvider>(services));
        Assert.AreEqual(10, Count<IBootstrapResourceHandler>(services));
        Assert.AreEqual(6, Count<IPackResourceHandler>(services));
        Assert.AreEqual(2, Count<IToolExecutionEventSink>(services));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.IsInstanceOfType<SingleChatClientResolver>(
            provider.GetRequiredService<IChatClientResolver>());
        Assert.AreSame(
            provider.GetRequiredService<ModelProfileManagementService>(),
            provider.GetRequiredService<IModelProfileReferenceValidator>());
    }

    [TestMethod]
    public void ManagedSqliteCompositionSelectsManagedResolverWithoutDuplicateFallbacks()
    {
        var services = Compose(AgentstrationStorageProvider.Sqlite, "Managed");

        AssertSingleServiceContracts(services);
        Assert.AreEqual(1, Count<IChatClientResolver>(services));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.IsInstanceOfType<ChatClientResolver>(
            provider.GetRequiredService<IChatClientResolver>());
        Assert.AreSame(
            provider.GetRequiredService<ModelProfileManagementService>(),
            provider.GetRequiredService<IModelProfileReferenceValidator>());
    }

    [TestMethod]
    public void PostgreSqlCompositionSelectsOneStoreForEachPlane()
    {
        var services = Compose(AgentstrationStorageProvider.PostgreSql, "Deterministic");

        AssertSingleServiceContracts(services);
        Assert.AreEqual(1, Count<IControlPlaneStore>(services));
        Assert.AreEqual(1, Count<IWorkItemRepository>(services));
        Assert.AreEqual(1, Count<IWorkplaceRepository>(services));
        Assert.AreEqual(1, Count<IFlowRepository>(services));
        Assert.AreEqual(1, Count<IRuntimeRunStore>(services));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static ServiceCollection Compose(
        AgentstrationStorageProvider storageProvider,
        string aiProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        var storageOptions = new AgentstrationStorageOptions
        {
            Provider = storageProvider.ToString(),
            ConnectionString = storageProvider == AgentstrationStorageProvider.PostgreSql
                ? "Host=localhost;Database=agentstration;Username=test;Password=test"
                : null
        };
        services.AddAgentstration(new AgentstrationServiceRegistrationOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "agentstration-di-contracts"),
            AiOptions = new AiProviderOptions(
                aiProvider,
                new Uri("http://localhost/"),
                "deterministic",
                null),
            StorageOptions = storageOptions,
            EnableHostedServices = false
        });
        services.AddAgentstrationModelProviders(
            configuration,
            string.Equals(aiProvider, "Managed", StringComparison.Ordinal));
        services.AddAgentstrationModelManagement();
        services.AddScoped<ILocalAccountPrincipalResolver, StubLocalAccountPrincipalResolver>();
        return services;
    }

    private static void AssertSingleServiceContracts(IServiceCollection services)
    {
        Assert.AreEqual(1, Count<TimeProvider>(services));
        Assert.AreEqual(1, Count<GenAiObservabilityOptions>(services));
        Assert.AreEqual(1, Count<GenAiHttpPayloadCaptureHandler>(services));
        Assert.AreEqual(1, Count<IModelProfileReferenceValidator>(services));
        Assert.AreEqual(1, Count<IRuntimeRunExecutionScope>(services));
        Assert.AreEqual(1, Count<IAgentstrationStorageInitializer>(services));
        Assert.AreEqual(1, Count<IControlPlaneStore>(services));
        Assert.AreEqual(1, Count<IWorkItemRepository>(services));
        Assert.AreEqual(1, Count<IFlowRepository>(services));
        Assert.AreEqual(1, Count<IRuntimeRunStore>(services));
    }

    private static int Count<TService>(IServiceCollection services) =>
        services.Count(descriptor => descriptor.ServiceType == typeof(TService));

    private sealed class StubLocalAccountPrincipalResolver : ILocalAccountPrincipalResolver
    {
        public Task<Principal?> ResolveByUserNameAsync(
            string userName,
            CancellationToken cancellationToken) =>
            Task.FromResult<Principal?>(null);
    }
}
