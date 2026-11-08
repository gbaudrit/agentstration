using Agentstration.Console.Web.Configuration;
using Agentstration.Console.Web.Security;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Console.Web.Tests;

[TestClass]
public sealed class ConsoleCompositionRegistrationTests
{
    [TestMethod]
    public void ConsoleCompositionSelectsOneScopedDelegatingFactoryAndPreservesPooledHandlers()
    {
        var services = new ServiceCollection();
        var clock = new ConsoleTestTimeProvider();
        services.AddSingleton<TimeProvider>(clock);

        services.AddAgentstrationConsoleHost(new ConfigurationBuilder().Build());

        Assert.AreSame(clock, services.Single(value => value.ServiceType == typeof(TimeProvider)).ImplementationInstance);
        AssertSingle<IHttpClientFactory>(services, ServiceLifetime.Scoped);
        AssertSingle<IHttpMessageHandlerFactory>(services, ServiceLifetime.Singleton);
        AssertSingle<IConsoleContextHttpClientFactory>(services, ServiceLifetime.Scoped);
        AssertSingle<BffDelegatingHttpClientFactory>(services, ServiceLifetime.Scoped);
        AssertSingle<IBffServerSessionStore>(services, ServiceLifetime.Singleton);
        AssertSingle<BffDelegationTokenCache>(services, ServiceLifetime.Singleton);
        AssertSingle<BffDelegationTokenProvider>(services, ServiceLifetime.Scoped);
        AssertSingle<IManagementApiClient>(services, ServiceLifetime.Scoped);
        AssertSingle<IWorkOperationsRealtimeClient>(services, ServiceLifetime.Scoped);
        AssertSingle<IConsoleRealtimeConnectionConfigurator>(services, ServiceLifetime.Singleton);
        AssertSingle<IConsoleContextProvider>(services, ServiceLifetime.Scoped);
        AssertSingle<IResourceSearchProvider>(services, ServiceLifetime.Scoped);
        AssertSingle<IPlatformStatusProvider>(services, ServiceLifetime.Scoped);
        AssertSingle<IUserPreferencesClient>(services, ServiceLifetime.Scoped);
        AssertSingle<IConnectedServicesClient>(services, ServiceLifetime.Scoped);
        AssertSingle<IStoragesClient>(services, ServiceLifetime.Scoped);
        AssertSingle<IStorageTypesClient>(services, ServiceLifetime.Scoped);
    }

    [TestMethod]
    public void DelegationTargetsAreDistinctIntentionalContributions()
    {
        var services = new ServiceCollection();
        services.AddAgentstrationConsoleHost(new ConfigurationBuilder().Build());

        var targets = services.Where(value => value.ServiceType == typeof(BffDelegationTarget)).ToArray();
        Assert.IsTrue(targets.Length > 1);
        Assert.IsTrue(targets.All(value => value.Lifetime == ServiceLifetime.Singleton));
        var configured = targets.Select(value => (BffDelegationTarget)value.ImplementationInstance!).ToArray();
        Assert.AreEqual(configured.Length, configured.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var (name, audience) in new[]
        {
            (CleanupApiClient.ManagementClient, InternalDelegationAudiences.Management),
            (CleanupApiClient.WorkClient, InternalDelegationAudiences.Work),
            (CleanupApiClient.FlowClient, InternalDelegationAudiences.Flow),
            (CleanupApiClient.RuntimeClient, InternalDelegationAudiences.Runtime)
        })
        {
            Assert.AreEqual(audience, configured.Single(value => value.Name == name).Audience);
        }
    }

    private static void AssertSingle<TService>(IServiceCollection services, ServiceLifetime expectedLifetime) =>
        Assert.AreEqual(expectedLifetime, services.Single(value => value.ServiceType == typeof(TService)).Lifetime);

    private sealed class ConsoleTestTimeProvider : TimeProvider;
}
