using Agentstration.Agents.Api;
using Agentstration.Bootstrap.Api;
using Agentstration.Extensions.Api;
using Agentstration.Flows.Api;
using Agentstration.Identity.Api;
using Agentstration.Infrastructure;
using Agentstration.Models.Api;
using Agentstration.Packs.Api;
using Agentstration.ResourcePlanning.Api;
using Agentstration.Resources.Api;
using Agentstration.Runtime.Api;
using Agentstration.Secrets.Api;
using Agentstration.Sources.Api;
using Agentstration.Tools.Api;
using Agentstration.Triggers.Api;
using Agentstration.Web.Configuration;
using Agentstration.Work.Api;
using Agentstration.Workplace.Api;

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
        app.MapIdentityApi();
        app.MapBootstrapApi();
        app.MapAgentsApi();
        app.MapTriggersApi();
        app.MapSourcesApi();
        app.MapModelsApi();
        app.MapPacksApi();
        app.MapSecretsApi();
        app.MapToolsApi();
        app.MapResourcesApi();
        app.MapResourcePlanningApi();
        app.MapExtensionsApi();
        app.MapWorkApi();
        app.MapWorkplaceApi();
        app.MapFlowsApi();
        app.MapRuntimeApi();
        if (app.Environment.IsDevelopment()) app.MapModelDiagnostics();
        return app;
    }
}
