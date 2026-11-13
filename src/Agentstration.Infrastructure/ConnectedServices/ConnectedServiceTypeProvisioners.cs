using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;
using Agentstration.ResourceManagement;

namespace Agentstration.Infrastructure.ConnectedServices;

public sealed partial class ConnectedServicePlatformResourceProvisioner(
    ConnectedServiceTypeManagementService types,
    ConnectedServiceOperationTypeManagementService operationTypes,
    FlowService flows,
    IResourceStore store,
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
        var discoveryFlow = await EnsureHttpFlowAsync(true, cancellationToken);
        var acquisitionFlow = await EnsureHttpFlowAsync(false, cancellationToken);
        var existingOperation = await operationTypes.GetExactAsync(ResourceScopeRef.Instance,
            ResourceNamespace.Default, BuiltinHttpOperation.TypeName, cancellationToken);
        var existingService = await types.GetExactAsync(ResourceScopeRef.Instance,
            ResourceNamespace.Default, HttpTypeName, cancellationToken);
        var operationType = (await operationTypes.SynchronizeManagedAsync(new ConnectedServiceOperationTypeResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceOperationType,
            Metadata = new() { Name = BuiltinHttpOperation.TypeName },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new()
            {
                DisplayName = "HTTP GET operation",
                PreferredVersion = BuiltinHttpOperation.Version,
                UsePolicy = existingOperation?.Value.Definition.UsePolicy ?? new(),
                Versions = [new(BuiltinHttpOperation.Version, AepSchemaDigest.Compute(BuiltinHttpOperation.DefinitionSchema),
                    BuiltinHttpOperation.DefinitionSchema, ConnectedServiceOperationInteraction.RequestResponse,
                    new(BuiltinHttpOperation.ProviderOperationId, "1.0")) { Acquisition = new(acquisitionFlow) }]
            }
        }, cancellationToken)).Value;
        var operationVersion = operationType.Definition.Versions.Single();
        var discovery = new ConnectedServiceOperationDiscoveryDescriptor
        {
            Flow = discoveryFlow,
            ProducedTypes = [new()
            {
                Type = new(operationType.Name, ResourceScopeRef.Instance, operationType.Namespace),
                TypeUid = operationType.Uid, Version = operationVersion.Version, SchemaDigest = operationVersion.SchemaDigest
            }]
        };
        var httpVersion = new ConnectedServiceTypeVersionDescriptor(
            HttpTypeVersion,
            AepSchemaDigest.Compute(HttpSchema),
            HttpSchema,
            [new("baseUrl", true, ConnectedServiceValueType.Text,
                ConnectedServiceValueProtection.Standard, "uri",
                "The absolute base URL of the remote HTTP system.")],
            [new("baseUrl", ConnectedServiceValueType.Text,
                ConnectedServiceValueProtection.Standard, "uri",
                "The absolute base URL of the remote HTTP system.", "baseUrl")]) { OperationDiscovery = discovery };
        // Maintainer-approved correction of the unreleased 1.0.0 baseline only.
        // Keep identity, policies, references and other observed preview versions intact.
        if (existingService is not null && BuiltinHttpOperation.IsCore(existingService.Value, HttpTypeName)
            && existingService.Value.Definition.Versions.SingleOrDefault(item => item.Version == HttpTypeVersion) is { } previous
            && previous.OperationDiscovery is null
            && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(previous),
                JsonSerializer.SerializeToElement(httpVersion with { OperationDiscovery = null })))
        {
            existingService = await store.PutExactAsync(ResourceScopeRef.Instance, existingService.Value with
            {
                Generation = checked(existingService.Value.Generation + 1),
                Definition = existingService.Value.Definition with
                { Versions = existingService.Value.Definition.Versions.Select(item => item.Version == HttpTypeVersion ? httpVersion : item).ToArray() }
            }, existingService.ETag, false, cancellationToken);
        }
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
                UsePolicy = existingService?.Value.Definition.UsePolicy ?? new(),
                Versions = [httpVersion]
            }
        }, cancellationToken);
    }
}

public sealed class AepEnrollmentConnectedServiceTypeProvisioner(
    ConnectedServiceTypeManagementService types,
    ConnectedServiceOperationTypeManagementService operationTypes,
    FlowService flows,
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
                    Versions = await Task.WhenAll(contribution.Versions.Select(version =>
                        MapAsync(version, scopeRef, @namespace, cancellationToken)))
                }
            }, cancellationToken);
        }
    }

    private async Task<ConnectedServiceTypeVersionDescriptor> MapAsync(
        AepConnectedServiceTypeVersionDescriptor version,
        ResourceScopeRef scopeRef,
        ResourceNamespace extensionNamespace,
        CancellationToken cancellationToken)
    {
        ConnectedServiceOperationDiscoveryDescriptor? discovery = null;
        if (version.OperationDiscovery is { } declared)
        {
            var flowNamespace = string.IsNullOrWhiteSpace(declared.FlowNamespace)
                ? extensionNamespace : ResourceNamespace.Parse(declared.FlowNamespace);
            var flowId = new FlowId(declared.FlowName, flowNamespace);
            var flow = await flows.GetExactAsync(scopeRef, flowId, cancellationToken)
                ?? throw new ConnectedServiceTypeValidationException(
                    $"Discovery Flow '{flowNamespace}/{declared.FlowName}' was not provisioned.");
            var flowVersion = await flows.GetVersionExactAsync(scopeRef, flowId,
                    declared.FlowVersion, cancellationToken)
                ?? throw new ConnectedServiceTypeValidationException(
                    $"Discovery Flow version '{flowNamespace}/{declared.FlowName}:{declared.FlowVersion}' was not provisioned.");
            if (!flowVersion.Value.Metadata.TryGetValue(FlowMetadataKeys.Contract, out var contract)
                || !string.Equals(contract, ConnectedServiceOperationDiscoveryContracts.V1, StringComparison.Ordinal))
                throw new ConnectedServiceTypeValidationException(
                    $"Discovery Flow '{flowNamespace}/{declared.FlowName}:{declared.FlowVersion}' must declare contract '{ConnectedServiceOperationDiscoveryContracts.V1}'.");
            var producedTypes = new List<ConnectedServiceOperationTypeReference>();
            foreach (var reference in declared.ProducedTypes)
            {
                var stored = await operationTypes.GetExactAsync(scopeRef, extensionNamespace,
                    reference.Id, cancellationToken)
                    ?? throw new ConnectedServiceOperationTypeNotFoundException(extensionNamespace, reference.Id);
                var operationVersion = stored.Value.Definition.Versions.SingleOrDefault(candidate =>
                    string.Equals(candidate.Version, reference.Version, StringComparison.Ordinal))
                    ?? throw new ConnectedServiceOperationTypeRevisionException(
                        $"Operation type version '{reference.Id}:{reference.Version}' was not provisioned.");
                producedTypes.Add(new()
                {
                    Type = new(reference.Id, scopeRef, extensionNamespace),
                    TypeUid = stored.Value.Uid,
                    Version = operationVersion.Version,
                    SchemaDigest = operationVersion.SchemaDigest
                });
            }
            discovery = new()
            {
                Flow = new()
                {
                    Flow = new(declared.FlowName, scopeRef, flowNamespace),
                    FlowUid = flow.Value.Uid,
                    Version = flowVersion.Value.Version,
                    DefinitionHash = flowVersion.Value.DefinitionHash
                        ?? throw new ConnectedServiceTypeValidationException("The discovery Flow has no definition hash."),
                    Contract = contract
                },
                ProducedTypes = producedTypes
            };
        }
        return new(
            version.Version,
            version.SchemaDigest,
            version.Schema.Clone(),
            version.Inputs.Select(input => new ConnectedServiceInputDescriptor(
                input.Id, input.Required, Map(input.Type), Map(input.Protection),
                input.Format, input.Description)).ToArray(),
            version.Exports.Select(export => new ConnectedServiceExportDescriptor(
                export.Id, Map(export.Type), Map(export.Protection),
                export.Format, export.Description)).ToArray(),
            version.Deprecated)
        {
            OperationDiscovery = discovery
        };
    }

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

public sealed class AepEnrollmentConnectedServiceOperationTypeProvisioner(
    ConnectedServiceOperationTypeManagementService types,
    FlowService flows,
    IResourceGovernanceContext governance) : IAepEnrollmentResourceProvisioner
{
    public async Task ProvisionAsync(
        ExtensionRegistrationResource registration,
        AepManifest? authenticatedManifest,
        CancellationToken cancellationToken)
    {
        if (authenticatedManifest?.Contributions.ConnectedServiceOperationTypes is not { Count: > 0 } contributions)
            return;
        var scopeRef = registration.ScopeRef
            ?? throw new ConnectedServiceOperationTypeValidationException(
                "The Extension Registration has no ownership scope.");
        var installationReference = $"{scopeRef.Value}/{registration.Kind}/{registration.Namespace}/{registration.Name}";
        using var governanceScope = governance.Push(ResourceGovernance.Extension(
            installationReference, authenticatedManifest.Extension.Id, authenticatedManifest.Extension.Version));
        var @namespace = ResourceNamespace.Parse(authenticatedManifest.Extension.Id);
        foreach (var contribution in contributions)
        {
            _ = await types.SynchronizeManagedAsync(new ConnectedServiceOperationTypeResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ConnectedServiceResourceKinds.ConnectedServiceOperationType,
                Metadata = new() { Name = contribution.Id, Namespace = @namespace },
                ScopeRef = scopeRef,
                Definition = new()
                {
                    DisplayName = contribution.DisplayName,
                    Description = contribution.Description,
                    PreferredVersion = contribution.PreferredVersion,
                    Versions = await Task.WhenAll(contribution.Versions.Select(version =>
                        MapAsync(version, scopeRef, @namespace, cancellationToken)))
                }
            }, cancellationToken);
        }
    }

    private async Task<ConnectedServiceOperationTypeVersionDescriptor> MapAsync(
        AepConnectedServiceOperationTypeVersionDescriptor version, ResourceScopeRef scope,
        ResourceNamespace extensionNamespace, CancellationToken cancellationToken)
    {
        ConnectedServiceOperationAcquisitionDescriptor? acquisition = null;
        if (version.Acquisition is { } declared)
        {
            var id = new FlowId(declared.FlowName, extensionNamespace);
            var flow = await flows.GetExactAsync(scope, id, cancellationToken)
                ?? throw new ConnectedServiceOperationTypeValidationException("The acquisition Flow was not provisioned.");
            var published = await flows.GetVersionExactAsync(scope, id, declared.FlowVersion, cancellationToken)
                ?? throw new ConnectedServiceOperationTypeValidationException("The acquisition Flow version was not published.");
            const string contract = "connected-service-operation-acquisition/v1";
            if (published.Value.Metadata.GetValueOrDefault(FlowMetadataKeys.Contract) != contract
                || published.Value.DefinitionHash is null)
                throw new ConnectedServiceOperationTypeValidationException("The acquisition Flow contract or hash is invalid.");
            acquisition = new(new()
            {
                Flow = new(declared.FlowName, scope, extensionNamespace),
                FlowUid = flow.Value.Uid,
                Version = published.Value.Version,
                DefinitionHash = published.Value.DefinitionHash,
                Contract = contract
            }, declared.InputSchemaProperty, declared.OutputSchemaProperty, declared.ReadOnlyProperty);
        }
        return new(version.Version, version.SchemaDigest, version.DefinitionSchema.Clone(), Map(version.Interaction),
            new(version.Invocation.ProviderOperationId, version.Invocation.ContractVersion), version.Deprecated)
        { Acquisition = acquisition };
    }

    private static ConnectedServiceOperationInteraction Map(AepConnectedServiceOperationInteraction interaction) =>
        interaction switch
        {
            AepConnectedServiceOperationInteraction.RequestResponse => ConnectedServiceOperationInteraction.RequestResponse,
            AepConnectedServiceOperationInteraction.ServerStream => ConnectedServiceOperationInteraction.ServerStream,
            AepConnectedServiceOperationInteraction.ClientStream => ConnectedServiceOperationInteraction.ClientStream,
            AepConnectedServiceOperationInteraction.DuplexStream => ConnectedServiceOperationInteraction.DuplexStream,
            AepConnectedServiceOperationInteraction.Subscription => ConnectedServiceOperationInteraction.Subscription,
            _ => throw new ArgumentOutOfRangeException(nameof(interaction))
        };
}
