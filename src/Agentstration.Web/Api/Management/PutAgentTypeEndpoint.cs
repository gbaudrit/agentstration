using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Api.Management;

internal sealed class PutAgentTypeEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPut("/agentTypes/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(
        string resourceGroup,
        string name,
        ResourceEnvelope<AgentTypeDefinition> body,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var resource = new AgentTypeResource
            {
                Id = ResourceIdentifier.Create(resourceGroup, AgentstrationProviderNamespaces.Agents, "agentTypes", name).Value,
                Name = name,
                Type = AgentstrationResourceTypes.AgentTypes,
                ApiVersion = ManagementApiVersions.V20260801,
                ResourceGroup = resourceGroup,
                Location = body.Location ?? "local",
                Tags = body.Tags ?? new Dictionary<string, string>(),
                Properties = body.Properties
            };
            var stored = await service.PutAgentTypeAsync(resource, ManagementHttp.IfMatch(request), ManagementHttp.IfNoneMatch(request), cancellationToken);
            return ManagementHttp.ResourceResult(stored, response, StatusCodes.Status200OK);
        });
}
