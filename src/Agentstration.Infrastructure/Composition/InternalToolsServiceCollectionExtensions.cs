using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Assistant;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Notifications;
using Agentstration.Infrastructure.ResourcePlanning;
using Agentstration.Infrastructure.Tools;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Infrastructure;

internal static class InternalToolsServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationInternalTools(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var dataDirectory = context.Options.DataDirectory;
        services.AddSingleton<WorkNotificationMcpToolDefinitionProvider>();
        services.AddSingleton<IInternalMcpToolDefinitionProvider>(provider => provider.GetRequiredService<WorkNotificationMcpToolDefinitionProvider>());
        services.AddSingleton<WorkNotificationMcpTool>();
        services.AddSingleton(provider => new AssistantDocumentationCatalog(
            Path.Combine(provider.GetService<IHostEnvironment>()?.ContentRootPath ?? dataDirectory, "docs")));
        AddInternalTool<AssistantDocumentationMcpTool>(services);
        AddInternalTool<AssistantDiagnosticsMcpTool>(services);
        AddInternalTool<DateTimeMcpTool>(services);
        AddInternalTool<ConnectedServiceDiscoveryMcpTool>(services);
        services.AddSingleton<ConnectedServiceDiscoveryExecutor>();
        services.AddSingleton<ConnectedServiceBoundValues>();
        AddInternalTool<ConnectedServiceAcquisitionMcpTool>(services);
        services.AddSingleton<ConnectedServiceAcquisitionExecutor>();
        AddInternalTool<ResourcePlanCreateMcpTool>(services);
        AddInternalTool<ResourcePlanGetMcpTool>(services);
        AddInternalTool<ResourcePlanRefineMcpTool>(services);
        AddInternalTool<ResourcePlanSubmitMcpTool>(services);
        AddInternalTool<ResourcePlanMaterializeMcpTool>(services);
        AddInternalTool<ResourceChangeSetCreateMcpTool>(services);
        AddInternalTool<ResourceChangeSetValidateMcpTool>(services);
        AddInternalTool<ArtifactStagingCreateMcpTool>(services);
        AddInternalTool<ArtifactStagingWriteMcpTool>(services);
        AddInternalTool<ArtifactStagingReadMcpTool>(services);
        AddInternalTool<ArtifactStagingStatMcpTool>(services);
        AddInternalTool<ArtifactStagingDeleteMcpTool>(services);
        AddInternalTool<StagedArtifactCreateMcpTool>(services);
        AddInternalTool<StagedArtifactWriteMcpTool>(services);
        AddInternalTool<StagedArtifactSealMcpTool>(services);
        AddInternalTool<StagedArtifactInspectMcpTool>(services);
        AddInternalTool<StagedArtifactReadContentMcpTool>(services);
        AddInternalTool<DurableArtifactReadContentMcpTool>(services);
        AddInternalTool<StagedArtifactCreateLeaseMcpTool>(services);
        AddInternalTool<StagedArtifactHandoffMcpTool>(services);
        AddInternalTool<StagedArtifactManageRetentionMcpTool>(services);
        AddInternalTool<StagedArtifactPurgeMcpTool>(services);
        AddInternalTool<ArtifactStorageWriteMcpTool>(services);
        AddInternalTool<ArtifactStorageReadMcpTool>(services);
        AddInternalTool<DataSourceArtifactImportMcpTool>(services);
        AddInternalTool<WebFetchMcpTool>(services);
        AddInternalTool<RestGetMcpTool>(services);
        AddInternalTool<KnowledgeRetrievalSearchMcpTool>(services);
        AddInternalTool<KnowledgeRetrievalQueryMcpTool>(services);
        AddInternalTool<KnowledgeRetrievalReadMcpTool>(services);
        AddInternalTool<KnowledgeContentReadMcpTool>(services);
        AddInternalTool<KnowledgeContentRegexMcpTool>(services);
        AddInternalTool<KnowledgeContentStatusMcpTool>(services);
        services.AddSingleton<IInternalMcpToolHandler>(provider => provider.GetRequiredService<WorkNotificationMcpTool>());
        services.AddSingleton<InternalMcpToolProjectionService>();
        services.AddSingleton(provider => new Lazy<IEnumerable<IInternalMcpToolHandler>>(
            () => provider.GetServices<IInternalMcpToolHandler>()));
        return services;
    }

    private static void AddInternalTool<TTool>(IServiceCollection services) where TTool : class, IInternalMcpToolHandler
    {
        services.AddSingleton<TTool>();
        services.AddSingleton<IInternalMcpToolDefinitionProvider>(provider => provider.GetRequiredService<TTool>());
        services.AddSingleton<IInternalMcpToolHandler>(provider => provider.GetRequiredService<TTool>());
    }
}
