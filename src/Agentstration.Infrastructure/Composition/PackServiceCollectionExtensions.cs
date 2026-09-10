using Agentstration.Infrastructure.Packs;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class PackServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationPacks(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        services.AddSingleton<IPackArchiveReader, ZipPackArchiveReader>();
        services.AddSingleton<IPackArtifactStore>(_ =>
            new FileSystemPackArtifactStore(Path.Combine(context.DataDirectory, "pack-artifacts")));
        services.AddSingleton<IPackResourceHandler, ModelProviderPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, RuntimeProfilePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, ModelProfilePackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, AgentPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, FlowPackResourceHandler>();
        services.AddSingleton<IPackResourceHandler, EntryPackResourceHandler>();
        services.AddSingleton<IPackWorkspaceResourceCatalog, WorkspacePackResourceCatalog>();
        services.AddSingleton<PackManagementService>();
        services.AddSingleton<PackAuthoringService>();
        services.AddSingleton<PackCompositionService>();
        return services;
    }
}
