using Agentstration.DataSources.Api.Internal;

namespace Agentstration.DataSources.Api;

public static class DataSourcesApiModule
{
    public static IServiceCollection AddDataSourcesApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapDataSourcesApi(this IEndpointRouteBuilder endpoints)
    {
        DataSourceProfileEndpoints.Map(endpoints.MapGroup("/api/datasourceprofiles"));
        var sources = endpoints.MapGroup("/api/datasources");
        DataSourceEndpoints.Map(sources);
        DataSourceAcquisitionEndpoints.Map(sources, endpoints.MapGroup("/api/datasourceacquisitions"));
        return endpoints;
    }
}
