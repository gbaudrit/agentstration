using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class TriggerEndpoints : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/triggers", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/triggers/{name}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/triggers/{name}", PutAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/triggers/{name}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        group.MapPost("/triggers/{name}/run", RunNowAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        group.MapGet("/triggers/{name}/occurrences", HistoryAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapGet("/namespaces/{namespace}/triggers", ListNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/namespaces/{namespace}/triggers/{name}", GetNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/namespaces/{namespace}/triggers/{name}", PutNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/namespaces/{namespace}/triggers/{name}", DeleteNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        group.MapPost("/namespaces/{namespace}/triggers/{name}/run", RunNowNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        group.MapGet("/namespaces/{namespace}/triggers/{name}/occurrences", HistoryNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
    }

    private static Task<IResult> ListAsync(TriggerManagementService service, CancellationToken token) => ListCoreAsync(null, service, token);
    private static Task<IResult> ListNamespacedAsync(string @namespace, TriggerManagementService service, CancellationToken token) => ListCoreAsync(ResourceNamespace.Parse(@namespace), service, token);
    private static Task<IResult> ListCoreAsync(ResourceNamespace? @namespace, TriggerManagementService service, CancellationToken token) => ManagementHttp.ExecuteAsync(async () =>
    {
        var values = await service.ListAsync(token);
        return Results.Ok((@namespace is null ? values : values.Where(value => value.Value.Namespace == @namespace.Value)).Select(value => value.Value));
    });

    private static Task<IResult> GetAsync(string name, HttpResponse response, TriggerManagementService service, CancellationToken token) => GetCoreAsync(ResourceNamespace.Default, name, response, service, token);
    private static Task<IResult> GetNamespacedAsync(string @namespace, string name, HttpResponse response, TriggerManagementService service, CancellationToken token) => GetCoreAsync(ResourceNamespace.Parse(@namespace), name, response, service, token);
    private static Task<IResult> GetCoreAsync(ResourceNamespace @namespace, string name, HttpResponse response, TriggerManagementService service, CancellationToken token) => ManagementHttp.ExecuteAsync(async () =>
    {
        var stored = await service.GetAsync(@namespace, name, token) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Trigger, name, @namespace));
        return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
    });

    private static Task<IResult> PutAsync(string name, TriggerResource body, HttpRequest request, HttpResponse response, TriggerManagementService service, CancellationToken token) => PutCoreAsync(ResourceNamespace.Default, name, body, request, response, service, token);
    private static Task<IResult> PutNamespacedAsync(string @namespace, string name, TriggerResource body, HttpRequest request, HttpResponse response, TriggerManagementService service, CancellationToken token) => PutCoreAsync(ResourceNamespace.Parse(@namespace), name, body, request, response, service, token);
    private static Task<IResult> PutCoreAsync(ResourceNamespace @namespace, string name, TriggerResource body, HttpRequest request, HttpResponse response, TriggerManagementService service, CancellationToken token) => ManagementHttp.ExecuteAsync(async () =>
    {
        ManagementHttp.RequireApiVersion(request);
        if (body.Metadata.Name != name || body.Metadata.Namespace != @namespace) throw new TriggerValidationException("route_identity_mismatch", "Route, metadata.name and metadata.namespace must match.");
        var current = await service.GetAsync(@namespace, name, token);
        StoredResource<TriggerResource> stored;
        if (current is null)
        {
            stored = await service.CreateAsync(body, token);
            response.Headers.Location = @namespace == ResourceNamespace.Default ? $"/api/triggers/{Uri.EscapeDataString(name)}" : $"/api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/triggers/{Uri.EscapeDataString(name)}";
        }
        else
        {
            var ifMatch = ManagementHttp.IfMatch(request) ?? throw new ControlPlaneConcurrencyException("Updating a Trigger requires If-Match.");
            stored = await service.UpdateAsync(@namespace, name, body.Definition, ifMatch, token);
        }
        return ManagementHttp.ResourceResult(stored, response, current is null ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    });

    private static Task<IResult> DeleteAsync(string name, HttpRequest request, TriggerManagementService service, CancellationToken token) => DeleteCoreAsync(ResourceNamespace.Default, name, request, service, token);
    private static Task<IResult> DeleteNamespacedAsync(string @namespace, string name, HttpRequest request, TriggerManagementService service, CancellationToken token) => DeleteCoreAsync(ResourceNamespace.Parse(@namespace), name, request, service, token);
    private static Task<IResult> DeleteCoreAsync(ResourceNamespace @namespace, string name, HttpRequest request, TriggerManagementService service, CancellationToken token) => ManagementHttp.ExecuteAsync(async () =>
    {
        var ifMatch = ManagementHttp.IfMatch(request) ?? throw new ControlPlaneConcurrencyException("Deleting a Trigger requires If-Match.");
        await service.DeleteAsync(@namespace, name, ifMatch, token);
        return Results.NoContent();
    });

    private static Task<IResult> RunNowAsync(string name, ICurrentRequestContext context, IAuthorizationService authorization, TriggerFiringService service, CancellationToken token) => RunNowCoreAsync(ResourceNamespace.Default, name, context, authorization, service, token);
    private static Task<IResult> RunNowNamespacedAsync(string @namespace, string name, ICurrentRequestContext context, IAuthorizationService authorization, TriggerFiringService service, CancellationToken token) => RunNowCoreAsync(ResourceNamespace.Parse(@namespace), name, context, authorization, service, token);
    private static Task<IResult> RunNowCoreAsync(ResourceNamespace @namespace, string name, ICurrentRequestContext context, IAuthorizationService authorization, TriggerFiringService service, CancellationToken token) => ManagementHttp.ExecuteAsync(async () =>
    {
        await authorization.EnsurePermissionAsync(context.Current, AuthorizationPermissions.RunsExecute, token);
        var occurrence = await service.RunNowAsync(@namespace, name, token);
        return Results.Accepted($"/api/triggers/{Uri.EscapeDataString(name)}/occurrences", occurrence);
    });

    private static Task<IResult> HistoryAsync(string name, int? take, TriggerManagementService management, TriggerFiringService firing, CancellationToken token) => HistoryCoreAsync(ResourceNamespace.Default, name, take, management, firing, token);
    private static Task<IResult> HistoryNamespacedAsync(string @namespace, string name, int? take, TriggerManagementService management, TriggerFiringService firing, CancellationToken token) => HistoryCoreAsync(ResourceNamespace.Parse(@namespace), name, take, management, firing, token);
    private static Task<IResult> HistoryCoreAsync(ResourceNamespace @namespace, string name, int? take, TriggerManagementService management, TriggerFiringService firing, CancellationToken token) => ManagementHttp.ExecuteAsync(async () =>
    {
        var trigger = await management.GetAsync(@namespace, name, token) ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.Trigger, name, @namespace));
        return Results.Ok(await firing.ListHistoryAsync(trigger.Value.WorkspaceId, trigger.Value.Uid, take ?? 50, token));
    });
}
