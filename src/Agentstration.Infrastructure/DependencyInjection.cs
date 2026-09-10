using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentstration(
        this IServiceCollection services,
        AgentstrationServiceRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var composition = AgentstrationServiceRegistrationContext.Create(options);
        return services
            .AddAgentstrationFoundation(composition)
            .AddAgentstrationControlPlane(composition)
            .AddAgentstrationSecurityAndBootstrap(composition)
            .AddAgentstrationAgentRuntime(composition)
            .AddAgentstrationPacks(composition)
            .AddAgentstrationSources(composition)
            .AddAgentstrationToolingAndTriggers(composition)
            .AddAgentstrationRuntimeRuns(composition)
            .AddAgentstrationWorkPlane(composition)
            .AddAgentstrationFlowPlane(composition);
    }

    public static IServiceCollection AddAgentstration(
        this IServiceCollection services,
        string dataDirectory,
        AiProviderOptions? aiOptions = null,
        string? controlPlaneConnectionString = null,
        string? workPlaneConnectionString = null,
        string? flowConnectionString = null,
        string? runtimeConnectionString = null,
        AgentstrationStorageOptions? storageOptions = null,
        bool enableHostedServices = true,
        SourceVerificationIndexOptions? sourceVerificationIndexOptions = null) =>
        services.AddAgentstration(new AgentstrationServiceRegistrationOptions
        {
            DataDirectory = dataDirectory,
            AiOptions = aiOptions,
            ControlPlaneConnectionString = controlPlaneConnectionString,
            WorkPlaneConnectionString = workPlaneConnectionString,
            FlowConnectionString = flowConnectionString,
            RuntimeConnectionString = runtimeConnectionString,
            StorageOptions = storageOptions,
            EnableHostedServices = enableHostedServices,
            SourceVerificationIndexOptions = sourceVerificationIndexOptions
        });
}
