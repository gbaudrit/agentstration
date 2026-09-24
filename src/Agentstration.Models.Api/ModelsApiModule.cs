using Agentstration.Web;
using Agentstration.Web.Api.Models;

namespace Agentstration.Models.Api;

public static class ModelsApiModule
{
    public static IServiceCollection AddModelsApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapModelsApi(this IEndpointRouteBuilder endpoints)
    {
        var providers = endpoints.MapGroup("/api/modelproviders");
        ListModelProvidersEndpoint.Map(providers);
        RefreshProviderModelsEndpoint.Map(providers);
        ListProviderModelsEndpoint.Map(providers);
        GetModelProviderStatusEndpoint.Map(providers);
        GetModelProviderUsagesEndpoint.Map(providers);
        TestModelProviderEndpoint.Map(providers);
        GetModelProviderEndpoint.Map(providers);
        CreateModelProviderEndpoint.Map(providers);
        PutModelProviderEndpoint.Map(providers);
        DeleteModelProviderEndpoint.Map(providers);

        var models = endpoints.MapGroup("/api/models");
        ListModelsEndpoint.Map(models);
        GetModelEndpoint.Map(models);

        var profiles = endpoints.MapGroup("/api/modelprofiles");
        ListModelProfilesEndpoint.Map(profiles);
        GetModelProfileUsagesEndpoint.Map(profiles);
        ResolveModelProfileEndpoint.Map(profiles);
        PreviewModelProfileOptionMigrationEndpoint.Map(profiles);
        ApplyModelProfileOptionMigrationEndpoint.Map(profiles);
        GetModelProfileEndpoint.Map(profiles);
        CreateModelProfileEndpoint.Map(profiles);
        PutModelProfileEndpoint.Map(profiles);
        DeleteModelProfileEndpoint.Map(profiles);

        var agents = endpoints.MapGroup("/api/agents");
        GetAgentModelEndpoint.Map(agents);
        GetAgentModelEndpoint.MapNamespaced(endpoints);
        return endpoints;
    }

    public static IEndpointRouteBuilder MapModelDiagnostics(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapOllamaDiagnostics();
}
