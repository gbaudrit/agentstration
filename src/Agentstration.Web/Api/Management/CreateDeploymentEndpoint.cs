using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed class CreateDeploymentEndpoint : IManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/deployments/{name}", HandleAsync)
        .RequireAuthorization(AgentstrationPolicies.CanWriteResources);

    private static Task<IResult> HandleAsync(
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
                name,
                body.RevisionName,
                new AgentDeploymentSpec
                {
                    Environment = body.Environment,
                    RuntimeProfileName = body.RuntimeProfileName,
                    RuntimeProfileNamespace = ResourceNamespace.Parse(body.RuntimeProfileNamespace),
                    HostingMode = body.HostingMode
                },
                cancellationToken);
            response.Headers.ETag = stored.ETag;
            response.Headers.Location = $"/api/deployments/{Uri.EscapeDataString(stored.Value.Metadata.Name)}";
            return Results.Accepted(response.Headers.Location, stored.Value);
        });
}
