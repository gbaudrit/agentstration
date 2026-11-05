using Agentstration.Agents;
using Agentstration.ModelProviders;
using Agentstration.Parameters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Models;

public static class ModelManagementServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationModelManagement(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ModelProviderManagementService>();
        services.AddSingleton<ModelDiscoveryService>();
        services.AddSingleton<IParameterUsageProvider, ModelProviderParameterUsageProvider>();
        services.AddSingleton<IModelProviderConfigurationStore>(provider => provider.GetRequiredService<ModelProviderManagementService>());
        services.AddSingleton<ModelProfileManagementService>();
        services.AddSingleton<ModelProfileOptionMigrationService>();
        services.AddSingleton<IModelProfileStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<IModelDeploymentStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        // Model management supplies the authoritative validator instead of the standalone fallback.
        services.Replace(ServiceDescriptor.Singleton<IModelProfileReferenceValidator>(provider => provider.GetRequiredService<ModelProfileManagementService>()));
        return services;
    }
}
