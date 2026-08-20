using Agentstration.Resources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agentstration.ModelProviders;

public sealed class ChatClientResolver(
    IModelProfileStore profiles,
    IModelDeploymentStore deployments,
    IModelProviderConfigurationStore providerConfigurations,
    IModelProviderResolver providers,
    GenAiObservabilityOptions observability,
    ILoggerFactory loggerFactory,
    ILogger<ChatClientResolver> logger,
    IEnumerable<IModelProviderCapabilitiesResolver>? capabilityResolvers = null) : IChatClientResolver
{
    public ValueTask<IChatClient> ResolveAsync(string modelProfileResourceId, CancellationToken cancellationToken = default) =>
        ResolveAsync(ResourceNamespace.Default, modelProfileResourceId, cancellationToken);

    public async ValueTask<IChatClient> ResolveAsync(ResourceNamespace @namespace, string modelProfileResourceId, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetRequiredAsync(@namespace, modelProfileResourceId, cancellationToken);
        var deployment = await deployments.GetRequiredAsync(@namespace, profile.DeploymentName, cancellationToken);
        var providerConfiguration = await providerConfigurations.GetRequiredAsync(deployment.ProviderNamespace, deployment.ProviderName, cancellationToken);
        var provider = providers.GetRequiredProvider(providerConfiguration.ProviderType);
        var capabilityResolver = capabilityResolvers?.SingleOrDefault(value => value.CanHandle(providerConfiguration.ProviderType));
        var capabilities = capabilityResolver is null
            ? null
            : await capabilityResolver.ResolveCapabilitiesAsync(providerConfiguration, deployment, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Resolved model profile {ModelProfile} to deployment {Deployment}, provider {ProviderType}/{ProviderName}, and model {ModelName}",
                profile.Name,
                deployment.Name,
                providerConfiguration.ProviderType,
                providerConfiguration.Name,
                deployment.ModelName);
        }
        var client = provider.CreateChatClient(providerConfiguration, deployment);
        if (observability.Enabled)
        {
            client = client.AsBuilder()
                .UseOpenTelemetry(
                    loggerFactory,
                    GenAiObservabilityOptions.ChatClientSourceName,
                    telemetry => telemetry.EnableSensitiveData = false)
                .Build();
        }
        return new ResolvedModelChatClient(
            client,
            new ModelChatClientMetadata(
                profile.Name,
                deployment.Name,
                providerConfiguration.ProviderType,
                providerConfiguration.Name,
                deployment.ModelName,
                profile.Generation,
                profile.Reasoning,
                profile.Output,
                profile.ProviderOptions,
                capabilities?.Provider,
                capabilities?.Model,
                capabilities?.Adapter));
    }
}
