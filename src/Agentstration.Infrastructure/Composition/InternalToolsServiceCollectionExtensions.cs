using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.DataSources;
using Agentstration.Infrastructure.Assistant;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Notifications;
using Agentstration.Infrastructure.ResourcePlanning;
using Agentstration.Infrastructure.Storage;
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
        services.AddSingleton<BuiltinHttpOperationExecutor>();
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
        foreach (var operation in Agentstration.Runtime.Abstractions.RuntimeArtifactProcessingTools.Create())
        {
            services.AddSingleton<IInternalMcpToolHandler>(provider => new ArtifactProcessingContentTool(operation,
                provider.GetRequiredService<ArtifactProcessingService>(), provider.GetRequiredService<Agentstration.Flows.Storage.Abstractions.IFlowRepository>(),
                provider.GetRequiredService<Agentstration.Identity.Contracts.ICurrentRequestContext>()));
            services.AddSingleton<IInternalMcpToolDefinitionProvider>(_ => new ArtifactProcessingToolDefinition(operation));
        }
        AddInternalTool<DataSourceArtifactImportMcpTool>(services);
        AddInternalTool<WebFetchMcpTool>(services);
        AddInternalTool<RestGetMcpTool>(services);
        AddInternalTool<KnowledgeRetrievalSearchMcpTool>(services);
        AddInternalTool<KnowledgeRetrievalQueryMcpTool>(services);
        AddInternalTool<KnowledgeRetrievalReadMcpTool>(services);
        AddInternalTool<StorageDocumentGetMcpTool>(services);
        AddInternalTool<StorageDocumentPutMcpTool>(services);
        AddInternalTool<StorageDocumentDeleteMcpTool>(services);
        AddInternalTool<StorageDocumentQueryMcpTool>(services);
        services.AddSingleton<IInternalMcpToolDefinitionProvider, DataSourceDocumentQueryDefinitionProvider>();
        services.AddSingleton<IInternalMcpToolHandler, DataSourceDocumentQueryMcpTool>();
        AddInternalTool<KnowledgeDocumentQueryMcpTool>(services);
        AddInternalTool<KnowledgeContentReadMcpTool>(services);
        AddInternalTool<KnowledgeContentRegexMcpTool>(services);
        AddInternalTool<KnowledgeContentStatusMcpTool>(services);
        services.AddSingleton<IInternalMcpToolHandler>(provider => provider.GetRequiredService<WorkNotificationMcpTool>());
        services.AddSingleton<InternalMcpToolProjectionService>();
        services.AddSingleton(provider => new Lazy<IEnumerable<IInternalMcpToolHandler>>(
            () => provider.GetServices<IInternalMcpToolHandler>()));
        return services;
    }

    private sealed class ArtifactProcessingToolDefinition(Agentstration.Runtime.Abstractions.RuntimeExecutionToolMaterial operation)
        : IInternalMcpToolDefinitionProvider
    {
        public InternalMcpToolDefinition Definition { get; } = new("artifact.processing." + operation.ExternalId,
            operation.Name, operation.Description, operation.InputSchema, operation.OutputSchema,
            InitialCategory: new("artifact-processing", "Artifact processing", "Content operations restricted to the current Artifact processing execution."),
            ExposeThroughMcp: false);
    }

    private static void AddInternalTool<TTool>(IServiceCollection services) where TTool : class, IInternalMcpToolHandler
    {
        services.AddSingleton<TTool>();
        services.AddSingleton<IInternalMcpToolDefinitionProvider>(provider => provider.GetRequiredService<TTool>());
        services.AddSingleton<IInternalMcpToolHandler>(provider => provider.GetRequiredService<TTool>());
    }
}
