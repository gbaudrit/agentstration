namespace Agentstration.Schemas.Api;

public static class SchemasApiModule
{
    public static IServiceCollection AddSchemasApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapSchemasApi(this IEndpointRouteBuilder endpoints)
    {
        SchemaEndpoints.Map(endpoints);
        return endpoints;
    }
}
