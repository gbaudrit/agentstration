using Agentstration.Aep.Client;
using Agentstration.Sources.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.ModelProviders;

public static class ModelProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationModelProviders(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useManagedProfileResolver = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var inferenceOptions = configuration.GetSection(AepInferenceOptions.SectionName).Get<AepInferenceOptions>() ?? new();
        inferenceOptions.Validate();
        services.Replace(ServiceDescriptor.Singleton(inferenceOptions));
        var transportOptions = configuration.GetSection(AepTransportSecurityOptions.SectionName).Get<AepTransportSecurityOptions>() ?? new();
        transportOptions.Validate();
        // Configuration explicitly selects these options over transport and AI fallbacks.
        services.Replace(ServiceDescriptor.Singleton(transportOptions));
        services.Replace(ServiceDescriptor.Singleton(configuration.GetSection(GenAiObservabilityOptions.SectionName).Get<GenAiObservabilityOptions>() ?? new()));
        services.TryAddTransient<GenAiHttpPayloadCaptureHandler>();
        services.AddHttpClient("agentstration-aep", client => client.Timeout = TimeSpan.FromSeconds(90))
            .ConfigurePrimaryHttpMessageHandler(services =>
                AepSecureHttpMessageHandler.Create(services.GetRequiredService<AepTransportSecurityOptions>()))
            .AddHttpMessageHandler<GenAiHttpPayloadCaptureHandler>();
        services.AddHttpClient(AepInferenceOptions.HttpClientName, client => client.Timeout = inferenceOptions.Timeout)
            .ConfigurePrimaryHttpMessageHandler(services =>
                AepSecureHttpMessageHandler.Create(services.GetRequiredService<AepTransportSecurityOptions>()))
            .AddHttpMessageHandler<GenAiHttpPayloadCaptureHandler>();
        services.AddSingleton<AepModelProvider>();
        services.AddSingleton<IModelProvider>(services => services.GetRequiredService<AepModelProvider>());
        services.AddSingleton<IModelProviderOptionsValidator>(services => services.GetRequiredService<AepModelProvider>());
        services.AddSingleton<IModelProviderDiscovery>(services => services.GetRequiredService<AepModelProvider>());
        services.AddSingleton<IModelProviderCapabilitiesResolver>(services => services.GetRequiredService<AepModelProvider>());
        services.AddSingleton<IExtensionInspector>(services => services.GetRequiredService<AepModelProvider>());
        services.AddSingleton<IExtensionOptionsMigrator>(services => services.GetRequiredService<AepModelProvider>());
        services.AddSingleton<ISourceProviderMaterializer, AepSourceProviderMaterializer>();
        services.AddSingleton<IModelProviderResolver, ModelProviderResolver>();
        if (useManagedProfileResolver)
            services.Replace(ServiceDescriptor.Singleton<IChatClientResolver, ChatClientResolver>());
        return services;
    }
}
