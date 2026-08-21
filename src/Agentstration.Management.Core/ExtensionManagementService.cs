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

public sealed record ExtensionProviderBinding(
    string Name,
    string Namespace,
    string ContributionId);

public sealed record ExtensionView(
    string RegistrationName,
    string RegistrationNamespace,
    Uri Endpoint,
    string Status,
    ExtensionIdentity? Extension,
    IReadOnlyList<ExtensionContribution> Contributions,
    IReadOnlyList<ExtensionOptionSet> OptionSets,
    IReadOnlyList<ExtensionOptionUsage> Usages,
    IReadOnlyList<ExtensionProviderBinding> Providers,
    string? Details,
    string DiscoverySource);

public sealed class ExtensionManagementService(
    IModelProviderConfigurationStore providers,
    ModelProfileManagementService profiles,
    IEnumerable<IExtensionInspector> inspectors,
    ExtensionRegistrationManagementService registrations)
{
    public async Task<IReadOnlyList<ExtensionView>> ListAsync(CancellationToken cancellationToken)
    {
        var configurations = await providers.ListAsync(cancellationToken);
        var profileResources = await profiles.ListAsync(cancellationToken);
        var views = new List<ExtensionView>();
        foreach (var registration in (await registrations.ListAsync(cancellationToken))
            .OrderBy(value => value.Value.Name, StringComparer.Ordinal))
        {
            var resource = registration.Value;
            var endpoint = resource.Definition.Endpoint;
            var bindings = configurations.Where(provider => References(provider, resource)).ToArray();
            var inspector = inspectors.SingleOrDefault(value => value.CanInspectEndpoint(endpoint));
            var inspection = inspector is null
                ? new ExtensionInspection(resource.Name, endpoint, "unknown", null, [], [], "No extension inspector is registered.")
                : resource.Definition.Enabled
                    ? await inspector.InspectAsync(resource.Name, endpoint, cancellationToken)
                    : new ExtensionInspection(resource.Name, endpoint, "disabled", null, [], [], "The extension registration is disabled.");
            if (inspection.Extension is not null
                && resource.Definition.ExpectedExtensionId is { Length: > 0 } expectedId
                && !string.Equals(inspection.Extension.Id, expectedId, StringComparison.Ordinal))
            {
                inspection = inspection with
                {
                    Status = "incompatible",
                    Details = $"Expected extension '{expectedId}', but endpoint reports '{inspection.Extension.Id}'."
                };
            }
            var usages = bindings.SelectMany(provider => profileResources
                .Where(value => References(value.Value, provider))
                .SelectMany(value => InspectUsage(value.Value, provider, inspection.Status, inspection.OptionSets)))
                .ToArray();
            var status = string.Equals(inspection.Status, "available", StringComparison.Ordinal)
                && usages.Any(value => string.Equals(value.Status, "incompatible", StringComparison.Ordinal))
                ? "incompatible"
                : inspection.Status;
            views.Add(new ExtensionView(
                resource.Name,
                resource.Namespace.Value,
                endpoint,
                status,
                inspection.Extension,
                inspection.Contributions,
                inspection.OptionSets,
                usages,
                bindings.Select(value => new ExtensionProviderBinding(value.Name, value.Namespace.Value, value.ContributionId)).ToArray(),
                inspection.Details,
                resource.Definition.Source.ToString().ToLowerInvariant()));
        }
        return views;
    }

    private static bool References(ModelProviderConfiguration provider, ExtensionRegistrationResource registration)
    {
        var address = provider.Extension.Resolve(provider.Namespace, ResourceKinds.ExtensionRegistration);
        return address.Namespace == registration.Namespace
            && string.Equals(address.Name, registration.Name, StringComparison.Ordinal);
    }

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
        if (!profile.Definition.ProviderOptions.TryGetValue(provider.ContributionId, out var options)) yield break;
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
            && string.Equals(value.ContributionId, provider.ContributionId, StringComparison.OrdinalIgnoreCase)
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
