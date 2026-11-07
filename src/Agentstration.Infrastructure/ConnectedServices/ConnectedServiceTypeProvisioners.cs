using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.ConnectedServices;

public sealed class ConnectedServicePlatformResourceProvisioner(
    ConnectedServiceTypeManagementService types,
    IResourceGovernanceContext governance)
{
    public const string HttpTypeName = "http-endpoint";
    public const string HttpTypeVersion = "1.0.0";
    private static readonly JsonElement HttpSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false
    });

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        using var governanceScope = governance.Push(
            ResourceGovernance.Core("connected-services.http", HttpTypeVersion));
        _ = await types.SynchronizeManagedAsync(new ConnectedServiceTypeResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            Metadata = new()
            {
                Name = HttpTypeName,
                Annotations = new Dictionary<string, string>
                {
                    [ResourceProvenanceAnnotations.BuiltIn] = "true"
                }
            },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new()
            {
                DisplayName = "HTTP endpoint",
                Description = "A remote HTTP system identified by an exact base URL.",
                PreferredVersion = HttpTypeVersion,
                Versions =
                [
                    new(
                        HttpTypeVersion,
                        AepSchemaDigest.Compute(HttpSchema),
                        HttpSchema,
                        [new("baseUrl", true, ConnectedServiceValueType.Text,
                            ConnectedServiceValueProtection.Standard, "uri",
                            "The absolute base URL of the remote HTTP system.")],
                        [new("baseUrl", ConnectedServiceValueType.Text,
                            ConnectedServiceValueProtection.Standard, "uri",
                            "The absolute base URL of the remote HTTP system.", "baseUrl")])
                ]
            }
        }, cancellationToken);
    }
}

public sealed class AepEnrollmentConnectedServiceTypeProvisioner(
    ConnectedServiceTypeManagementService types,
    IResourceGovernanceContext governance) : IAepEnrollmentResourceProvisioner
{
    public async Task ProvisionAsync(
        ExtensionRegistrationResource registration,
        AepManifest? authenticatedManifest,
        CancellationToken cancellationToken)
    {
        if (authenticatedManifest?.Contributions.ConnectedServiceTypes is not { Count: > 0 } contributions)
            return;
        var scopeRef = registration.ScopeRef
            ?? throw new ConnectedServiceTypeValidationException(
                "The Extension Registration has no ownership scope.");
        var installationReference = $"{scopeRef.Value}/{registration.Kind}/{registration.Namespace}/{registration.Name}";
        using var governanceScope = governance.Push(ResourceGovernance.Extension(
            installationReference,
            authenticatedManifest.Extension.Id,
            authenticatedManifest.Extension.Version));
        var @namespace = ResourceNamespace.Parse(authenticatedManifest.Extension.Id);
        foreach (var contribution in contributions)
        {
            _ = await types.SynchronizeManagedAsync(new ConnectedServiceTypeResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
                Metadata = new() { Name = contribution.Id, Namespace = @namespace },
                ScopeRef = scopeRef,
                Definition = new()
                {
                    DisplayName = contribution.DisplayName,
                    Description = contribution.Description,
                    PreferredVersion = contribution.PreferredVersion,
                    Versions = contribution.Versions.Select(Map).ToArray()
                }
            }, cancellationToken);
        }
    }

    private static ConnectedServiceTypeVersionDescriptor Map(
        AepConnectedServiceTypeVersionDescriptor version) => new(
        version.Version,
        version.SchemaDigest,
        version.Schema.Clone(),
        version.Inputs.Select(input => new ConnectedServiceInputDescriptor(
            input.Id,
            input.Required,
            Map(input.Type),
            Map(input.Protection),
            input.Format,
            input.Description)).ToArray(),
        version.Exports.Select(export => new ConnectedServiceExportDescriptor(
            export.Id,
            Map(export.Type),
            Map(export.Protection),
            export.Format,
            export.Description)).ToArray(),
        version.Deprecated);

    private static ConnectedServiceValueType Map(AepValueType type) => type switch
    {
        AepValueType.Text => ConnectedServiceValueType.Text,
        AepValueType.WholeNumber => ConnectedServiceValueType.WholeNumber,
        AepValueType.DecimalNumber => ConnectedServiceValueType.DecimalNumber,
        AepValueType.Logical => ConnectedServiceValueType.Logical,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static ConnectedServiceValueProtection Map(AepValueProtection protection) => protection switch
    {
        AepValueProtection.Standard => ConnectedServiceValueProtection.Standard,
        AepValueProtection.Secured => ConnectedServiceValueProtection.Secured,
        _ => throw new ArgumentOutOfRangeException(nameof(protection))
    };
}
