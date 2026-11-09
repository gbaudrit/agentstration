using Agentstration.Identity;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Packs;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class BootstrapServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationBootstrapHandlers(this IServiceCollection services)
    {
        services.AddScoped<IBootstrapResourceHandler, TenantBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, WorkspaceBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, PrincipalDefaultContextBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, PackInstallationBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ParameterBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ConnectedServiceTypeBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, StorageTypeBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, StorageBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ConnectedServiceOperationTypeBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ModelProviderBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, RuntimeProfileBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ModelProfileBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ToolSetBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, AgentBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, FlowBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, DataSourceProfileBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, DataSourceBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, KnowledgeSourceBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, KnowledgeSourceToolExposureBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, EntryBootstrapResourceHandler>();
        return services;
    }
}
