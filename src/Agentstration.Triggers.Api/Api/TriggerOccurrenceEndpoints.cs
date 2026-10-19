using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed partial class TriggerEndpoints
{
    public static void MapOccurrences(RouteGroupBuilder group)
    {
        group.MapPost("/triggers/{name}/run", RunNowAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        group.MapGet("/triggers/{name}/occurrences", HistoryAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        group.MapPost("/namespaces/{namespace}/triggers/{name}/run", RunNowNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        group.MapGet("/namespaces/{namespace}/triggers/{name}/occurrences", HistoryNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadRuns);
    }
}
