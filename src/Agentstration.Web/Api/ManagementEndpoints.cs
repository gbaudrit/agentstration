using Agentstration.Web.Api.Management;

namespace Agentstration.Web;

public static class ManagementEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationManagementApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/resourceGroups/{resourceGroup}/providers/Agentstration.Agents");

        PutAgentTypeEndpoint.Map(group);
        ListAgentTypesEndpoint.Map(group);
        GetAgentTypeEndpoint.Map(group);
        PutAgentEndpoint.Map(group);
        ListAgentsEndpoint.Map(group);
        GetAgentEndpoint.Map(group);
        DeleteAgentEndpoint.Map(group);
        CreateAgentRevisionEndpoint.Map(group);
        CreateDeploymentEndpoint.Map(group);
        GetDeploymentEndpoint.Map(group);
        StartDeploymentEndpoint.Map(group);
        StopDeploymentEndpoint.Map(group);
        ReconcileDeploymentEndpoint.Map(group);
        RouteAndExecuteEndpoint.Map(group);

        return endpoints;
    }
}
