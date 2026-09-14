using Agentstration.Web;

namespace Agentstration.Work.Api;

public static class WorkApiModule
{
    public static IServiceCollection AddWorkApi(this IServiceCollection services) => services;
    public static IEndpointRouteBuilder MapWorkApi(this IEndpointRouteBuilder endpoints) => endpoints.MapAgentstrationWorkApi();
}
