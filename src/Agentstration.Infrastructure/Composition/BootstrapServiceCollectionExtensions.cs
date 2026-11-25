using Agentstration.Coding.Bootstrap;
using Agentstration.Extensions.Aep;
using Agentstration.Identity;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Packs;
using Agentstration.Infrastructure.Sources;
using Agentstration.Sources;
using Agentstration.Tools.Bootstrap;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class BootstrapServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationBootstrapHandlers(this IServiceCollection services)
    {
        services.AddScoped<Agentstration.Bootstrap.Contracts.IBootstrapStartupModelCorrections, BootstrapStartupModelCorrectionService>();
        services.AddHttpClient(WorkspaceSeedService.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Agentstration-Workspace-Seed/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => HttpSourceRegistryDocumentRetriever.CreatePrimaryHandler(
            new SourceRegistryTransportOptions { ConnectTimeoutSeconds = 5 }));
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
        services.AddScoped<BootstrapResourcePreparation>();
        services.AddScoped<IBootstrapResourceHandler, VaultBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, SecretBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, SchemaBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ConnectedServiceBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ConnectedServiceDiscoveryBootstrapHandler>();
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
        services.AddScoped<IBootstrapResourceHandler, ProjectBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ProjectSharingBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ToolDefinitionBootstrapHandler>();
        services.AddScoped<IBootstrapResourceHandler, HarnessNativeConfigurationBootstrapHandler>();
        services.AddScoped<IBootstrapResourceHandler, HarnessProfileBootstrapHandler>();
        return services;
    }
}
