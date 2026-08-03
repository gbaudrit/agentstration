using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ModelProviders;

public static class ModelProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationModelProviders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<IModelProfileStore, ConfigurationModelProfileStore>();
        services.AddSingleton<IModelDeploymentStore, ConfigurationModelDeploymentStore>();
        services.AddSingleton<IModelProviderConfigurationStore, ConfigurationModelProviderStore>();
        services.AddSingleton<IModelProviderResolver, ModelProviderResolver>();
        services.AddSingleton<IChatClientResolver, ChatClientResolver>();
        return services;
    }
}
