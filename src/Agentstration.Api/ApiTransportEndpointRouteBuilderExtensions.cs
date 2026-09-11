using Agentstration.Infrastructure;
using Agentstration.Web.Api;
using Agentstration.Web.Configuration;
using Agentstration.Web.Features.Flows;
using Agentstration.Web.Features.Workplace;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;

namespace Agentstration.Web;

public static class ApiTransportEndpointRouteBuilderExtensions
{
    public static WebApplication MapAgentstrationApi(this WebApplication app)
    {
        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
            app.MapAgentstrationOpenApi();

        app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
        app.MapGet("/health/ready", (IAgentstrationStorageInitializer storage) => storage.IsReady
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
        app.MapAgentstrationAuthentication();
        app.MapAgentstrationLocalAccountAdministration();
        app.MapAgentstrationIdentityApi();
        app.MapAgentstrationBootstrapProfiles();
        app.MapAgentstrationManagementApi();
        app.MapAgentstrationModelManagementApi();
        app.MapAgentstrationAepEnrollment();
        app.MapAgentstrationWorkApi();
        app.MapAgentstrationWorkplaceApi();
        app.MapAgentstrationWorkOperationsApi();
        app.MapAgentstrationFlowApi();
        app.MapAgentstrationRuntimeApi();
        app.MapAgentstrationToolGovernanceAuditApi();
        app.MapHub<FlowRunHub>("/hubs/flow-runs")
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        app.MapHub<WorkplaceHub>("/hubs/workplace")
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        if (app.Environment.IsDevelopment()) app.MapOllamaDiagnostics();
        app.MapMcp("/mcp").RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        return app;
    }
}
