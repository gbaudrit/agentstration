using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Api.Management;

internal sealed class CreateDeploymentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/deployments/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(
        string resourceGroup,
        string name,
        CreateDeploymentRequest body,
        HttpRequest request,
        HttpResponse response,
        AgentManagementService service,
        CancellationToken cancellationToken) =>
        ManagementHttp.ExecuteAsync(async () =>
        {
            ManagementHttp.RequireApiVersion(request);
            var stored = await service.CreateDeploymentAsync(
                resourceGroup,
                name,
                "local",
                body.RevisionId,
                new AgentDeploymentSpec { Environment = body.Environment, RuntimeProfileId = body.RuntimeProfileId, HostingMode = body.HostingMode },
                cancellationToken);
            response.Headers.ETag = stored.ETag;
            response.Headers.Location = $"{stored.Value.Id}?api-version={ManagementApiVersions.V20260801}";
            return Results.Accepted(response.Headers.Location, stored.Value);
        });
}
