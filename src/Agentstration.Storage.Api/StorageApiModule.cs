namespace Agentstration.Storage.Api;

public static class StorageApiModule
{
    public static IServiceCollection AddStorageApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapStorageApi(this IEndpointRouteBuilder endpoints)
    {
        StorageEndpoints.Map(endpoints);
        StorageDocumentEndpoints.Map(endpoints);
        return endpoints;
    }
}
