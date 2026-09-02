using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Web.Api.Runtime;

internal sealed class DeleteRuntimeRunEndpoint : IRuntimeEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapDelete("/{runId}", HandleAsync)
        .RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanDeleteRuns);

    private static Task<IResult> HandleAsync(
        string runId,
        HttpRequest request,
        RuntimeRunService service,
        ICurrentRequestContext requestContext,
        CancellationToken cancellationToken) =>
        RuntimeHttp.ExecuteAsync(async () =>
        {
            var expectedETag = request.Headers.IfMatch.FirstOrDefault()
                ?? throw new RuntimeRunValidationException("if_match_required", "Deleting a Runtime Run requires an If-Match ETag.");
            await service.DeleteAsync(new WorkspaceId(requestContext.Current.WorkspaceId), runId, expectedETag, cancellationToken);
            return Results.NoContent();
        });
}
