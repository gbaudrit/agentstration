using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class ModelProfileOptionMigrationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record ModelProfileOptionMigrationPreview(
    string ProfileName,
    string ProfileNamespace,
    string ProviderType,
    VersionedExtensionOptions Source,
    VersionedExtensionOptions Target,
    string ProfileETag);

public sealed class ModelProfileOptionMigrationService(
    ModelProfileManagementService profiles,
    ModelProviderManagementService providers,
    IEnumerable<IExtensionOptionsMigrator> migrators,
    IEnumerable<IExtensionInspector> inspectors)
{
    public async Task<ModelProfileOptionMigrationPreview> PreviewAsync(
        ResourceNamespace @namespace,
        string profileName,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        var profile = await profiles.GetAsync(@namespace, profileName, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, profileName, @namespace));
        var providerAddress = profile.Value.Definition.Provider.Resolve(profile.Value.Namespace, ResourceKinds.ModelProvider);
        var provider = await providers.GetConfigurationRequiredAsync(providerAddress.Namespace, providerAddress.Name, cancellationToken);
        if (!profile.Value.Definition.ProviderOptions.TryGetValue(provider.ProviderType, out var source))
            throw Invalid("option_source_missing", $"Model profile '{profile.Value.Address}' has no native options for provider '{provider.ProviderType}'.");
        if (string.IsNullOrWhiteSpace(source.OptionSet)
            || string.IsNullOrWhiteSpace(source.Version)
            || string.IsNullOrWhiteSpace(source.SchemaDigest))
            throw Invalid("legacy_options_unsupported", "Legacy unversioned options cannot be migrated without an explicit source contract.");
        if (string.Equals(source.Version, targetVersion, StringComparison.Ordinal))
            throw Invalid("option_migration_not_required", $"The profile already uses option version '{targetVersion}'.");
        var inspector = inspectors.SingleOrDefault(value => value.CanHandle(provider.ProviderType))
            ?? throw Invalid("extension_unavailable", $"No extension inspector supports provider '{provider.ProviderType}'.");
        var inspection = await inspector.InspectAsync(provider, cancellationToken);
        if (!string.Equals(inspection.Status, "available", StringComparison.Ordinal))
            throw Invalid("extension_unavailable", inspection.Details ?? "The extension is unavailable.");
        var optionSet = inspection.OptionSets.SingleOrDefault(value =>
            string.Equals(value.Id, source.OptionSet, StringComparison.Ordinal)
            && string.Equals(value.ContributionId, provider.ProviderType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.Scope, ExtensionOptionScopes.ModelProfile, StringComparison.Ordinal))
            ?? throw Invalid("option_set_unsupported", $"Option set '{source.OptionSet}' is not supported by provider '{provider.ProviderType}'.");
        Validate(source, optionSet, source.Version, "source");
        _ = optionSet.Versions.SingleOrDefault(value => string.Equals(value.Version, targetVersion, StringComparison.Ordinal))
            ?? throw Invalid("option_version_unsupported", $"Target version '{targetVersion}' is not supported.");
        if (!HasMigrationPath(optionSet, source.Version, targetVersion))
            throw Invalid("option_migration_unsupported", $"No migration path exists from '{source.Version}' to '{targetVersion}'.");
        var migrator = migrators.SingleOrDefault(value => value.CanHandle(provider.ProviderType))
            ?? throw Invalid("option_migration_unsupported", $"No option migrator supports provider '{provider.ProviderType}'.");
        VersionedExtensionOptions target;
        try { target = await migrator.MigrateAsync(provider, source, targetVersion, cancellationToken); }
        catch (ExtensionOptionMigrationException exception)
        {
            throw Invalid(exception.Code, exception.Message, exception);
        }
        if (!string.Equals(target.OptionSet, source.OptionSet, StringComparison.Ordinal))
            throw Invalid("option_migration_invalid", "The extension changed the option-set identity during migration.");
        Validate(target, optionSet, targetVersion, "target");
        return new(profileName, @namespace.Value, provider.ProviderType, source, target, profile.ETag);
    }

    public async Task<StoredResource<ModelProfileResource>> ApplyAsync(
        ResourceNamespace @namespace,
        string profileName,
        string targetVersion,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            throw new ControlPlaneConcurrencyException("Applying an option migration requires If-Match.");
        var preview = await PreviewAsync(@namespace, profileName, targetVersion, cancellationToken);
        if (!string.Equals(ifMatch, preview.ProfileETag, StringComparison.Ordinal))
            throw new ControlPlaneConcurrencyException($"Model profile '{@namespace}/{profileName}' was modified before the migration could be applied.");
        var profile = await profiles.GetAsync(@namespace, profileName, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ModelProfile, profileName, @namespace));
        var options = new Dictionary<string, VersionedExtensionOptions>(profile.Value.Definition.ProviderOptions, StringComparer.Ordinal)
        {
            [preview.ProviderType] = preview.Target
        };
        return await profiles.PutAsync(
            @namespace,
            profileName,
            profile.Value.Definition with { ProviderOptions = options },
            ifMatch,
            cancellationToken);
    }

    private static void Validate(
        VersionedExtensionOptions options,
        ExtensionOptionSet optionSet,
        string expectedVersion,
        string role)
    {
        if (!string.Equals(options.Version, expectedVersion, StringComparison.Ordinal))
            throw Invalid("option_migration_invalid", $"The migration {role} version does not match '{expectedVersion}'.");
        var version = optionSet.Versions.SingleOrDefault(value => string.Equals(value.Version, expectedVersion, StringComparison.Ordinal))
            ?? throw Invalid("option_version_unsupported", $"Version '{expectedVersion}' is not supported.");
        if (!string.Equals(version.SchemaDigest, ExtensionOptionSchemaDigest.Compute(version.Schema), StringComparison.Ordinal))
            throw Invalid("option_schema_mismatch", $"The extension schema for version '{expectedVersion}' does not match its digest.");
        if (!string.Equals(options.SchemaDigest, version.SchemaDigest, StringComparison.Ordinal))
            throw Invalid("option_schema_mismatch", $"The migration {role} digest does not match version '{expectedVersion}'.");
        var issues = ExtensionOptionSchemaValidator.Validate(options.Values, version.Schema);
        if (issues.Count > 0)
            throw Invalid("invalid_options", string.Join(" ", issues.Select(value => value.Message)));
    }

    private static bool HasMigrationPath(ExtensionOptionSet optionSet, string sourceVersion, string targetVersion)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceVersion };
        pending.Enqueue(sourceVersion);
        while (pending.TryDequeue(out var current))
        {
            foreach (var migration in (optionSet.Migrations ?? []).Where(value => string.Equals(value.FromVersion, current, StringComparison.Ordinal)))
            {
                if (string.Equals(migration.ToVersion, targetVersion, StringComparison.Ordinal)) return true;
                if (visited.Add(migration.ToVersion)) pending.Enqueue(migration.ToVersion);
            }
        }
        return false;
    }

    private static ModelProfileOptionMigrationException Invalid(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);
}
