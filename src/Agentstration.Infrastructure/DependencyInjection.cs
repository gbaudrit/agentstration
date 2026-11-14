using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Sources;
using Agentstration.Runtime.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentstration(
        this IServiceCollection services,
        AgentstrationCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        var context = new AgentstrationCompositionContext(options);
        services.AddAgentstrationFoundation();
        services.AddAgentstrationAi(context);
        services.AddAgentstrationStorage(context);
        services.AddAgentstrationSecurity(context);
        services.AddAgentstrationBootstrapHandlers();
        services.AddAgentstrationResourcePlanning();
        services.AddAgentstrationAgents(context);
        services.AddAgentstrationPacks(context);
        services.AddAgentstrationSources(context);
        services.AddAgentstrationSchemas();
        services.AddAgentstrationFunctions();
        services.AddAgentstrationTools();
        services.AddAgentstrationTriggerScheduling(context);
        services.AddAgentstrationRuntimeRuns(context);
        services.AddAgentstrationArtifacts(context);
        services.AddAgentstrationWorkPlane();
        services.AddAgentstrationInternalTools(context);
        services.AddAgentstrationDataAndKnowledge(context);
        services.AddAgentstrationFlowPlane();
        return services;
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
        SourceVerificationIndexOptions? sourceVerificationIndexOptions = null,
        SourceRegistryTransportOptions? sourceRegistryTransportOptions = null,
        RuntimeWorkerLeaseOptions? runtimeWorkerLeaseOptions = null,
        RuntimeWorkerDispatchOptions? runtimeWorkerDispatchOptions = null)
        => services.AddAgentstration(new AgentstrationCompositionOptions
        {
            DataDirectory = dataDirectory,
            Ai = aiOptions ?? new AiProviderOptions("Deterministic", new Uri("http://localhost/"), "deterministic", null),
            ControlPlaneConnectionString = controlPlaneConnectionString,
            WorkPlaneConnectionString = workPlaneConnectionString,
            FlowConnectionString = flowConnectionString,
            RuntimeConnectionString = runtimeConnectionString,
            Storage = storageOptions ?? new(),
            EnableHostedServices = enableHostedServices,
            SourceVerificationIndex = sourceVerificationIndexOptions ?? new(),
            SourceRegistryTransport = sourceRegistryTransportOptions ?? new(),
            RuntimeWorkerLease = runtimeWorkerLeaseOptions ?? new(),
            RuntimeWorkerDispatch = runtimeWorkerDispatchOptions ?? new()
        });
}
