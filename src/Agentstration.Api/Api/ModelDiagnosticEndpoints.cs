using Agentstration.Web.Api.Diagnostics;

namespace Agentstration.Web;

public static class ModelDiagnosticEndpoints
{
    public static IEndpointRouteBuilder MapOllamaDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        OllamaChatDiagnosticEndpoint.Map(endpoints);
        return endpoints;
    }
}
