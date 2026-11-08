using Agentstration.Agents;
using Agentstration.Extensions.Aep;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Packs.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Local;
using Agentstration.Secrets.Abstractions;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Tools;
using Agentstration.Web.Hosting;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class StandaloneRegistrationContractTests
{
    [TestMethod]
    [DataRow("Sqlite", "Deterministic", false)]
    [DataRow("Sqlite", "Deterministic", true)]
    [DataRow("Sqlite", "Managed", false)]
    [DataRow("Sqlite", "Managed", true)]
    [DataRow("PostgreSql", "Deterministic", false)]
    [DataRow("PostgreSql", "Deterministic", true)]
    public async Task CompleteServerGraphValidatesWithoutStartingStorageOrWorkers(
        string storageProvider, string aiProvider, bool enableHostedServices)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-registration-{Guid.NewGuid():N}");
        string? testingDirectory = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing",
                ApplicationName = typeof(global::Program).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Data:Directory"] = directory,
                ["AI:Provider"] = aiProvider,
                ["Agentstration:Storage:Provider"] = storageProvider,
                ["ConnectionStrings:Agentstration"] = "Host=localhost;Database=registration_contracts;Username=registration_contracts",
                ["Agentstration:Testing:HostedServicesEnabled"] = enableHostedServices.ToString(),
                ["Agentstration:Testing:OpenTelemetryEnabled"] = "false",
                ["Agentstration:Bootstrap:InitialBootstrapEnabled"] = "false"
            });
            var composition = builder.AddAgentstrationStandaloneHost();
            testingDirectory = composition.TestingDataDirectory;
            var services = builder.Services;

            AssertProductDescriptorCardinality(services);
            AssertSingleDescriptor<IRuntimeRunExecutionScope>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<IModelProfileReferenceValidator>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<IChatClientResolver>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<IFlowRunEventSink>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<TimeProvider>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<GenAiObservabilityOptions>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<GenAiHttpPayloadCaptureHandler>(services, ServiceLifetime.Transient);
            AssertSingleDescriptor<IResourceStore>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<IIdentityStore>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<IWorkItemRepository>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<IFlowRepository>(services, ServiceLifetime.Singleton);
            AssertSingleDescriptor<IRuntimeRunStore>(services, ServiceLifetime.Singleton);

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
            Assert.IsInstanceOfType<ModelProfileManagementService>(provider.GetRequiredService<IModelProfileReferenceValidator>());
            Assert.AreEqual(aiProvider == "Managed" ? typeof(ChatClientResolver) : typeof(SingleChatClientResolver),
                provider.GetRequiredService<IChatClientResolver>().GetType());
            AssertStorageProvider<IResourceStore>();
            AssertStorageProvider<IIdentityStore>();
            AssertStorageProvider<IWorkItemRepository>();
            AssertStorageProvider<IFlowRepository>();
            AssertStorageProvider<IRuntimeRunStore>();
            using var scope = provider.CreateScope();
            var identity = scope.ServiceProvider.GetRequiredService<LocalIdentityDbContext>();
            Assert.AreEqual(storageProvider == "PostgreSql" ? "Npgsql.EntityFrameworkCore.PostgreSQL" : "Microsoft.EntityFrameworkCore.Sqlite",
                identity.Database.ProviderName);

            // Resolving descriptors does not start the scheduler, open stores or execute initialization.
            Assert.IsFalse(Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories).Any());
            if (testingDirectory is not null)
                Assert.IsFalse(Directory.EnumerateFiles(testingDirectory, "*.db", SearchOption.AllDirectories).Any());

            void AssertStorageProvider<TContract>() where TContract : notnull
            {
                Assert.AreEqual(ServiceLifetime.Singleton,
                    services.Single(descriptor => descriptor.ServiceType == typeof(TContract)).Lifetime);
                StringAssert.Contains(provider.GetRequiredService<TContract>().GetType().Namespace!, storageProvider);
            }
        }
        finally
        {
            if (testingDirectory is not null && Directory.Exists(testingDirectory)) Directory.Delete(testingDirectory, recursive: true);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertProductDescriptorCardinality(IServiceCollection services)
    {
        var contributions = new Dictionary<Type, int>
        {
            [typeof(ISecretVaultProvider)] = 2,
            [typeof(IBootstrapResourceHandler)] = 21,
            [typeof(IPackResourceHandler)] = 11,
            [typeof(IAepEnrollmentResourceProvisioner)] = 5,
            [typeof(Agentstration.Parameters.IParameterUsageProvider)] = 2,
            [typeof(IToolExecutionEventSink)] = 2,
            [typeof(IInternalMcpToolDefinitionProvider)] = 34,
            [typeof(IInternalMcpToolHandler)] = 34,
            [typeof(IFlowVersionActivationGuard)] = 4,
            [typeof(IFlowDeletionGuard)] = 3,
            [typeof(IWorkExecutionScopeAccessor)] = 2
        };
        foreach (var (contract, count) in contributions)
        {
            var descriptors = services.Where(descriptor => descriptor.ServiceType == contract).ToArray();
            Assert.HasCount(count, descriptors, contract.FullName);
            var lifetime = contract == typeof(IBootstrapResourceHandler) ? ServiceLifetime.Scoped : ServiceLifetime.Singleton;
            Assert.IsTrue(descriptors.All(descriptor => descriptor.Lifetime == lifetime), contract.FullName);
        }
        var duplicates = services
            .Where(descriptor => descriptor.ServiceType.Namespace?.StartsWith("Agentstration", StringComparison.Ordinal) == true)
            .GroupBy(descriptor => descriptor.ServiceType)
            .Where(group => group.Count() > 1 && !contributions.ContainsKey(group.Key))
            .Select(group => $"{group.Key.FullName}: {group.Count()}")
            .ToArray();
        Assert.HasCount(0, duplicates, string.Join(Environment.NewLine, duplicates));
    }

    private static void AssertSingleDescriptor<TService>(IServiceCollection services, ServiceLifetime lifetime)
    {
        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(TService)).ToArray();
        Assert.HasCount(1, descriptors, typeof(TService).FullName);
        Assert.AreEqual(lifetime, descriptors[0].Lifetime, typeof(TService).FullName);
    }
}
