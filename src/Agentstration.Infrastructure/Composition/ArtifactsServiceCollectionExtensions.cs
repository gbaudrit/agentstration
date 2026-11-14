using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Storage;
using Agentstration.Storage.Contracts;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class ArtifactsServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationArtifacts(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var dataDirectory = context.Options.DataDirectory;
        var enableHostedServices = context.Options.EnableHostedServices;
        services.AddSingleton<IArtifactStore>(_ => new FileSystemArtifactStore(Path.Combine(dataDirectory, "artifacts")));
        services.AddSingleton<IArtifactContentStore>(_ => new FileSystemStagedArtifactContentStore(Path.Combine(dataDirectory, "staged-artifacts")));
        services.AddSingleton(_ => new FileSystemDurableArtifactStore(Path.Combine(dataDirectory, "durable-artifacts")));
        services.AddSingleton<StorageMaterializedArtifactReader>();
        services.AddSingleton<WorkspaceArtifactStorage>();
        services.AddSingleton<IArtifactDurableStore>(provider => provider.GetRequiredService<WorkspaceArtifactStorage>());
        services.AddSingleton<IStorageUsageProvider, ArtifactStorageUsageProvider>();
        services.AddSingleton<IArtifactStagingToolExecutor, ToolSetArtifactStagingExecutor>();
        services.AddSingleton<ArtifactManagementService>();
        services.AddSingleton<ArtifactProcessingService>();
        services.AddSingleton<IStoredArtifactRegistrar>(provider => provider.GetRequiredService<ArtifactManagementService>());
        services.AddSingleton<IArtifactContentMaterializationGateway, ArtifactContentMaterializationGateway>();
        services.AddSingleton<DurableArtifactContentReader>();
        services.AddSingleton<StoredArtifactReconciliationService>();
        services.AddSingleton<ArtifactPlatformResourceProvisioner>();
        services.AddSingleton<KnowledgePlatformResourceProvisioner>();
        services.AddSingleton(new StagedArtifactCleanupOptions());
        if (enableHostedServices) services.AddHostedService<StagedArtifactCleanupWorker>();
        return services;
    }
}
