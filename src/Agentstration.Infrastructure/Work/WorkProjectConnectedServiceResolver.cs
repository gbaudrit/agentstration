using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

// Uses the delivered service inspector. Only standard, explicitly mapped target exports are read.
public sealed partial class WorkProjectConnectedServiceResolver(IResourceStore resources, IConnectedServiceExportInspector services,
    IParameterResolver parameters, IRequestContextScopeFactory scopes, IConnectedServiceResolver? serviceResolver = null,
    Agentstration.Tools.Mcp.IAepExtensionRegistrationResolver? nativeExtensions = null, Agentstration.Tools.Mcp.AepMcpTransport? nativeTransport = null, ToolAccessService? toolAccess = null)
{
    public async Task<Result<WorkProjectResolvedBinding>> ResolveAsync(WorkspaceId workspace, WorkProjectConnectionSelection selection,
        Func<WorkProjectResourceReference, CancellationToken, Task<Result<WorkProjectResolvedBinding>>> authorize,
        Func<WorkProjectContextBinding, WorkProjectConnectionEvidence, CancellationToken, Task<Result<WorkProjectResolvedBinding>>> resolve, CancellationToken token)
    {
        var inspected = await InspectAsync(workspace, selection, authorize, token);
        if (!inspected.IsSuccess) return Result<WorkProjectResolvedBinding>.Failure(inspected.ErrorCode!);
        var (evidence, fields) = inspected.Value!;
        var assignment = selection.Assignment;
        var tool = (await resources.GetExactAsync<ToolResource>(assignment.DescribeTool.Address, token))?.Value;
        if (tool?.Definition.Provider is not { } providerRef || (providerRef.ScopeRef ?? tool.ScopeRef) is not { } providerScope)
            return Failure("work_project_connection_adapter_unavailable");
        var provider = (await resources.GetExactAsync<ToolProviderResource>(new(providerScope, providerRef.Namespace ?? tool.Namespace,
            ToolResourceKinds.ToolProvider, providerRef.Name), token))?.Value;
        if (provider?.Definition.Aep?.Extension is not { ScopeRef: { } extensionScope } extensionRef)
            return Failure("work_project_connection_adapter_unavailable");
        var extension = (await resources.GetExactAsync<ExtensionRegistrationResource>(new(extensionScope,
            extensionRef.Namespace ?? provider.Namespace, ExtensionKinds.ExtensionRegistration, extensionRef.Name), token))?.Value;
        if (extension is null) return Failure("work_project_connection_adapter_unavailable");
        var binding = WorkProjectConnectionRules.Binding(selection) with
        {
            Connection = null,
            Contract = new(Reference(provider), Reference(extension), selection.Requirement.Version,
                fields["providerId"], fields["targetId"], fields["bindingId"], fields["revision"], selection.Requirement.RequiredOperations)
        };
        var result = await resolve(binding, evidence, token);
        return !result.IsSuccess ? result : Result<WorkProjectResolvedBinding>.Success(result.Value! with
        { Binding = result.Value.Binding with { Connection = selection }, Service = evidence });
    }

    public async Task<Result<Inspection>> InspectAsync(WorkspaceId workspace, WorkProjectConnectionSelection selection,
        Func<WorkProjectResourceReference, CancellationToken, Task<Result<WorkProjectResolvedBinding>>> authorize, CancellationToken token)
    {
        if (!WorkProjectConnectionRules.Valid(selection)) return Invalid("work_project_connection_invalid");
        var adapter = await authorize(selection.Assignment.DescribeTool, token);
        if (!adapter.IsSuccess) return Invalid(adapter.ErrorCode!);
        var inspected = await InspectServiceAsync(workspace, selection.Requirement.Name, selection.Assignment.Service, authorize, token);
        if (!inspected.IsSuccess) return Invalid(inspected.ErrorCode!);
        var (evidence, metadata, values) = inspected.Value!;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in selection.Assignment.Exports)
        {
            var export = metadata.Exports.SingleOrDefault(value => value.Id == mapping.ExportId);
            if (export is not { Type: ConnectedServiceValueType.Text, Protection: ConnectedServiceValueProtection.Standard, SourceInputId: { } input }
                || !values.TryGetValue(input, out var value) || value.ValueKind != JsonValueKind.String
                || !WorkProjectContextRules.Text(value.GetString())) return Invalid("work_project_connection_incompatible");
            fields.Add(mapping.Field, value.GetString()!);
        }
        if (selection.Requirement.Target is { } target && (target.ProviderId != fields["providerId"] || target.TargetId != fields["targetId"]))
            return Invalid("work_project_connection_target_mismatch");
        return Result<Inspection>.Success(new(evidence, fields));
    }

    private async Task<Result<ServiceInspection>> InspectServiceAsync(WorkspaceId workspace, string name, WorkProjectResourceReference selectedService,
        Func<WorkProjectResourceReference, CancellationToken, Task<Result<WorkProjectResolvedBinding>>> authorize, CancellationToken token)
    {
        try
        {
            foreach (var reference in new[] { selectedService })
            {
                var permission = await authorize(reference, token);
                if (!permission.IsSuccess) return InvalidService(permission.ErrorCode!);
            }
            var address = selectedService.Address;
            var metadata = await services.InspectAsync(new()
            {
                Service = new(new(address.Namespace, address.Kind, address.Name), address.ScopeRef),
                ServiceUid = selectedService.Uid,
                Generation = selectedService.Generation
            }, new(ResourceScopeRef.Workspace(workspace.Value), new(address.Namespace, "Project", name)), token);
            ConnectedServiceResource service;
            ConnectedServiceTypeResource type;
            using (scopes.PushSystem())
            {
                service = (await resources.GetExactAsync<ConnectedServiceResource>(address, token))?.Value
                    ?? throw new ConnectedServiceExportInspectionException("storage_service_unavailable");
                var typeRef = service.Definition.Type;
                type = (await resources.GetExactAsync<ConnectedServiceTypeResource>(new(typeRef.Type.ScopeRef ?? address.ScopeRef,
                    typeRef.Type.Namespace ?? address.Namespace, ConnectedServiceResourceKinds.ConnectedServiceType, typeRef.Type.Name), token))?.Value
                    ?? throw new ConnectedServiceExportInspectionException("storage_service_unavailable");
            }
            if (Reference(service) != selectedService || service.Definition.Type != metadata.Type || type.Uid != metadata.Type.TypeUid)
                return InvalidService("work_project_connection_stale");
            var version = type.Definition.Versions.SingleOrDefault(value => value.Version == metadata.Type.Version && value.SchemaDigest == metadata.Type.SchemaDigest);
            if (version is null) return InvalidService("work_project_connection_incompatible");
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var binding in service.Definition.Bindings.OrderBy(value => value.InputId, StringComparer.Ordinal))
            {
                if (!version.Inputs.Any(input => input.Id == binding.InputId && input.Protection == ConnectedServiceValueProtection.Standard)) continue;
                if (binding.Kind == ConnectedServiceBindingKind.Literal && binding.Literal is { } literal) values.Add(binding.InputId, literal);
                else if (binding.Kind == ConnectedServiceBindingKind.Parameter && binding.Parameter is { } parameter)
                {
                    // The service and its input access were authorized by the inspector above.
                    using var ownerRead = scopes.PushSystem();
                    var value = await parameters.ResolveAsync(parameter, new(address.ScopeRef, service.Address), token);
                    if (value is null) return InvalidService("work_project_connection_unavailable");
                    values.Add(binding.InputId, value.Value);
                }
                else return InvalidService("work_project_connection_incompatible");
            }
            var evidence = new WorkProjectConnectionEvidence(selectedService, Reference(type), metadata.Type.Version, metadata.Type.SchemaDigest,
                WorkPlanDefinition.Digest(new { service.Definition.Options, service.Definition.Type, service.Definition.Bindings, Values = values }));
            return Result<ServiceInspection>.Success(new(evidence, metadata, values));
        }
        catch (ParameterAccessDeniedException) { return InvalidService("work_project_binding_denied"); }
        catch (ParameterManagementException) { return InvalidService("work_project_connection_incompatible"); }
        catch (ParameterResourceNotFoundException) { return InvalidService("work_project_connection_unavailable"); }
        catch (ConnectedServiceExportInspectionException error)
        {
            return InvalidService(error.Code == "storage_service_access_denied" ? "work_project_binding_denied"
                : error.Code == "storage_service_stale" ? "work_project_connection_stale" : "work_project_connection_unavailable");
        }
    }

    public sealed record Inspection(WorkProjectConnectionEvidence Evidence, IReadOnlyDictionary<string, string> Fields);
    private static WorkProjectResourceReference Reference(Resource resource) => new(new(resource.ScopeRef!.Value, resource.Namespace,
        resource.Kind, resource.Name), resource.Uid, resource.Generation);
    private sealed record ServiceInspection(WorkProjectConnectionEvidence Evidence, ConnectedServiceExportMetadata Metadata, IReadOnlyDictionary<string, JsonElement> Values);
    private static Result<ServiceInspection> InvalidService(string code) => Result<ServiceInspection>.Failure(code);
    private static Result<Inspection> Invalid(string code) => Result<Inspection>.Failure(code);
    private static Result<WorkProjectResolvedBinding> Failure(string code) => Result<WorkProjectResolvedBinding>.Failure(code);
}
