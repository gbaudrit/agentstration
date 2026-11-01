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
        return await CreateAsync(profile, deployment, providerConfiguration, cancellationToken);
    }

    public async ValueTask<IChatClient> ResolveAsync(ResourceScopeRef profileScopeRef, ResourceNamespace @namespace,
        string modelProfileResourceId, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetRequiredAsync(profileScopeRef, @namespace, modelProfileResourceId, cancellationToken);
        var deployment = await deployments.GetRequiredAsync(profileScopeRef, @namespace, profile.DeploymentName, cancellationToken);
        var providerConfiguration = await providerConfigurations.GetRequiredForConsumerAsync(
            deployment.ProviderScopeRef,
            deployment.ProviderNamespace,
            deployment.ProviderName,
            profileScopeRef,
            cancellationToken);
        return await CreateAsync(profile, deployment, providerConfiguration, cancellationToken);
    }

    private async ValueTask<IChatClient> CreateAsync(ModelProfileConfiguration profile,
        ModelDeploymentConfiguration deployment, ModelProviderConfiguration providerConfiguration,
        CancellationToken cancellationToken)
    {
        var provider = providers.GetRequiredProvider(providerConfiguration.AdapterType);
        var capabilityResolver = capabilityResolvers?.SingleOrDefault(value => value.CanHandle(providerConfiguration.AdapterType));
        var capabilities = capabilityResolver is null
            ? null
            : await capabilityResolver.ResolveCapabilitiesAsync(providerConfiguration, deployment, cancellationToken);
        if (capabilities is not null && deployment.EffectiveSpecification is not null)
            capabilities = capabilities with { Model = ModelSpecificationCapabilities.Map(deployment.EffectiveSpecification) };
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Resolved model profile {ModelProfile} to deployment {Deployment}, provider {ProviderType}/{ProviderName}, and model {ModelName}",
                profile.Name,
                deployment.Name,
                providerConfiguration.AdapterType,
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
                providerConfiguration.ContributionId,
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
