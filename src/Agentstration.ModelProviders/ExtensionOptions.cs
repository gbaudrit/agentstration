using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Management.Abstractions;

namespace Agentstration.ModelProviders;

public static class ExtensionOptionScopes
{
    public const string ModelProfile = "model-profile";
}

public sealed record ExtensionOptionSetVersion(
    string Version,
    string SchemaDigest,
    JsonElement Schema,
    bool Deprecated);

public sealed record ExtensionOptionSet(
    string Id,
    string ContributionKind,
    string ContributionId,
    string Scope,
    string PreferredVersion,
    IReadOnlyList<ExtensionOptionSetVersion> Versions,
    IReadOnlyList<ExtensionOptionMigration>? Migrations = null);

public sealed record ExtensionOptionMigration(string FromVersion, string ToVersion);

public sealed class ExtensionOptionMigrationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public interface IExtensionOptionsMigrator
{
    bool CanHandle(string providerType);
    ValueTask<VersionedExtensionOptions> MigrateAsync(
        ModelProviderConfiguration provider,
        VersionedExtensionOptions source,
        string targetVersion,
        CancellationToken cancellationToken = default);
}

public sealed record ExtensionIdentity(string Id, string Name, string Version, string? Description);

public sealed record ExtensionContribution(string Kind, string Id);

public sealed record ExtensionInspection(
    string ProviderName,
    Uri Endpoint,
    string Status,
    ExtensionIdentity? Extension,
    IReadOnlyList<ExtensionContribution> Contributions,
    IReadOnlyList<ExtensionOptionSet> OptionSets,
    string? Details = null);

public interface IExtensionInspector
{
    bool CanHandle(string providerType);
    bool CanInspectEndpoint(Uri endpoint);
    ValueTask<ExtensionInspection> InspectAsync(
        ModelProviderConfiguration provider,
        CancellationToken cancellationToken = default);
    ValueTask<ExtensionInspection> InspectAsync(
        string registrationName,
        Uri endpoint,
        CancellationToken cancellationToken = default);
}

public sealed record ExtensionOptionValidationIssue(string Path, string Code, string Message);

public static class ExtensionOptionSchemaValidator
{
    public static IReadOnlyList<ExtensionOptionValidationIssue> Validate(
        JsonElement value,
        JsonElement schema,
        string path = "values") =>
        AepOptionSchemaValidator.Validate(value, schema, path)
            .Select(issue => new ExtensionOptionValidationIssue(issue.Path, issue.Code, issue.Message))
            .ToArray();
}

public static class ExtensionOptionSchemaDigest
{
    public static string Compute(JsonElement schema) => AepSchemaDigest.Compute(schema);
}
