using Agentstration.Agents;
using Agentstration.Extensions.Aep;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Infrastructure.Sources;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Packs.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Local;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Work;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class RegistrationContractTests
{
    [TestMethod]
    public void InfrastructureDescriptorsHaveExplicitCardinalityAndLifetimes()
    {
        var services = CreateServices("Deterministic", false);
        var contributions = new Dictionary<Type, (int Count, ServiceLifetime Lifetime)>
        {
            [typeof(ISecretVaultProvider)] = (2, ServiceLifetime.Singleton),
            [typeof(IBootstrapResourceHandler)] = (16, ServiceLifetime.Scoped),
            [typeof(IPackResourceHandler)] = (11, ServiceLifetime.Singleton),
            [typeof(IAepEnrollmentResourceProvisioner)] = (2, ServiceLifetime.Singleton),
            [typeof(IToolExecutionEventSink)] = (2, ServiceLifetime.Singleton),
            [typeof(IInternalMcpToolDefinitionProvider)] = (33, ServiceLifetime.Singleton),
            [typeof(IInternalMcpToolHandler)] = (33, ServiceLifetime.Singleton),
            [typeof(IFlowVersionActivationGuard)] = (4, ServiceLifetime.Singleton),
            [typeof(IFlowDeletionGuard)] = (3, ServiceLifetime.Singleton)
        };

        foreach (var (contract, expected) in contributions)
        {
            var descriptors = services.Where(descriptor => descriptor.ServiceType == contract).ToArray();
            Assert.HasCount(expected.Count, descriptors, contract.FullName);
            Assert.IsTrue(descriptors.All(descriptor => descriptor.Lifetime == expected.Lifetime), contract.FullName);
            var directImplementations = descriptors.Where(descriptor => descriptor.ImplementationType is not null).ToArray();
            Assert.HasCount(directImplementations.Length,
                directImplementations.Select(descriptor => descriptor.ImplementationType).Distinct().ToArray(),
                $"Duplicate contribution for {contract.FullName}.");
        }

        var accidentalDuplicates = services
            .Where(descriptor => descriptor.ServiceType.Namespace?.StartsWith("Agentstration", StringComparison.Ordinal) == true)
            .GroupBy(descriptor => descriptor.ServiceType)
            .Where(group => group.Count() > 1 && !contributions.ContainsKey(group.Key))
            .Select(group => $"{group.Key.FullName}: {group.Count()}")
            .ToArray();
        Assert.HasCount(0, accidentalDuplicates, string.Join(Environment.NewLine, accidentalDuplicates));
        AssertSingleton<IRuntimeRunExecutionScope>(services, typeof(WorkspaceRuntimeRunExecutionScope));
        AssertSingleton<IWorkExecutionScopeAccessor>(services);
        AssertSingleton<ICurrentRequestContext>(services);
        AssertSingleton<IRequestContextScopeFactory>(services);
        AssertSingleton<TimeProvider>(services);
        Assert.HasCount(0, services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray());
        CollectionAssert.AreEqual(new Type?[]
        {
            typeof(ToolDefinitionFlowDeletionGuard),
            typeof(KnowledgeFlowDeletionGuard),
            null // EntryResourceDeletionGuard aliases its shared resource-deletion singleton.
        }, services.Where(descriptor => descriptor.ServiceType == typeof(IFlowDeletionGuard))
            .Select(descriptor => descriptor.ImplementationType).ToArray());
    }

    [TestMethod]
    [DataRow("Deterministic", false)]
    [DataRow("Deterministic", true)]
    [DataRow("Managed", false)]
    [DataRow("Managed", true)]
    public void ModelSelectionIsExplicitInEitherCompositionOrder(string aiProvider, bool modelsFirst)
    {
        var services = CreateServices(aiProvider, modelsFirst);
        AssertSingleton<IModelProfileReferenceValidator>(services);
        AssertSingleton<IChatClientResolver>(services,
            aiProvider == "Managed" ? typeof(ChatClientResolver) : typeof(SingleChatClientResolver));
        AssertSingleton<GenAiObservabilityOptions>(services);
        var capture = services.Where(descriptor => descriptor.ServiceType == typeof(GenAiHttpPayloadCaptureHandler)).ToArray();
        Assert.HasCount(1, capture);
        Assert.AreEqual(ServiceLifetime.Transient, capture[0].Lifetime);
        Assert.IsTrue(services.Single(descriptor => descriptor.ServiceType == typeof(GenAiObservabilityOptions))
            .ImplementationInstance is GenAiObservabilityOptions { Enabled: false });
    }

    [TestMethod]
    public void InfrastructurePreservesAnExplicitClockDefaultOverride()
    {
        var services = new ServiceCollection();
        var clock = new FixedClock();
        services.AddSingleton<TimeProvider>(clock);
        services.AddAgentstration(Path.GetTempPath(), enableHostedServices: false);
        AssertSingleton<TimeProvider>(services);
        Assert.AreSame(clock, services.Single(descriptor => descriptor.ServiceType == typeof(TimeProvider)).ImplementationInstance);
    }

    [TestMethod]
    [DataRow("storage")]
    [DataRow("verification")]
    [DataRow("registry")]
    [DataRow("lease")]
    [DataRow("dispatch")]
    public void InvalidCompositionOptionsFailBeforeChangingDescriptors(string invalidOption)
    {
        var options = new AgentstrationCompositionOptions { DataDirectory = Path.GetTempPath() };
        options = invalidOption switch
        {
            "storage" => options with { Storage = new AgentstrationStorageOptions { Provider = "PostgreSql" } },
            "verification" => options with { SourceVerificationIndex = new SourceVerificationIndexOptions { TimeoutSeconds = 0 } },
            "registry" => options with { SourceRegistryTransport = new SourceRegistryTransportOptions { MaximumRedirects = -1 } },
            "lease" => options with { RuntimeWorkerLease = new RuntimeWorkerLeaseOptions { HeartbeatInterval = TimeSpan.Zero } },
            "dispatch" => options with { RuntimeWorkerDispatch = new RuntimeWorkerDispatchOptions { MaximumWorkerCapacity = 0 } },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidOption))
        };
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        var existing = services.ToArray();

        Assert.Throws<InvalidOperationException>(() => services.AddAgentstration(options));

        CollectionAssert.AreEqual(existing, services.ToArray());
    }

    private static ServiceCollection CreateServices(string aiProvider, bool modelsFirst)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{GenAiObservabilityOptions.SectionName}:Enabled"] = "false"
        }).Build();
        if (modelsFirst) AddModels();
        services.AddAgentstration(Path.GetTempPath(),
            new AiProviderOptions(aiProvider, new Uri("http://localhost/"), "deterministic", null),
            enableHostedServices: false);
        if (!modelsFirst) AddModels();
        return services;

        void AddModels()
        {
            services.AddAgentstrationModelProviders(configuration, useManagedProfileResolver: aiProvider == "Managed");
            services.AddAgentstrationModelManagement();
        }
    }

    private static void AssertSingleton<TService>(IServiceCollection services, Type? implementation = null)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(TService)).ToArray();
        Assert.HasCount(1, descriptors, typeof(TService).FullName);
        Assert.AreEqual(ServiceLifetime.Singleton, descriptors[0].Lifetime, typeof(TService).FullName);
        if (implementation is not null) Assert.AreEqual(implementation, descriptors[0].ImplementationType);
    }

    private sealed class FixedClock : TimeProvider;
}
