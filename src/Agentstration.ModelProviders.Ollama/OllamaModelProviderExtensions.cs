using Agentstration.ModelProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.ModelProviders.Ollama;

public static class OllamaModelProviderExtensions
{
    public static IHostApplicationBuilder AddOllamaModelProvider(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpClient("agentstration-ollama-dynamic", client => client.Timeout = TimeSpan.FromSeconds(90))
            .AddHttpMessageHandler<GenAiHttpPayloadCaptureHandler>();
        builder.Services.AddSingleton<IOllamaClientFactory, OllamaClientFactory>();
        builder.Services.AddSingleton<OllamaModelProvider>();
        builder.Services.AddSingleton<IModelProvider>(provider => provider.GetRequiredService<OllamaModelProvider>());
        builder.Services.AddSingleton<IModelProviderOptionsValidator>(provider => provider.GetRequiredService<OllamaModelProvider>());
        builder.Services.AddSingleton<OllamaModelProviderDiscovery>();
        builder.Services.AddSingleton<IModelProviderDiscovery>(provider => provider.GetRequiredService<OllamaModelProviderDiscovery>());
        return builder;
    }
}
