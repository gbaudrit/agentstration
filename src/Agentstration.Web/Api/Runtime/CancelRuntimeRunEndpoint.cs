using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class CancelRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapPost("/{runId}/cancel", HandleAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanRunAgents);

    private static Task<IResult> HandleAsync(string runId, HttpResponse response, RuntimeRunService service, ICurrentRequestContext requestContext, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () => RuntimeHttp.RunResult(await service.CancelAsync(new WorkspaceId(requestContext.Current.WorkspaceId), runId, cancellationToken), response, StatusCodes.Status200OK));
}
