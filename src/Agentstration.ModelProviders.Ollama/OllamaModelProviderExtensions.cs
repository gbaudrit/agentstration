using Agentstration.ModelProviders;
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentstration.ModelProviders.Ollama;

public static class OllamaModelProviderExtensions
{
    public static IHostApplicationBuilder AddOllamaModelProvider(
        this IHostApplicationBuilder builder,
        string connectionName,
        Uri? fallbackEndpoint = null,
        string? fallbackModel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        var configured = builder.Configuration.GetSection(OllamaModelProviderOptions.SectionName).Get<OllamaModelProviderOptions>() ?? new();
        var connectionString = builder.Configuration.GetConnectionString(connectionName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var endpointValue = connectionString;
            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var discoveredEndpoint))
            {
                var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
                endpointValue = values.TryGetValue("Endpoint", out var endpoint) ? endpoint?.ToString() : null;
            }
            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out discoveredEndpoint))
                throw new InvalidOperationException($"Connection string '{connectionName}' must contain an absolute Ollama endpoint.");
            configured.Endpoint = discoveredEndpoint;
        }
        else if (fallbackEndpoint is not null)
        {
            configured.Endpoint = fallbackEndpoint;
        }
        if (string.IsNullOrWhiteSpace(configured.DefaultModel)) configured.DefaultModel = fallbackModel ?? string.Empty;
        configured.Validate();

        builder.Services.AddSingleton(Options.Create(configured));
        builder.AddOllamaApiClient(connectionName, settings =>
            {
                settings.Endpoint = configured.Endpoint;
                settings.SelectedModel = configured.DefaultModel;
            })
            .AddChatClient();
        builder.Services.AddSingleton<OllamaModelProvider>();
        builder.Services.AddSingleton<IModelProvider>(provider => provider.GetRequiredService<OllamaModelProvider>());
        builder.Services.AddSingleton<IModelProviderResolver, ModelProviderResolver>();
        return builder;
    }
}
