using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Infrastructure.ConnectedServices;

// A Flow can request only its own persisted discovery. All connection authority is server-derived.
internal sealed class ConnectedServiceDiscoveryExecutor(
    ConnectedServiceOperationDiscoveryService discoveries,
    IResourceStore store, ConnectedServiceManagementService services, ConnectedServiceBoundValues boundValues,
    ISecretResolver secrets,
    ICurrentRequestContext current,
    IHttpClientFactory clients,
    BuiltinHttpOperationExecutor httpOperations)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        try { return await ExecuteCoreAsync(invocation, cancellationToken); }
        catch (AepProtocolException) { throw Error("connected_service_provider_discovery_failed"); }
        catch (HttpRequestException) { throw Error("connected_service_provider_unavailable"); }
    }

    private async Task<JsonElement?> ExecuteCoreAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.CallerKind != ToolDefinitionCallerKind.Flow || string.IsNullOrEmpty(invocation.RunId)
            || current.AccessMode is not (ControlPlaneAccessMode.Workspace or ControlPlaneAccessMode.Instance
                or ControlPlaneAccessMode.TenantAdministration))
            throw Error("connected_service_discovery_execution_denied");
        RequestContext operational;
        try { operational = current.Current; }
        catch (InvalidOperationException) { throw Error("connected_service_discovery_execution_denied"); }
        if (operational.PrincipalId == Guid.Empty || operational.TenantId == Guid.Empty || operational.WorkspaceId == Guid.Empty
            || operational.PrincipalId != invocation.PrincipalId || operational.TenantId != invocation.TenantId
            || operational.WorkspaceId != invocation.WorkspaceId.Value)
            throw Error("connected_service_discovery_execution_denied");
        var consumer = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(operational.TenantId),
            _ => ResourceScopeRef.Workspace(operational.WorkspaceId)
        };
        var id = invocation.Arguments.GetProperty("discoveryId").GetString()
            ?? throw Error("connected_service_discovery_id_required");
        var @namespace = ResourceNamespace.Parse(invocation.Arguments.GetProperty("discoveryNamespace").GetString()!);
        var execution = await discoveries.GetExecutionAsync(@namespace, id, invocation.RunId, cancellationToken);
        var (discovery, service, type, version) = execution;
        var resolved = await services.AuthorizeAsync(discovery.Service.Service,
            new(consumer, discovery.Address), cancellationToken)
            ?? throw Error("connected_service_unavailable");
        if (resolved.Uid != discovery.Service.ServiceUid || resolved.Generation != discovery.Service.Generation)
            throw Error("connected_service_revision_changed");
        if (BuiltinHttpOperation.Owns(type, version))
            return await httpOperations.DiscoverAsync(service, version, new(consumer, discovery.Address), cancellationToken);
        var manager = type.Governance?.ManagedBy;
        if (manager?.Kind != ResourceManagementAuthorityKind.ExtensionInstallation)
            throw Error("connected_service_discovery_provider_required");
        var registrations = await store.ListExactAsync<ExtensionRegistrationResource>(
            type.ScopeRef!.Value, ExtensionKinds.ExtensionRegistration, 0, 1000, cancellationToken);
        var registration = registrations.SingleOrDefault(item => manager.Reference ==
            $"{item.Value.ScopeRef!.Value.Value}/{item.Value.Kind}/{item.Value.Namespace}/{item.Value.Name}")?.Value
            ?? throw Error("connected_service_discovery_provider_missing");
        if (!registration.Definition.Enabled || registration.Definition.ExpectedExtensionId != type.Governance!.Origin.Reference)
            throw Error("connected_service_discovery_provider_unavailable");
        var credentials = AepExtensionCredentials.Create(registration, secrets);
        using var http = clients.CreateClient("agentstration-aep");
        http.BaseAddress = registration.Definition.Endpoint;
        var client = new AepClient(http, credentials, expectedExtensionId: registration.Definition.ExpectedExtensionId);
        var manifest = await client.DiscoverAsync(cancellationToken);
        if (!manifest.Capabilities.TryGetValue(AepConnectedServiceDiscoveryProtocol.Capability, out var capability)
            || capability.Version != AepConnectedServiceDiscoveryProtocol.Version
            || !manifest.Capabilities.TryGetValue(AepCapabilityNames.BoundValues, out var boundCapability)
            || boundCapability.Version != AepProtocol.BoundValuesCapabilityVersion)
            throw Error("connected_service_discovery_provider_unsupported");
        var advertised = manifest.Contributions.ConnectedServiceTypes?.SingleOrDefault(item => item.Id == type.Name)?
            .Versions.SingleOrDefault(item => item.Version == version.Version);
        if (advertised?.SchemaDigest != version.SchemaDigest)
            throw Error("connected_service_discovery_provider_changed");
        return await boundValues.ExecuteAsync(service, version, registration, manifest, invocation, async values =>
        {
            var result = await client.DiscoverConnectedServiceOperationsAsync(new(type.Name, version.Version,
                resolved.Definition.Options, values, discovery.CorrelationId), cancellationToken);
            var output = new ConnectedServiceOperationDiscoveryManifest
            {
                Operations = result.Operations.Select(operation => new ConnectedServiceOperationCandidate
                {
                    ExternalId = operation.ExternalId,
                    DisplayName = operation.DisplayName,
                    Description = operation.Description,
                    Type = version.OperationDiscovery!.ProducedTypes.SingleOrDefault(reference =>
                        reference.Type.Name == operation.TypeId && reference.Version == operation.TypeVersion)
                        ?? throw Error("connected_service_operation_type_not_allowed"),
                    Definition = operation.Definition.Clone()
                }).ToArray(),
                Diagnostics = result.Diagnostics.Select(item => new ConnectedServiceOperationDiscoveryDiagnostic(item.Code, item.Message)).ToArray()
            };
            var manifestOutput = JsonSerializer.SerializeToElement(output, JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(manifestOutput.GetRawText()) > AepConnectedServiceDiscoveryProtocol.MaximumResponseBytes)
                throw Error("connected_service_discovery_manifest_too_large");
            return manifestOutput;
        }, cancellationToken);
    }

    private static ConnectedServiceOperationDiscoveryException Error(string code) => new(code, "Connected Service provider discovery could not be completed.");
}

internal sealed class ConnectedServiceDiscoveryMcpTool(ConnectedServiceDiscoveryExecutor execution) : IInternalMcpToolHandler
{
    public const string Name = "connected-service.operations.discover";
    public InternalMcpToolDefinition Definition { get; } = new(Name, Name, null,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                discoveryId = new { type = "string", minLength = 1, maxLength = 128 },
                discoveryNamespace = new { type = "string", minLength = 1, maxLength = 128 }
            },
            required = new[] { "discoveryId", "discoveryNamespace" },
            additionalProperties = false
        }), JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                contract = new { type = "string", @enum = new[] { ConnectedServiceOperationDiscoveryContracts.V1 } },
                operations = new { type = "array", items = new { type = "object" } },
                diagnostics = new { type = "array", items = new { type = "object" } }
            },
            required = new[] { "contract", "operations", "diagnostics" },
            additionalProperties = false
        }), ExposeThroughMcp: false);

    public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken) =>
        execution.ExecuteAsync(invocation, cancellationToken);
}
