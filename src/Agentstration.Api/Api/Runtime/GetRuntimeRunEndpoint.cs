using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class GetRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{runId}", HandleAsync).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanReadRuns);

    private static Task<IResult> HandleAsync(string runId, HttpResponse response, RuntimeRunService service, ICurrentRequestContext requestContext, CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(new WorkspaceId(requestContext.Current.WorkspaceId), runId, cancellationToken) ?? throw new RuntimeRunNotFoundException(runId);
            return RuntimeHttp.RunResult(stored, response, StatusCodes.Status200OK);
        });
}
