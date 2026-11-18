using Agentstration.Extensions.Aep;
using Agentstration.Identity;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Packs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class BootstrapServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationBootstrapHandlers(this IServiceCollection services)
    {
        services.TryAddSingleton<AepEnrollmentPolicyOptions>();
        services.AddSingleton<ExtensionEnrollmentPolicyService>();
        services.AddSingleton<IBootstrapExtensionReferenceResolver, BootstrapExtensionReferenceResolver>();
        services.AddSingleton<IBootstrapContributionResourceResolver, BootstrapToolContributionResolver>();
        services.AddSingleton<IBootstrapContributionResourceResolver, BootstrapConnectedServiceTypeContributionResolver>();
        services.AddSingleton<IBootstrapContributionResourceResolver, BootstrapStorageTypeContributionResolver>();
        services.AddSingleton<IBootstrapContributionResourceResolver, BootstrapDataSourceProfileContributionResolver>();
        services.AddScoped<IBootstrapResourceHandler, ExtensionEnrollmentPolicyBootstrapHandler>();
        services.AddScoped<IBootstrapResourceHandler, ResourceAccessPolicyBootstrapHandler>();
        services.AddScoped<IBootstrapResourceHandler, TenantBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, WorkspaceBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, PrincipalDefaultContextBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, PackInstallationBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ParameterBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ConnectedServiceTypeBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, StorageTypeBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, FunctionKindBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, TransformationFunctionBootstrapResourceHandler>();
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
