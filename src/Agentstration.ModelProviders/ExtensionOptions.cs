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
    IReadOnlyList<ExtensionOptionSetVersion> Versions);

public sealed record ExtensionIdentity(string Id, string Name, string Version, string? Description);

public sealed record ExtensionInspection(
    string ProviderName,
    Uri Endpoint,
    string Status,
    ExtensionIdentity? Extension,
    IReadOnlyList<string> Contributions,
    IReadOnlyList<ExtensionOptionSet> OptionSets,
    string? Details = null);

public interface IExtensionInspector
{
    bool CanHandle(string providerType);
    ValueTask<ExtensionInspection> InspectAsync(
        ModelProviderConfiguration provider,
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
