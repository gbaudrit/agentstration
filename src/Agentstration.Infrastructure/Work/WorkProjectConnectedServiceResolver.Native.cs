using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed partial class WorkProjectConnectedServiceResolver
{
    public async Task<Result<WorkProjectResolvedBinding>> ResolveNativeAsync(WorkspaceId workspace, WorkProjectContextBinding binding,
        WorkProjectResolvedBinding allowed, Func<WorkProjectResourceReference, CancellationToken, Task<Result<WorkProjectResolvedBinding>>> authorize, CancellationToken token)
    {
        if (binding.NativeService is not { } selected || !WorkProjectContextRules.Valid([binding]) || nativeExtensions is null || nativeTransport is null)
            return Failure("work_project_connection_invalid");
        var inspected = await InspectServiceAsync(workspace, binding.Name, selected, authorize, token);
        if (!inspected.IsSuccess) return Failure(inspected.ErrorCode!);
        try
        {
            var tool = (await resources.GetExactAsync<ToolResource>(binding.Target.Address, token))?.Value;
            if (tool?.Definition.Provider is not { } providerRef || !(toolAccess is null ? tool.Definition.Enabled : await toolAccess.IsEnabledAsync(tool, token)) || tool.Definition.RequiresApproval
                || tool.Definition.Discovery?.Available != true || !GitWorkProjectSharingGateway.HasRequiredCapabilities(tool)
                || !tool.Definition.Metadata.TryGetValue(TransportMetadata, out var declaration)) return Failure("work_project_connection_incompatible");
            _ = await ValidateTransportAsync(declaration, inspected.Value!.Evidence, token);
            var provider = (await resources.GetExactAsync<ToolProviderResource>(new(providerRef.ScopeRef ?? tool.ScopeRef!.Value,
                providerRef.Namespace ?? tool.Namespace, ToolResourceKinds.ToolProvider, providerRef.Name), token))?.Value;
            if (provider is null || !provider.Definition.Enabled || provider.Definition.ProviderType != ToolProviderType.Aep) return Failure("work_project_connection_adapter_unavailable");
            var extension = await nativeExtensions.ResolveAsync(provider, token);
            foreach (var reference in new[] { Reference(provider), Reference(extension.Registration) })
            {
                var permission = await authorize(reference, token);
                if (!permission.IsSuccess) return Failure(permission.ErrorCode!);
            }
            var manifest = await nativeTransport.DiscoverAsync(extension, extension.Registration.Definition.ExpectedExtensionId, token);
            var mappings = (manifest.Contributions.Tools ?? []).Where(value => value.Id == tool.Definition.ExternalId).ToArray();
            if (mappings.Length != 1 || mappings[0].Metadata is not { } metadata
                || !metadata.TryGetValue(TransportMetadata, out var transportDeclaration) || !JsonElement.DeepEquals(transportDeclaration, declaration)
                || !metadata.TryGetValue("agentstration.git.repository", out var capabilities)
                || !JsonElement.DeepEquals(capabilities, tool.Definition.Metadata["agentstration.git.repository"])) return Failure("work_project_connection_incompatible");
            return Result<WorkProjectResolvedBinding>.Success(allowed with
            {
                Service = inspected.Value.Evidence,
                NativeRoute = new(Reference(provider), Reference(extension.Registration), ToolInvocationFingerprint.AepRoute(extension.Registration, manifest, mappings[0]))
            });
        }
        catch (Exception error) when (error is ToolResolutionException or ConnectedServiceExportInspectionException or JsonException or KeyNotFoundException
            or InvalidOperationException or Agentstration.Aep.Client.AepProtocolException or HttpRequestException or ModelContextProtocol.McpException)
        { return Failure("work_project_connection_incompatible"); }
    }

    public async Task<JsonElement> NativeOptionsAsync(WorkProjectResolvedBinding connection, CancellationToken token)
    {
        var evidence = connection.Service ?? throw InvalidHeaders();
        ConnectedServiceResource service;
        using (scopes.PushSystem()) service = (await resources.GetExactAsync<ConnectedServiceResource>(evidence.Service.Address, token))?.Value ?? throw InvalidHeaders();
        if (Reference(service) != evidence.Service) throw InvalidHeaders();
        return service.Definition.Options.Clone();
    }

    public async Task<ResolvedSecretInputs> ResolveNativeHeadersAsync(WorkspaceId workspace, WorkProjectResolvedBinding connection, CancellationToken token)
    {
        if (connection.Binding.NativeService is not { } service || connection.Service is not { } evidence) throw InvalidHeaders();
        try { return await ResolveHeadersCoreAsync(workspace, service, connection.Binding.Target, connection.Binding.Name, evidence, token); }
        catch (Exception error) when (error is Agentstration.ConnectedServices.ConnectedServiceValidationException or Agentstration.ConnectedServices.ConnectedServiceAccessDeniedException
            or Agentstration.ConnectedServices.ConnectedServiceUnavailableException or SecretResolutionException or Agentstration.Parameters.ParameterAccessDeniedException
            or Agentstration.Parameters.ParameterManagementException or KeyNotFoundException or JsonException or InvalidOperationException)
        { throw InvalidHeaders(); }
    }
}
