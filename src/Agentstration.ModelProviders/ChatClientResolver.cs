using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agentstration.ModelProviders;

public sealed class ChatClientResolver(
    IModelProfileStore profiles,
    IModelDeploymentStore deployments,
    IModelProviderConfigurationStore providerConfigurations,
    IModelProviderResolver providers,
    ILogger<ChatClientResolver> logger) : IChatClientResolver
{
    public async ValueTask<IChatClient> ResolveAsync(string modelProfileResourceId, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetRequiredAsync(modelProfileResourceId, cancellationToken);
        var deployment = await deployments.GetRequiredAsync(profile.DeploymentName, cancellationToken);
        var providerConfiguration = await providerConfigurations.GetRequiredAsync(deployment.ProviderName, cancellationToken);
        var provider = providers.GetRequiredProvider(providerConfiguration.ProviderType);
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
                profile.ProviderOptions));
    }
}
