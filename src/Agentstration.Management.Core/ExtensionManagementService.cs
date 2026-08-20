using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;

namespace Agentstration.Management.Core;

public sealed record ExtensionOptionUsage(
    string ProfileName,
    string ProfileNamespace,
    string OptionSet,
    string Version,
    string SchemaDigest,
    string Status,
    IReadOnlyList<string> Issues);

public sealed record ExtensionView(
    string ProviderName,
    string ProviderNamespace,
    Uri Endpoint,
    string Status,
    ExtensionIdentity? Extension,
    IReadOnlyList<ExtensionContribution> Contributions,
    IReadOnlyList<ExtensionOptionSet> OptionSets,
    IReadOnlyList<ExtensionOptionUsage> Usages,
    string? Details,
    bool Configured,
    string DiscoverySource);

public sealed class ExtensionManagementService(
    IModelProviderConfigurationStore providers,
    ModelProfileManagementService profiles,
    IEnumerable<IExtensionInspector> inspectors,
    IEnumerable<IExtensionEndpointSource> endpointSources)
{
    public async Task<IReadOnlyList<ExtensionView>> ListAsync(CancellationToken cancellationToken)
    {
        var configurations = await providers.ListAsync(cancellationToken);
        var profileResources = await profiles.ListAsync(cancellationToken);
        var views = new List<ExtensionView>();
        foreach (var provider in configurations.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            var inspector = inspectors.SingleOrDefault(value => value.CanHandle(provider.ProviderType));
            var inspection = inspector is null
                ? new ExtensionInspection(provider.Name, provider.Endpoint, "unknown", null, [], [], "No extension inspector is registered.")
                : await inspector.InspectAsync(provider, cancellationToken);
            var usages = profileResources
                .Where(value => References(value.Value, provider))
                .SelectMany(value => InspectUsage(value.Value, provider, inspection.Status, inspection.OptionSets))
                .ToArray();
            var status = string.Equals(inspection.Status, "available", StringComparison.Ordinal)
                && usages.Any(value => string.Equals(value.Status, "incompatible", StringComparison.Ordinal))
                ? "incompatible"
                : inspection.Status;
            views.Add(new ExtensionView(
                provider.Name,
                provider.Namespace.Value,
                provider.Endpoint,
                status,
                inspection.Extension,
                inspection.Contributions,
                inspection.OptionSets,
                usages,
                inspection.Details,
                true,
                "model-provider"));
        }

        var configuredEndpoints = configurations
            .Select(value => Normalize(value.Endpoint))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registrations = endpointSources
            .SelectMany(value => value.List())
            .Where(value => !configuredEndpoints.Contains(Normalize(value.Endpoint)))
            .DistinctBy(value => Normalize(value.Endpoint), StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.Id, StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var inspector = inspectors.SingleOrDefault(value => value.CanInspectEndpoint(registration.Endpoint));
            var inspection = inspector is null
                ? new ExtensionInspection(registration.Id, registration.Endpoint, "unknown", null, [], [], "No extension inspector is registered.")
                : await inspector.InspectAsync(registration.Id, registration.Endpoint, cancellationToken);
            views.Add(new ExtensionView(
                registration.Id,
                string.Empty,
                registration.Endpoint,
                inspection.Status,
                inspection.Extension,
                inspection.Contributions,
                inspection.OptionSets,
                [],
                inspection.Details,
                false,
                registration.Source));
        }
        return views;
    }

    private static string Normalize(Uri endpoint) => endpoint.AbsoluteUri.TrimEnd('/');

    private static bool References(ModelProfileResource profile, ModelProviderConfiguration provider)
    {
        var address = profile.Definition.Provider.Resolve(profile.Namespace, ResourceKinds.ModelProvider);
        return address.Namespace == provider.Namespace
            && string.Equals(address.Name, provider.Name, StringComparison.Ordinal);
    }

    private static IEnumerable<ExtensionOptionUsage> InspectUsage(
        ModelProfileResource profile,
        ModelProviderConfiguration provider,
        string inspectionStatus,
        IReadOnlyList<ExtensionOptionSet> optionSets)
    {
        if (!profile.Definition.ProviderOptions.TryGetValue(provider.ProviderType, out var options)) yield break;
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(options.OptionSet)
            || string.IsNullOrWhiteSpace(options.Version)
            || string.IsNullOrWhiteSpace(options.SchemaDigest))
        {
            issues.Add("These provider options use the legacy unversioned shape and must be migrated explicitly.");
            yield return new ExtensionOptionUsage(
                profile.Name,
                profile.Namespace.Value,
                options.OptionSet,
                options.Version,
                options.SchemaDigest,
                "incompatible",
                issues);
            yield break;
        }
        if (!string.Equals(inspectionStatus, "available", StringComparison.Ordinal))
        {
            yield return new ExtensionOptionUsage(
                profile.Name,
                profile.Namespace.Value,
                options.OptionSet,
                options.Version,
                options.SchemaDigest,
                "unverified",
                ["The option contract cannot be verified while the extension is unavailable."]);
            yield break;
        }
        var optionSet = optionSets.SingleOrDefault(value =>
            string.Equals(value.Id, options.OptionSet, StringComparison.Ordinal)
            && string.Equals(value.ContributionId, provider.ProviderType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.Scope, ExtensionOptionScopes.ModelProfile, StringComparison.Ordinal));
        var version = optionSet?.Versions.SingleOrDefault(value => string.Equals(value.Version, options.Version, StringComparison.Ordinal));
        if (optionSet is null) issues.Add($"Option set '{options.OptionSet}' is no longer supported.");
        else if (version is null) issues.Add($"Version '{options.Version}' is no longer supported.");
        else
        {
            if (!string.Equals(version.SchemaDigest, options.SchemaDigest, StringComparison.Ordinal))
                issues.Add("The persisted schema digest does not match the extension contract.");
            issues.AddRange(ExtensionOptionSchemaValidator.Validate(options.Values, version.Schema).Select(value => value.Message));
        }
        yield return new ExtensionOptionUsage(
            profile.Name,
            profile.Namespace.Value,
            options.OptionSet,
            options.Version,
            options.SchemaDigest,
            issues.Count == 0 ? "supported" : "incompatible",
            issues);
    }
}
