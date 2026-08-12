using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Api.Management;

internal sealed class PutAgentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPut("/agents/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(
        string name,
        AgentResourceRequest body,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            if (!string.Equals(body.Metadata.Name, name, StringComparison.Ordinal))
                throw new AgentDefinitionValidationException("route_name_mismatch", "The resource name must match the route agentName.");
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
