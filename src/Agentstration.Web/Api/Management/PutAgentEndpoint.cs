using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class PutAgentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPut("/agents/{name}", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanManageAgents);
        group.MapPut("/namespaces/{namespace}/agents/{name}", HandleNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanManageAgents);
    }

    private static Task<IResult> HandleNamespacedAsync(string @namespace, string name, AgentResourceRequest body, HttpRequest request, HttpResponse response, AgentManagementService service, CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Parse(@namespace), name, body, request, response, service, cancellationToken);

    private static Task<IResult> HandleAsync(
        string name,
        AgentResourceRequest body,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        HandleCoreAsync(ResourceNamespace.Default, name, body, request, response, service, cancellationToken);

    private static Task<IResult> HandleCoreAsync(ResourceNamespace @namespace, string name, AgentResourceRequest body, HttpRequest request, HttpResponse response, AgentManagementService service, CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            if (!string.Equals(body.Metadata.Name, name, StringComparison.Ordinal))
                throw new AgentDefinitionValidationException("route_name_mismatch", "The resource name must match the route agentName.");
            if (body.Metadata.Namespace != @namespace)
                throw new AgentDefinitionValidationException("route_namespace_mismatch", "The resource namespace must match the route namespace.");
            var resource = new AgentResource
            {
                ApiVersion = body.ApiVersion,
                Kind = body.Kind,
                Metadata = body.Metadata,
                Definition = body.Definition
            };
            var stored = await service.PutAgentAsync(resource, ManagementHttp.IfMatch(request), ManagementHttp.IfNoneMatch(request), cancellationToken);
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
