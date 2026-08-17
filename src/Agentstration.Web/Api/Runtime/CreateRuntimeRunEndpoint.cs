using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Runtime;

internal sealed class CreateRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanRunAgents);

    private static Task<IResult> HandleAsync(CreateRuntimeRunRequest body, HttpResponse response, RuntimeRunService service, ICurrentRequestContext requestContext, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var current = requestContext.Current;
            var scope = new RuntimeRunScope(current.TenantId, current.WorkspaceId, current.PrincipalId);
            var stored = await service.CreateAsync(body.Agent, body.Input, body.Execution, body.Origin, current.PrincipalId.ToString("D"), scope, cancellationToken);
            response.Headers.ETag = stored.ETag;
            response.Headers.Location = $"/api/runtime/runs/{stored.Value.Id}";
            return Results.Accepted(response.Headers.Location, stored.Value);
        });
}
