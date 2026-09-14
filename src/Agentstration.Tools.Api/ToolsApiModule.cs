using Agentstration.Web.Api.Models;
using Agentstration.Web.Security;
using ModelContextProtocol.AspNetCore;

namespace Agentstration.Tools.Api;

public static class ToolsApiModule
{
    public static IServiceCollection AddToolsApi(this IServiceCollection services)
    {
        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly()
            .WithListToolsHandler(Agentstration.Web.Api.AgentstrationMcpHandlers.ListToolsAsync)
            .WithCallToolHandler(Agentstration.Web.Api.AgentstrationMcpHandlers.CallToolAsync);
        return services;
    }

    public static IEndpointRouteBuilder MapToolsApi(this IEndpointRouteBuilder endpoints)
    {
        ToolProviderEndpoints.Map(endpoints);
        ToolDefinitionEndpoints.Map(endpoints.MapGroup("/api/tooldefinitions"));
        ToolExecutionHookEndpoints.Map(endpoints.MapGroup("/api/toolexecutionhooks"));
        Agentstration.Web.ToolGovernanceAuditEndpoints.MapAgentstrationToolGovernanceAuditApi(endpoints);
        endpoints.MapMcp("/mcp").RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        return endpoints;
    }
}
