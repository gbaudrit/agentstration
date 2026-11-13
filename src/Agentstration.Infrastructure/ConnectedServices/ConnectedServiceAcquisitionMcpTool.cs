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

internal sealed class ConnectedServiceAcquisitionExecutor(
    DataSourceDatasetAcquisitionService acquisitions, IResourceStore store,
    ConnectedServiceManagementService services, ConnectedServiceOperationTypeManagementService operationTypes,
    ConnectedServiceBoundValues boundValues, ISecretResolver secrets, ICurrentRequestContext current,
    IHttpClientFactory clients,
    BuiltinHttpOperationExecutor httpOperations)
{
    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        try { return await ExecuteCoreAsync(invocation, cancellationToken); }
        catch (AepProtocolException) { throw Error("connected_service_provider_acquisition_failed"); }
        catch (HttpRequestException) { throw Error("connected_service_provider_unavailable"); }
    }

    private async Task<JsonElement?> ExecuteCoreAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.CallerKind != ToolDefinitionCallerKind.Flow || string.IsNullOrEmpty(invocation.RunId)
            || current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance
                or ControlPlaneAccessMode.TenantAdministration)) throw Error("connected_service_acquisition_execution_denied");
        RequestContext context;
        try { context = current.Current; }
        catch (InvalidOperationException) { throw Error("connected_service_acquisition_execution_denied"); }
        if (context.PrincipalId == Guid.Empty || context.TenantId == Guid.Empty || context.WorkspaceId == Guid.Empty
            || context.PrincipalId != invocation.PrincipalId || context.TenantId != invocation.TenantId
            || context.WorkspaceId != invocation.WorkspaceId.Value) throw Error("connected_service_acquisition_execution_denied");
        var consumer = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(context.TenantId),
            _ => ResourceScopeRef.Workspace(context.WorkspaceId)
        };
        var acquisition = await acquisitions.GetExecutionAsync(invocation.Arguments.GetProperty("acquisitionId").GetString()!,
            invocation.RunId, cancellationToken);
        var operation = acquisition.Dataset!.Operation;
        var service = await services.AuthorizeAsync(operation.Definition.Service.Service,
            new(consumer, operation.Address), cancellationToken) ?? throw Error("connected_service_unavailable");
        var typeReference = service.Definition.Type;
        var type = (await store.GetExactAsync<ConnectedServiceTypeResource>(new(typeReference.Type.ScopeRef!.Value,
            typeReference.Type.Namespace ?? service.Namespace, ConnectedServiceResourceKinds.ConnectedServiceType,
            typeReference.Type.Name), cancellationToken))!.Value;
        var version = type.Definition.Versions.Single(item => item.Version == typeReference.Version);
        var (operationType, operationVersion) = await operationTypes.ResolveExactAsync(operation.Definition.Type,
            operation.Namespace, consumer, cancellationToken);
        if (BuiltinHttpOperation.Owns(type, version)
            && BuiltinHttpOperation.IsCore(operationType, BuiltinHttpOperation.TypeName)
            && operationVersion.Version == BuiltinHttpOperation.Version
            && operationVersion.Invocation.ProviderOperationId == BuiltinHttpOperation.ProviderOperationId)
            return await httpOperations.AcquireAsync(service, operation, acquisition.Parameters,
                new(consumer, operation.Address), cancellationToken);
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
        return await boundValues.ExecuteAsync(service, version, registration, manifest, invocation, async values =>
        {
            var result = await client.AcquireConnectedServiceOperationAsync(new(type.Name, version.Version,
                service.Definition.Options, values, operationType.Name, operationVersion.Version,
                operation.Definition.Definition, acquisition.Parameters, acquisition.CorrelationId), cancellationToken);
            DataSourceDatasetCompositionService.ValidateValue(acquisition.Dataset.OutputSchema, result.Result);
            return result.Result.Clone();
        }, cancellationToken);
    }
    private static DataSourceAcquisitionException Error(string code) => new(code, "Connected Service acquisition could not be completed.");
}

internal sealed class ConnectedServiceAcquisitionMcpTool(ConnectedServiceAcquisitionExecutor execution) : IInternalMcpToolHandler
{
    public const string Name = "connected-service.operations.acquire";
    public InternalMcpToolDefinition Definition { get; } = new(Name, Name, null,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { acquisitionId = new { type = "string", minLength = 1, maxLength = 128 } },
            required = new[] { "acquisitionId" },
            additionalProperties = false
        }), JsonSerializer.SerializeToElement(new { }), ExposeThroughMcp: false);
    public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken) =>
        execution.ExecuteAsync(invocation, cancellationToken);
}
