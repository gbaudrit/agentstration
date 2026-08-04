using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.ModelProviders;

public static class ModelProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationModelProviders(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useManagedProfileResolver = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(configuration.GetSection(GenAiObservabilityOptions.SectionName).Get<GenAiObservabilityOptions>() ?? new());
        services.AddTransient<GenAiHttpPayloadCaptureHandler>();
        services.AddSingleton<IModelProviderResolver, ModelProviderResolver>();
        if (useManagedProfileResolver) services.AddSingleton<IChatClientResolver, ChatClientResolver>();
        return services;
    }
}
