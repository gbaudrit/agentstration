using Agentstration.Agents.Api;
using Agentstration.Artifacts.Api;
using Agentstration.Bootstrap.Api;
using Agentstration.DataSources.Api;
using Agentstration.Extensions.Api;
using Agentstration.Flows.Api;
using Agentstration.Flows.Application;
using Agentstration.Identity.Api;
using Agentstration.Infrastructure.Flows;
using Agentstration.Knowledge.Api;
using Agentstration.Models.Api;
using Agentstration.Packs.Api;
using Agentstration.Parameters.Api;
using Agentstration.Resources.Api;
using Agentstration.Runtime.Api;
using Agentstration.Schemas.Api;
using Agentstration.Secrets.Api;
using Agentstration.Sources.Api;
using Agentstration.Tools.Api;
using Agentstration.Triggers.Api;
using Agentstration.Web.Api;
using Agentstration.Web.Features.Flows;
using Agentstration.Web.Hosting;
using Agentstration.Work.Api;
using Agentstration.Workplace.Api;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Web.Configuration;

public static class ApiTransportServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationApi(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddProblemDetails();
        services.AddArtifactsApi();
        services.AddIdentityApi(configuration, environment);
        services.AddKnowledgeApi();
        services.AddDataSourcesApi();
        services.AddBootstrapApi();
        services.AddAgentsApi();
        services.AddExtensionsApi();
        services.AddModelsApi();
        services.AddParametersApi();
        services.AddSchemasApi();
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
        services.AddAgentstrationBootstrapTransport();
        services.AddAgentstrationConsoleResourceQueries();
        services.AddAgentstrationRealtimeProjection();
        return services;
    }

    internal static IServiceCollection AddAgentstrationBootstrapTransport(this IServiceCollection services)
    {
        services.AddSingleton<BootstrapProfileCatalog>();
        services.AddSingleton<SourceBootstrapProfileLoader>();
        services.AddSingleton<BootstrapApplicationLock>();
        services.AddScoped<BootstrapProfileManagementService>();
        services.AddScoped<IBootstrapProfileApiService>(provider => provider.GetRequiredService<BootstrapProfileManagementService>());
        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleResourceQueries(this IServiceCollection services)
    {
        services.AddScoped<SourceConsoleManagementService>();
        services.AddScoped<ISourceConsoleQueryService>(provider => provider.GetRequiredService<SourceConsoleManagementService>());
        services.AddScoped<IResourceScopeApiService, ResourceScopeApiService>();
        return services;
    }

    internal static IServiceCollection AddAgentstrationRealtimeProjection(this IServiceCollection services)
    {
        services.AddSingleton<WorkplaceFlowConversationProjectionSink>();
        services.Replace(ServiceDescriptor.Singleton<IFlowRunEventSink>(provider => new CompositeFlowRunEventSink(
        [
            provider.GetRequiredService<WorkplaceFlowConversationProjectionSink>(),
            provider.GetRequiredService<SignalRFlowRunEventSink>()
        ])));
        return services;
    }

}
