using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Api.Management;

internal sealed class PutAgentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPut("/agents/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(
        string resourceGroup,
        string name,
        AgentResourceRequest body,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            if (!string.Equals(body.Name, name, StringComparison.Ordinal))
                throw new AgentDefinitionValidationException("route_name_mismatch", "The resource name must match the route agentName.");
            if (!string.Equals(body.ResourceGroup, resourceGroup, StringComparison.Ordinal))
                throw new AgentDefinitionValidationException("route_resource_group_mismatch", "The resourceGroup must match the route resourceGroup.");
            var resource = new AgentResource
            {
                Id = ManagementHttp.AgentId(resourceGroup, name),
                Name = body.Name,
                Type = body.Type,
                ApiVersion = body.ApiVersion,
                ResourceGroup = body.ResourceGroup,
                Location = body.Location,
                Tags = body.Tags ?? new Dictionary<string, string>(),
                Properties = body.Properties
            };
            var stored = await service.PutAgentAsync(resource, ManagementHttp.IfMatch(request), ManagementHttp.IfNoneMatch(request), cancellationToken);
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
