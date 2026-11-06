using Agentstration.Infrastructure.Packs;
using Agentstration.Packs;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class PacksServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationPacks(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var dataDirectory = context.Options.DataDirectory;
        services.AddSingleton<IPackArchiveReader, ZipPackArchiveReader>();
        services.AddSingleton<IPackArtifactStore>(_ => new FileSystemPackArtifactStore(Path.Combine(dataDirectory, "pack-artifacts")));
        services.AddSingleton<IPackResourceHandler, ModelProviderPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, ParameterPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, RuntimeProfilePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, ModelProfilePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, ToolSetPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, AgentPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, FlowPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, DataSourceProfilePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, DataSourcePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, KnowledgeSourcePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, EntryPackResourceHandler>();
        services.AddSingleton<IPackWorkspaceResourceCatalog, WorkspacePackResourceCatalog>();
        services.AddSingleton<PackManagementService>();
        services.AddSingleton<PackAuthoringService>();
        services.AddSingleton<PackCompositionService>();
        services.AddSingleton<PackSourceCatalogHandler>();
        services.AddSingleton<ISourceCatalogHandler>(provider => provider.GetRequiredService<PackSourceCatalogHandler>());
        services.AddSingleton<PackSourceInstallationService>();
        return services;
    }
}
