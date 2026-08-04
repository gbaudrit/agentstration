using Agentstration.Web.Api.Models;

namespace Agentstration.Web;

public static class ModelManagementEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationModelManagementApi(this IEndpointRouteBuilder endpoints)
    {
        var providers = endpoints.MapGroup("/api/modelproviders");
        ListModelProvidersEndpoint.Map(providers);
        ListProviderModelsEndpoint.Map(providers);
        GetModelProviderStatusEndpoint.Map(providers);
        GetModelProviderUsagesEndpoint.Map(providers);
        TestModelProviderEndpoint.Map(providers);
        GetModelProviderEndpoint.Map(providers);
        CreateModelProviderEndpoint.Map(providers);
        PutModelProviderEndpoint.Map(providers);
        DeleteModelProviderEndpoint.Map(providers);

        var profiles = endpoints.MapGroup("/api/modelprofiles");
        ListModelProfilesEndpoint.Map(profiles);
        GetModelProfileUsagesEndpoint.Map(profiles);
        ResolveModelProfileEndpoint.Map(profiles);
        GetModelProfileEndpoint.Map(profiles);
        CreateModelProfileEndpoint.Map(profiles);
        PutModelProfileEndpoint.Map(profiles);
        DeleteModelProfileEndpoint.Map(profiles);

        RuntimeProfileEndpoints.Map(endpoints.MapGroup("/api/runtimeprofiles"));

        var agents = endpoints.MapGroup("/api/agents");
        GetAgentModelEndpoint.Map(agents);
        return endpoints;
    }
}
