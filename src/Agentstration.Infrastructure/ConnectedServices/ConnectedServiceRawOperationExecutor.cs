using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;

namespace Agentstration.Infrastructure.ConnectedServices;

internal sealed class ConnectedServiceRawOperationExecutor(
    IResourceStore store, ConnectedServiceOperationService operations,
    ConnectedServiceManagementService services, ConnectedServiceOperationTypeManagementService operationTypes,
    ConnectedServiceBoundValues boundValues, ISecretResolver secrets, ICurrentRequestContext current,
    IHttpClientFactory clients, BuiltinHttpOperationExecutor httpOperations, IRequestContextScopeFactory requestScopes)
    : ISourceOperationSampleExecutor
{
    public async Task<JsonElement> ExecuteAsync(Agentstration.DataSources.Contracts.DataSourceOperationAcquisitionBinding binding,
        ResourceScopeRef consumer, string correlationId, CancellationToken cancellationToken)
    {
        var (operation, operationContract) = await operations.ResolveAsync(binding.Operation, consumer, true, cancellationToken);
        if (operationContract.Acquisition is not { } acquisitionAdapter
            || !operation.Definition.Definition.TryGetProperty(acquisitionAdapter.ReadOnlyProperty, out var readOnly)
            || readOnly.ValueKind != JsonValueKind.True) throw Error("source_sample_operation_not_read_only");
        DataSourceDatasetCompositionService.ValidateValue(operation.Definition.Definition.GetProperty(acquisitionAdapter.InputSchemaProperty), binding.Parameters);
        var context = current.Current;
        var invocation = new InternalMcpToolInvocation(context.TenantId, new(context.WorkspaceId), context.PrincipalId,
            correlationId, correlationId, JsonSerializer.SerializeToElement(new { }), ToolDefinitionCallerKind.Flow);
        var service = await services.AuthorizeAsync(operation.Definition.Service.Service,
            new(consumer, operation.Address), cancellationToken) ?? throw Error("connected_service_unavailable");
        var typeReference = service.Definition.Type;
        ConnectedServiceTypeResource type;
        ConnectedServiceOperationTypeResource operationType;
        ConnectedServiceOperationTypeVersionDescriptor operationVersion;
        using (requestScopes.PushSystem())
        {
            type = (await store.GetExactAsync<ConnectedServiceTypeResource>(new(typeReference.Type.ScopeRef!.Value,
                typeReference.Type.Namespace ?? service.Namespace, ConnectedServiceResourceKinds.ConnectedServiceType,
                typeReference.Type.Name), cancellationToken))!.Value;
            (operationType, operationVersion) = await operationTypes.ResolveExactAsync(operation.Definition.Type,
                operation.Namespace, operation.ScopeRef!.Value, cancellationToken);
        }
        var version = type.Definition.Versions.Single(item => item.Version == typeReference.Version);
        if (BuiltinHttpOperation.Owns(type, version)
            && BuiltinHttpOperation.IsCore(operationType, BuiltinHttpOperation.TypeName)
            && operationVersion.Version == BuiltinHttpOperation.Version
            && operationVersion.Invocation.ProviderOperationId == BuiltinHttpOperation.ProviderOperationId)
        {
            var result = await httpOperations.AcquireAsync(service, operation, binding.Parameters,
                new(consumer, operation.Address), cancellationToken);
            return result;
        }
        var manager = type.Governance?.ManagedBy;
        if (manager?.Kind != ResourceManagementAuthorityKind.ExtensionInstallation
            || operationType.Governance?.ManagedBy != manager
            || operationType.Governance?.Origin != type.Governance?.Origin)
            throw Error("connected_service_acquisition_provider_required");
        var registrations = await store.ListExactAsync<ExtensionRegistrationResource>(type.ScopeRef!.Value,
            ExtensionKinds.ExtensionRegistration, 0, 1000, cancellationToken);
        var registration = registrations.SingleOrDefault(item => manager.Reference ==
            $"{item.Value.ScopeRef!.Value.Value}/{item.Value.Kind}/{item.Value.Namespace}/{item.Value.Name}")?.Value
            ?? throw Error("connected_service_acquisition_provider_missing");
        if (!registration.Definition.Enabled || registration.Definition.ExpectedExtensionId != type.Governance!.Origin.Reference)
            throw Error("connected_service_acquisition_provider_unavailable");
        using var http = clients.CreateClient("agentstration-aep");
        http.BaseAddress = registration.Definition.Endpoint;
        var client = new AepClient(http, AepExtensionCredentials.Create(registration, secrets),
            expectedExtensionId: registration.Definition.ExpectedExtensionId);
        var manifest = await client.DiscoverAsync(cancellationToken);
        if (!manifest.Capabilities.TryGetValue(AepConnectedServiceAcquisitionProtocol.Capability, out var capability)
            || capability.Version != AepConnectedServiceAcquisitionProtocol.Version
            || !manifest.Capabilities.TryGetValue(AepCapabilityNames.BoundValues, out var valuesCapability)
            || valuesCapability.Version != AepProtocol.BoundValuesCapabilityVersion)
            throw Error("connected_service_acquisition_provider_unsupported");
        var advertisedService = manifest.Contributions.ConnectedServiceTypes?.SingleOrDefault(item => item.Id == type.Name)?
            .Versions.SingleOrDefault(item => item.Version == version.Version);
        var advertised = manifest.Contributions.ConnectedServiceOperationTypes?.SingleOrDefault(item => item.Id == operationType.Name)?
            .Versions.SingleOrDefault(item => item.Version == operationVersion.Version);
        var adapter = operationVersion.Acquisition!;
        if (advertisedService?.SchemaDigest != version.SchemaDigest || advertised?.SchemaDigest != operationVersion.SchemaDigest
            || advertised.Acquisition is not { } declared || declared.FlowName != adapter.Flow.Flow.Name
            || declared.FlowVersion != adapter.Flow.Version || declared.InputSchemaProperty != adapter.InputSchemaProperty
            || declared.OutputSchemaProperty != adapter.OutputSchemaProperty || declared.ReadOnlyProperty != adapter.ReadOnlyProperty
            || advertised.Invocation.ProviderOperationId != operationVersion.Invocation.ProviderOperationId
            || advertised.Invocation.ContractVersion != operationVersion.Invocation.ContractVersion)
            throw Error("connected_service_acquisition_provider_changed");
        return (await boundValues.ExecuteAsync(service, version, registration, manifest, invocation, async values =>
        {
            var result = await client.AcquireConnectedServiceOperationAsync(new(type.Name, version.Version,
                service.Definition.Options, values, operationType.Name, operationVersion.Version,
                operation.Definition.Definition, binding.Parameters, correlationId), cancellationToken);
            return result.Result.Clone();
        }, cancellationToken))!.Value;
    }
    private static DataSourceAcquisitionException Error(string code) => new(code, "Connected Service acquisition could not be completed.");
}
