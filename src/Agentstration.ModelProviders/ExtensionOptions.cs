using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Management.Abstractions;
using Microsoft.Extensions.Configuration;

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

public sealed record ExtensionContribution(string Kind, string Id);

public sealed record ExtensionEndpointRegistration(string Id, Uri Endpoint, string Source);

public interface IExtensionEndpointSource
{
    IReadOnlyList<ExtensionEndpointRegistration> List();
}

public sealed class ConfigurationExtensionEndpointSource(IConfiguration configuration) : IExtensionEndpointSource
{
    public IReadOnlyList<ExtensionEndpointRegistration> List() =>
        configuration.GetSection("Agentstration:Extensions")
            .GetChildren()
            .Select(section => new { section.Key, Endpoint = section["Endpoint"] })
            .Where(value => Uri.TryCreate(value.Endpoint, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme is "http" or "https")
            .Select(value => new ExtensionEndpointRegistration(
                value.Key,
                Normalize(new Uri(value.Endpoint!, UriKind.Absolute)),
                "configuration"))
            .DistinctBy(value => value.Endpoint.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static Uri Normalize(Uri endpoint) =>
        new(endpoint.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
}

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
