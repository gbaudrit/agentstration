using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Management;

internal sealed partial class TriggerEndpoints
{
    public static void MapConfiguration(RouteGroupBuilder group)
    {
        group.MapGet("/triggers", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/triggers/schedule-preview", PreviewScheduleAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/triggers/{name}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/triggers/{name}", PutAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/triggers/{name}", DeleteAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
        group.MapGet("/namespaces/{namespace}/triggers", ListNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/namespaces/{namespace}/triggers/{name}", GetNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/namespaces/{namespace}/triggers/{name}", PutNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/namespaces/{namespace}/triggers/{name}", DeleteNamespacedAsync).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }
}
