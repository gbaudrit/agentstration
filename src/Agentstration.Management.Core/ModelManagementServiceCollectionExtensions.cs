using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Core;

public static class ModelManagementServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationModelManagement(this IServiceCollection services)
    {
        services.AddSingleton<ModelProviderManagementService>();
        services.AddSingleton<IModelProviderConfigurationStore>(provider => provider.GetRequiredService<ModelProviderManagementService>());
        services.AddSingleton<ModelProfileManagementService>();
        services.AddSingleton<ModelProfileOptionMigrationService>();
        services.AddSingleton<IModelProfileStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<IModelDeploymentStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<IModelProfileReferenceValidator>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<ExtensionRegistrationManagementService>();
        services.AddSingleton<ExtensionManagementService>();
        return services;
    }
}

