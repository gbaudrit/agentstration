using System.Data.Common;
using Agentstration.Management.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Agentstration.ModelProviders;

public static class ModelProviderConfigurationSections
{
    public const string Root = "Agentstration:ModelProviders";
}

public sealed class ConfigurationModelProfileStore(IConfiguration configuration) : IModelProfileStore
{
    public ValueTask<ModelProfileConfiguration> GetRequiredAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ConfigurationModelStoreHelpers.GetConfigurationName(resourceId, nameof(resourceId));
        var section = configuration.GetSection($"{ModelProviderConfigurationSections.Root}:Profiles:{name}");
        if (!section.Exists()) throw new ModelProfileNotFoundException(resourceId);
        var deployment = section["DeploymentName"] ?? section["Deployment"];
        if (string.IsNullOrWhiteSpace(deployment))
            throw new ModelProviderConfigurationException($"Model profile '{name}' must reference a deployment.");
        return ValueTask.FromResult(new ModelProfileConfiguration
        {
            Name = name,
            DeploymentName = deployment,
            Generation = section.GetSection("Generation").Get<ModelGenerationOptions>() ?? new ModelGenerationOptions(),
            Reasoning = section.GetSection("Reasoning").Get<ModelReasoningOptions>() ?? new ModelReasoningOptions(),
            Output = section.GetSection("Output").Get<ModelOutputOptions>() ?? new ModelOutputOptions()
        });
    }
}

public sealed class ConfigurationModelDeploymentStore(IConfiguration configuration) : IModelDeploymentStore
{
    public ValueTask<ModelDeploymentConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var section = configuration.GetSection($"{ModelProviderConfigurationSections.Root}:Deployments:{name}");
        if (!section.Exists()) throw new ModelDeploymentNotFoundException(name);
        var providerName = section["ProviderName"] ?? section["Provider"];
        var modelName = section["ModelName"] ?? section["Model"];
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ModelProviderConfigurationException($"Model deployment '{name}' must reference a provider configuration.");
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ModelProviderConfigurationException($"Model deployment '{name}' must specify a model name.");
        return ValueTask.FromResult(new ModelDeploymentConfiguration { Name = name, ProviderName = providerName, ModelName = modelName });
    }
}

public sealed class ConfigurationModelProviderStore(IConfiguration configuration) : IModelProviderConfigurationStore
{
    public ValueTask<ModelProviderConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var section = configuration.GetSection($"{ModelProviderConfigurationSections.Root}:Providers:{name}");
        if (!section.Exists()) throw new ModelProviderConfigurationNotFoundException(name);
        var providerType = section["ProviderType"];
        if (string.IsNullOrWhiteSpace(providerType))
            throw new ModelProviderConfigurationException($"Model provider configuration '{name}' must specify a provider type.");

        var endpointValue = section["Endpoint"];
        var connectionName = section["ConnectionName"];
        if (!string.IsNullOrWhiteSpace(connectionName))
            endpointValue = ConfigurationModelStoreHelpers.GetEndpoint(configuration.GetConnectionString(connectionName)) ?? endpointValue;
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ModelProviderConfigurationException($"Model provider configuration '{name}' must resolve to an absolute HTTP(S) endpoint.");
        return ValueTask.FromResult(new ModelProviderConfiguration
        {
            Name = name,
            ProviderType = providerType,
            Endpoint = endpoint,
            DisplayName = section["DisplayName"],
            ManagementMode = section["ManagementMode"] ?? "external",
            EndpointDisplayName = section["EndpointDisplayName"] ?? connectionName,
            Capabilities = section.GetSection("Capabilities").Get<string[]>() ?? ["chat"]
        });
    }

    public async ValueTask<IReadOnlyList<ModelProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default)
    {
        var values = new List<ModelProviderConfiguration>();
        foreach (var section in configuration.GetSection($"{ModelProviderConfigurationSections.Root}:Providers").GetChildren())
            values.Add(await GetRequiredAsync(section.Key, cancellationToken));
        return values;
    }
}

internal static class ConfigurationModelStoreHelpers
{
    public static string GetConfigurationName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? value : segments[^1];
    }

    public static string? GetEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out _)) return connectionString;
        try
        {
            var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
            return values.TryGetValue("Endpoint", out var endpoint) ? endpoint?.ToString() : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
