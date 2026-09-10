using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Core;

public static class ModelManagementServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationModelManagement(this IServiceCollection services)
    {
        services.AddSingleton<ModelProviderManagementService>();
        services.AddSingleton<SourceProviderManagementService>();
        services.AddSingleton<IModelProviderConfigurationStore>(provider => provider.GetRequiredService<ModelProviderManagementService>());
        services.AddSingleton<ModelProfileManagementService>();
        services.AddSingleton<ModelProfileOptionMigrationService>();
        services.AddSingleton<IModelProfileStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.AddSingleton<IModelDeploymentStore>(provider => provider.GetRequiredService<ModelProfileManagementService>());
        services.Replace(ServiceDescriptor.Singleton<IModelProfileReferenceValidator>(
            provider => provider.GetRequiredService<ModelProfileManagementService>()));
        services.AddSingleton<ExtensionRegistrationManagementService>();
        services.AddSingleton<AepEnrollmentSettingsService>();
        services.AddSingleton<AepEnrollmentService>();
        services.AddSingleton<IResourceReferenceResolver, ResourceReferenceResolver>();
        services.AddSingleton<ResourceScopeOperationService>();
        services.AddSingleton<ResourceScopeInventoryService>();
        services.AddSingleton<ExtensionManagementService>();
        services.AddSingleton<ExtensionInventoryService>();
        return services;
    }
}

