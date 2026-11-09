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
public sealed class ConnectedServiceDiscoveryExecutor(
    ConnectedServiceOperationDiscoveryService discoveries,
    IResourceStore store, ConnectedServiceManagementService services, IParameterResolver parameters,
    ISecretResolver secrets, ISecretCapabilityService capabilities,
    ICurrentRequestContext current,
    IHttpClientFactory clients, IConfiguration configuration)
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
            || !manifest.Capabilities.TryGetValue(AepCapabilityNames.BoundValues, out var boundValues)
            || boundValues.Version != AepProtocol.BoundValuesCapabilityVersion)
            throw Error("connected_service_discovery_provider_unsupported");
        var advertised = manifest.Contributions.ConnectedServiceTypes?.SingleOrDefault(item => item.Id == type.Name)?
            .Versions.SingleOrDefault(item => item.Version == version.Version);
        if (advertised?.SchemaDigest != version.SchemaDigest)
            throw Error("connected_service_discovery_provider_changed");
        // Issue Secret grants from saved bindings; no Secret bytes enter the AEP request or Flow values.
        var handles = new List<SecretCapabilityHandle>();
        var values = new List<AepBoundValue>();
        try
        {
            var declared = version.Inputs.Select(input => input.Id).ToArray();
            foreach (var binding in service.Definition.Bindings)
            {
                if (binding.Kind == ConnectedServiceBindingKind.Parameter)
                {
                    var parameter = await parameters.ResolveAsync(binding.Parameter!,
                        new(service.ScopeRef!.Value, service.Address), cancellationToken)
                        ?? throw Error("connected_service_parameter_unavailable");
                    values.Add(AepBoundValue.Inline(binding.InputId, parameter.Value));
                }
                else
                {
                    if (!manifest.Capabilities.TryGetValue(AepCapabilityNames.SecretAccess, out var access)
                        || access.Version != AepProtocol.SecretAccessVersion)
                        throw Error("connected_service_secret_access_unsupported");
                    if (!Uri.TryCreate(configuration["Agentstration:Aep:SecretAccess:PublicBaseUrl"], UriKind.Absolute, out var baseUri)
                        || baseUri.Scheme != "https" && !(baseUri.Scheme == "http" && baseUri.IsLoopback))
                        throw Error("connected_service_secret_callback_unavailable");
                    var context = new SecretCapabilityContext(
                        new(registration.ScopeRef!.Value, registration.Namespace, registration.Kind, registration.Name),
                        manifest.Extension.Id, new(service.ScopeRef!.Value, service.Namespace, service.Kind, service.Name),
                        binding.InputId, invocation.CallId);
                    var handle = await capabilities.IssueAsync(context, new(binding.InputId, binding.Secret!),
                        declared, cancellationToken, cancellationToken);
                    handles.Add(handle);
                    values.Add(AepBoundValue.Secured(binding.InputId, new(AepProtocol.SecretAccessVersion,
                        new Uri(baseUri, AepProtocol.SecretAccessPath), manifest.Extension.Id,
                        binding.InputId, invocation.CallId, handle.RevealForTransport())));
                }
            }
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
        }
        finally
        {
            foreach (var handle in handles) capabilities.Revoke(handle);
        }
    }

    private static ConnectedServiceOperationDiscoveryException Error(string code) => new(code, "Connected Service provider discovery could not be completed.");
}

public sealed class ConnectedServiceDiscoveryMcpTool(ConnectedServiceDiscoveryExecutor execution) : IInternalMcpToolHandler
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
        }), ExposeThroughMcp: false);

    public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken) =>
        execution.ExecuteAsync(invocation, cancellationToken);
}
