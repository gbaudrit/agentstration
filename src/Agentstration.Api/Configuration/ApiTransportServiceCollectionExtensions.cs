using Agentstration.Agents.Api;
using Agentstration.Bootstrap.Api;
using Agentstration.Extensions.Api;
using Agentstration.Flows.Api;
using Agentstration.Flows.Application;
using Agentstration.Identity.Api;
using Agentstration.Infrastructure.Flows;
using Agentstration.Models.Api;
using Agentstration.Packs.Api;
using Agentstration.Resources.Api;
using Agentstration.Runtime.Api;
using Agentstration.Secrets.Api;
using Agentstration.Sources.Api;
using Agentstration.Tools.Api;
using Agentstration.Triggers.Api;
using Agentstration.Web.Api;
using Agentstration.Web.Features.Flows;
using Agentstration.Web.Hosting;
using Agentstration.Work.Api;
using Agentstration.Workplace.Api;

namespace Agentstration.Web.Configuration;

public static class ApiTransportServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationApi(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddProblemDetails();
        services.AddIdentityApi(configuration, environment);
        services.AddBootstrapApi();
        services.AddAgentsApi();
        services.AddExtensionsApi();
        services.AddModelsApi();
        services.AddSecretsApi();
        services.AddTriggersApi();
        services.AddAgentstrationOpenApi();
        services.AddSignalR();
        services.AddFlowsApi();
        services.AddRuntimeApi();
        services.AddResourcesApi();
        services.AddPacksApi();
        services.AddSourcesApi();
        services.AddToolsApi();
        services.AddWorkApi();
        services.AddWorkplaceApi();
        services.AddSingleton<BootstrapProfileCatalog>();
        services.AddSingleton<SourceBootstrapProfileLoader>();
        services.AddSingleton<BootstrapApplicationLock>();
        services.AddScoped<BootstrapProfileManagementService>();
        services.AddScoped<IBootstrapProfileApiService>(provider => provider.GetRequiredService<BootstrapProfileManagementService>());
        services.AddScoped<SourceConsoleManagementService>();
        services.AddScoped<ISourceConsoleQueryService>(provider => provider.GetRequiredService<SourceConsoleManagementService>());
        services.AddScoped<IResourceScopeApiService, ResourceScopeApiService>();
        services.AddSingleton<WorkplaceFlowConversationProjectionSink>();
        services.AddSingleton<IFlowRunEventSink>(provider => new CompositeFlowRunEventSink(
        [
            provider.GetRequiredService<WorkplaceFlowConversationProjectionSink>(),
            provider.GetRequiredService<SignalRFlowRunEventSink>()
        ]));
        return services;
    }

}
