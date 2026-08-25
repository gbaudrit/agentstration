using Agentstration.Aep.Client;
using Agentstration.Aep.MicrosoftExtensionsAI;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.ModelProviders;

public sealed class AepModelProvider(IHttpClientFactory httpClients) : IModelProvider, IModelProviderOptionsValidator, IModelProviderDiscovery, IModelProviderCapabilitiesResolver, IExtensionInspector, IExtensionOptionsMigrator
{
    public const string AdapterType = "aep";
    public string ProviderType => AdapterType;
    public bool CanHandle(string providerType) => !string.IsNullOrWhiteSpace(providerType);

    public IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        if (string.IsNullOrWhiteSpace(deployment.ModelName))
            throw new ModelProviderConfigurationException($"AEP deployment '{deployment.Name}' must specify a model name.");
        var client = httpClients.CreateClient("agentstration-aep");
        client.BaseAddress = provider.Endpoint;
        deployment.ProviderOptions.TryGetValue(provider.ContributionId, out var nativeOptions);
        return new AepChatClient(
            new AepClient(client).CreateModelProvider(provider.ContributionId),
            deployment.ModelName,
            nativeOptions is null ? null : Map(nativeOptions));
    }

    public void Validate(IReadOnlyDictionary<string, System.Text.Json.JsonElement> providerOptions)
    {
        ArgumentNullException.ThrowIfNull(providerOptions);
    }

    public async ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default)
    {
        if (!provider.ExtensionEnabled)
            return new ModelProviderHealth("unavailable", $"Extension registration '{provider.Extension.Name}' is disabled.");
        try
        {
            var client = CreateClient(provider);
            var descriptor = await client.DiscoverAsync(cancellationToken);
            if (provider.ExpectedExtensionId is { Length: > 0 } expectedId
                && !string.Equals(descriptor.Extension.Id, expectedId, StringComparison.Ordinal))
                return new ModelProviderHealth("incompatible", $"Expected extension '{expectedId}', but endpoint reports '{descriptor.Extension.Id}'.");
            var contribution = descriptor.Contributions.ModelProviders.FirstOrDefault(value => string.Equals(value.Id, provider.ContributionId, StringComparison.OrdinalIgnoreCase));
            if (contribution is null) return new ModelProviderHealth("unavailable", $"The extension does not contribute model provider '{provider.ContributionId}'.");
            var health = await client.CreateModelProvider(provider.ContributionId).GetHealthAsync(cancellationToken);
            return new ModelProviderHealth(health.Status, health.Details);
        }
        catch (AepProtocolException exception)
        {
            return new ModelProviderHealth(exception.Code == "protocol_incompatible" ? "incompatible" : "unavailable", exception.Message);
        }
        catch (HttpRequestException exception) { return new ModelProviderHealth("unreachable", exception.Message); }
    }

    public async ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default)
    {
        var models = await CreateClient(provider).CreateModelProvider(provider.ContributionId).ListModelsAsync(cancellationToken);
        return models.Select(value => new DiscoveredModel(
            value.Id,
            value.DisplayName,
            "available",
            value.Capabilities ?? [],
            value.Metadata ?? new Dictionary<string, string>())).ToArray();
    }

    public async ValueTask<ResolvedModelProviderCapabilities> ResolveCapabilitiesAsync(
        ModelProviderConfiguration provider,
        ModelDeploymentConfiguration deployment,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient(provider);
        var manifest = await client.DiscoverAsync(cancellationToken);
        if (provider.ExpectedExtensionId is { Length: > 0 } expectedId
            && !string.Equals(manifest.Extension.Id, expectedId, StringComparison.Ordinal))
            throw new ModelProviderConfigurationException($"Expected extension '{expectedId}', but endpoint reports '{manifest.Extension.Id}'.");
        var contribution = manifest.Contributions.ModelProviders.SingleOrDefault(
            value => string.Equals(value.Id, provider.ContributionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModelProviderConfigurationException($"The AEP extension does not contribute model provider '{provider.ContributionId}'.");
        if (deployment.ProviderOptions.TryGetValue(provider.ContributionId, out var nativeOptions))
        {
            var catalog = await client.GetConfigurationAsync(cancellationToken);
            ValidateNativeOptions(provider.ContributionId, nativeOptions, catalog);
        }
        var models = await client.CreateModelProvider(provider.ContributionId).ListModelsAsync(cancellationToken);
        var model = models.SingleOrDefault(value => string.Equals(value.Id, deployment.ModelName, StringComparison.Ordinal));
        if (model is null) throw new ModelProviderConfigurationException($"Model '{deployment.ModelName}' is not available from provider '{provider.Name}'.");
        return new ResolvedModelProviderCapabilities(
            Map(contribution.Capabilities),
            Map(model.Capabilities ?? []),
            new AgentRuntimeCapabilities
            {
                Streaming = new(CapabilitySupport.Native),
                Tools = new(CapabilitySupport.Native),
                StructuredOutput = new(CapabilitySupport.Native),
                Reasoning = new ReasoningCapability { Support = CapabilitySupport.Partial }
            });
    }

    public bool CanInspectEndpoint(Uri endpoint) => endpoint.Scheme is "http" or "https";

    public ValueTask<ExtensionInspection> InspectAsync(
        ModelProviderConfiguration provider,
        CancellationToken cancellationToken = default) =>
        InspectAsync(provider.Name, provider.Endpoint, cancellationToken);

    public async ValueTask<ExtensionInspection> InspectAsync(
        string registrationName,
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient(endpoint);
            var manifest = await client.DiscoverAsync(cancellationToken);
            var catalog = await client.GetConfigurationAsync(cancellationToken);
            return new ExtensionInspection(
                registrationName,
                endpoint,
                "available",
                new ExtensionIdentity(
                    manifest.Extension.Id,
                    manifest.Extension.Name,
                    manifest.Extension.Version,
                    manifest.Extension.Description),
                manifest.Contributions.ModelProviders
                    .Select(value => new ExtensionContribution(Agentstration.Aep.Abstractions.AepContributionKinds.ModelProvider, value.Id))
                    .Concat((manifest.Contributions.Tools ?? [])
                        .Select(value => new ExtensionContribution(Agentstration.Aep.Abstractions.AepContributionKinds.Tool, value.Id)))
                    .ToArray(),
                catalog.OptionSets.Select(Map).ToArray());
        }
        catch (AepProtocolException exception)
        {
            return new ExtensionInspection(
                registrationName,
                endpoint,
                exception.Code == "protocol_incompatible" ? "incompatible" : "unavailable",
                null,
                [],
                [],
                exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return new ExtensionInspection(registrationName, endpoint, "unavailable", null, [], [], exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ExtensionInspection(registrationName, endpoint, "unavailable", null, [], [], exception.Message);
        }
    }

    public async ValueTask<Agentstration.Management.Abstractions.VersionedExtensionOptions> MigrateAsync(
        ModelProviderConfiguration provider,
        Agentstration.Management.Abstractions.VersionedExtensionOptions source,
        string targetVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await CreateClient(provider).MigrateOptionsAsync(new(
                source.OptionSet,
                source.Version,
                source.SchemaDigest,
                targetVersion,
                source.Values.Clone()), cancellationToken);
            return new Agentstration.Management.Abstractions.VersionedExtensionOptions
            {
                OptionSet = response.Options.OptionSet,
                Version = response.Options.Version,
                SchemaDigest = response.Options.SchemaDigest,
                Values = response.Options.Values.Clone()
            };
        }
        catch (AepProtocolException exception)
        {
            throw new ExtensionOptionMigrationException(exception.Code, exception.Message, exception);
        }
    }

    private AepClient CreateClient(ModelProviderConfiguration provider)
        => CreateClient(provider.Endpoint);

    private AepClient CreateClient(Uri endpoint)
    {
        var client = httpClients.CreateClient("agentstration-aep");
        client.BaseAddress = endpoint;
        return new AepClient(client);
    }

    private static void ValidateNativeOptions(
        string providerType,
        Agentstration.Management.Abstractions.VersionedExtensionOptions options,
        Agentstration.Aep.Abstractions.AepConfigurationCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(options.OptionSet)
            || string.IsNullOrWhiteSpace(options.Version)
            || string.IsNullOrWhiteSpace(options.SchemaDigest))
            throw new ModelProviderConfigurationException(
                $"Provider options for '{providerType}' use the legacy unversioned shape and must be migrated before execution.");
        var optionSet = catalog.OptionSets.SingleOrDefault(value =>
            string.Equals(value.Id, options.OptionSet, StringComparison.Ordinal)
            && string.Equals(value.ContributionKind, Agentstration.Aep.Abstractions.AepContributionKinds.ModelProvider, StringComparison.Ordinal)
            && string.Equals(value.ContributionId, providerType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.Scope, Agentstration.Aep.Abstractions.AepOptionScopes.ModelProfile, StringComparison.Ordinal));
        if (optionSet is null)
            throw new ModelProviderConfigurationException($"Option set '{options.OptionSet}' is not supported by provider '{providerType}'.");
        var version = optionSet.Versions.SingleOrDefault(value => string.Equals(value.Version, options.Version, StringComparison.Ordinal));
        if (version is null)
            throw new ModelProviderConfigurationException($"Option set '{options.OptionSet}' version '{options.Version}' is not supported by provider '{providerType}'.");
        if (!string.Equals(version.SchemaDigest, options.SchemaDigest, StringComparison.Ordinal))
            throw new ModelProviderConfigurationException($"Option set '{options.OptionSet}' version '{options.Version}' has an unexpected schema digest.");
        if (!string.Equals(version.SchemaDigest, Agentstration.Aep.Abstractions.AepSchemaDigest.Compute(version.Schema), StringComparison.Ordinal))
            throw new ModelProviderConfigurationException($"Extension schema '{options.OptionSet}' version '{options.Version}' does not match its declared digest.");
        var issues = ExtensionOptionSchemaValidator.Validate(options.Values, version.Schema);
        if (issues.Count > 0)
            throw new ModelProviderConfigurationException(string.Join(" ", issues.Select(value => value.Message)));
    }

    private static Agentstration.Aep.Abstractions.AepVersionedOptions Map(
        Agentstration.Management.Abstractions.VersionedExtensionOptions value) =>
        new(value.OptionSet, value.Version, value.SchemaDigest, value.Values.Clone());

    private static ExtensionOptionSet Map(Agentstration.Aep.Abstractions.AepOptionSetDescriptor value) => new(
        value.Id,
        value.ContributionKind,
        value.ContributionId,
        value.Scope,
        value.PreferredVersion,
        value.Versions.Select(version => new ExtensionOptionSetVersion(
            version.Version,
            version.SchemaDigest,
            version.Schema.Clone(),
            version.Deprecated)).ToArray(),
        (value.Migrations ?? []).Select(migration => new ExtensionOptionMigration(
            migration.FromVersion,
            migration.ToVersion)).ToArray());

    private static AgentRuntimeCapabilities Map(Agentstration.Aep.Abstractions.AepModelProviderCapabilities value) => new()
    {
        Streaming = new(value.Streaming ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
        Tools = new(value.Tools ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
        StructuredOutput = new(value.StructuredOutput ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
        Reasoning = new ReasoningCapability { Support = value.Thinking ? CapabilitySupport.Native : CapabilitySupport.Unsupported }
    };

    private static AgentRuntimeCapabilities Map(IReadOnlyList<string> values)
    {
        bool Has(string name) => values.Contains(name, StringComparer.OrdinalIgnoreCase);
        FeatureCapability Feature(string name) => new(Has(name) ? CapabilitySupport.Native : CapabilitySupport.Unsupported);
        return new AgentRuntimeCapabilities
        {
            Streaming = Feature("streaming"),
            Tools = Feature("tools"),
            StructuredOutput = Feature("structuredOutput"),
            Reasoning = new ReasoningCapability { Support = Has("reasoning") || Has("thinking") ? CapabilitySupport.Native : CapabilitySupport.Unsupported }
        };
    }
}
