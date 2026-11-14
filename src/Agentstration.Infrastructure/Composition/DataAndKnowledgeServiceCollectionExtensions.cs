using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Infrastructure.DataSources;
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
        services.AddSingleton<Agentstration.Flows.IFlowCompositionResolver, SourceFlowCompositionResolver>();
        services.AddSingleton<Agentstration.Infrastructure.Knowledge.SourceHistoryMaintenance>();
        services.AddSingleton<Agentstration.Artifacts.IArtifactMaterializationAdmission>(provider => provider.GetRequiredService<Agentstration.Infrastructure.Knowledge.SourceHistoryMaintenance>());
        services.AddSingleton<IDataSourceHistoryMaintenance>(provider => provider.GetRequiredService<Agentstration.Infrastructure.Knowledge.SourceHistoryMaintenance>());
        services.AddSingleton<IKnowledgeHistoryMaintenance>(provider => provider.GetRequiredService<Agentstration.Infrastructure.Knowledge.SourceHistoryMaintenance>());
        services.AddSingleton<DataSourceProfileService>();
        services.AddSingleton<DataSourceManagementService>();
        services.AddSingleton<DataSourceDatasetCompositionService>();
        services.AddSingleton<DataSourceDocumentStorage>();
        services.AddSingleton<DataSourceDatasetAcquisitionService>();
        services.AddSingleton<Agentstration.Storage.Contracts.IStorageUsageProvider, DataSourceStorageUsageProvider>();
        services.AddSingleton<DataSourceAcquisitionService>();
        services.AddSingleton<DataSourceDocumentAccessService>();
        services.AddSingleton<DataSourceQueryService>();
        services.AddSingleton<DataSourceQueryToolService>();
        services.AddSingleton<IDataSourceQueryToolResolver, DataSourceQueryToolResolver>();
        services.AddSingleton<DataSourceDocumentToolExposureService>();
        services.AddSingleton<IDataSourceDocumentCursorProtector, DataSourceDocumentCursorProtector>();
        services.AddSingleton<IDataSourceDocumentToolResolver, DataSourceDocumentToolResolver>();
        services.AddSingleton<DataSourceDiscoveryService>();
        services.AddSingleton<DataSourceAcquisitionTargetService>();
        services.AddSingleton<KnowledgeSourceManagementService>();
        services.AddSingleton<KnowledgeProjectionService>();
        if (enableHostedServices) services.AddHostedService<KnowledgeProjectionWorker>();
        services.AddSingleton<IKnowledgeOutputPublisher, KnowledgeOutputPublisher>();
        services.AddSingleton<Agentstration.Storage.Contracts.IStorageUsageProvider, KnowledgeStorageUsageProvider>();
        services.AddSingleton<KnowledgeRetrievalService>();
        services.AddSingleton<KnowledgeAccessService>();
        services.AddSingleton<KnowledgeAccessTokenProtector>();
        services.AddSingleton<IKnowledgeAccessTokenProtector>(provider => provider.GetRequiredService<KnowledgeAccessTokenProtector>());
        services.AddSingleton<IKnowledgeAccessLifetime>(provider => provider.GetRequiredService<KnowledgeAccessTokenProtector>());
        services.AddSingleton<IKnowledgeSnapshotContentReader, KnowledgeSnapshotContentReader>();
        services.AddSingleton<IKnowledgeAccessToolResolver, KnowledgeAccessToolResolver>();
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
