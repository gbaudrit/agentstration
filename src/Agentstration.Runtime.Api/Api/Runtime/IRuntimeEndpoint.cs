namespace Agentstration.Web.Api.Runtime;

internal interface IRuntimeEndpoint
{
    static abstract void Map(RouteGroupBuilder group);
}
