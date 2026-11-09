using Agentstration.DataSources;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Sources;
using Agentstration.Knowledge;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class DataAndKnowledgeServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationDataAndKnowledge(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var enableHostedServices = context.Options.EnableHostedServices;
        services.AddSingleton<DataSourceProfileService>();
        services.AddSingleton<DataSourceManagementService>();
        services.AddSingleton<DataSourceDatasetCompositionService>();
        services.AddSingleton<DataSourceDatasetAcquisitionService>();
        services.AddSingleton<Agentstration.Storage.Contracts.IStorageUsageProvider, DataSourceStorageUsageProvider>();
        services.AddSingleton<DataSourceAcquisitionService>();
        services.AddSingleton<DataSourceDiscoveryService>();
        services.AddSingleton<DataSourceAcquisitionTargetService>();
        services.AddSingleton<KnowledgeSourceManagementService>();
        services.AddSingleton<KnowledgeProjectionService>();
        services.AddSingleton<IKnowledgeOutputPublisher, KnowledgeOutputPublisher>();
        services.AddSingleton<Agentstration.Storage.Contracts.IStorageUsageProvider, KnowledgeStorageUsageProvider>();
        services.AddSingleton<KnowledgeRetrievalService>();
        services.AddHttpClient<IHttpContentFetcher, SafeHttpContentFetcher>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Agentstration-DataSources/1.0");
        }).ConfigurePrimaryHttpMessageHandler(SafeHttpContentFetcher.CreatePrimaryHandler);
        services.AddSingleton<IKnowledgeFlowResolver, KnowledgeFlowResolver>();
        services.AddSingleton<IDataSourceAcquisitionCompositionResolver, DataSourceAcquisitionCompositionResolver>();
        services.AddSingleton<IDataSourceAcquisitionFlowGateway, DataSourceAcquisitionFlowGateway>();
        services.AddSingleton<IDataSourceArtifactReferenceValidator, DataSourceArtifactReferenceValidator>();
        services.AddScoped<AepDataSourceProfileBundleInstaller>();
        services.AddSingleton<IKnowledgeProjectionInputResolver, KnowledgeProjectionInputResolver>();
        services.AddSingleton<IKnowledgeProjectionFlowGateway, KnowledgeProjectionFlowGateway>();
        services.AddSingleton<IKnowledgeRetrievalFlowGateway, KnowledgeRetrievalFlowGateway>();
        services.AddSingleton<IKnowledgeArtifactReferenceValidator, KnowledgeArtifactReferenceValidator>();
        services.AddSingleton<IKnowledgeSnapshotArtifactResolver, KnowledgeSnapshotArtifactResolver>();
        services.AddSingleton<KnowledgeSnapshotService>();
        if (enableHostedServices) services.AddHostedService<DataSourceAcquisitionRecoveryWorker>();
        services.AddSingleton<KnowledgeSourceToolExposureService>();
        return services;
    }
}
