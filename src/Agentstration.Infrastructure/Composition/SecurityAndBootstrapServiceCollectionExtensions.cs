using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Packs;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Local;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SecurityAndBootstrapServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSecurityAndBootstrap(
        this IServiceCollection services,
        AgentstrationServiceRegistrationContext context)
    {
        var secretPath = Path.Combine(context.DataDirectory, "secrets");
        services.AddSingleton(_ => new EnvironmentMasterKeyProvider(Path.Combine(secretPath, "master.key")));
        services.AddSingleton<IMasterKeyProvider>(provider => provider.GetRequiredService<EnvironmentMasterKeyProvider>());
        services.AddSingleton<ISecretVaultProvider>(provider => new LocalSecretVaultProvider(
            secretPath,
            provider.GetRequiredService<IMasterKeyProvider>()));
        services.AddSingleton<ISecretVaultProvider, SharedKeyFileSecretVaultProvider>();
        services.AddSingleton<SecretManagementService>();
        services.AddSingleton<ISecretResolver>(provider => provider.GetRequiredService<SecretManagementService>());
        services.AddSingleton<IPrincipalResolver, ExternalIdentityPrincipalResolver>();
        services.AddSingleton<IInitialPrincipalProvisioner, InitialPrincipalProvisioner>();
        services.AddSingleton<IInitialTopologyProvisioner, InitialTopologyProvisioner>();
        services.AddSingleton<ILocalPrincipalProvisioner, LocalPrincipalProvisioner>();
        services.AddSingleton<IPlatformAuthorizationService, PlatformAuthorizationService>();
        services.AddSingleton<PlatformAdministratorLifecycleLock>();
        services.AddSingleton<PlatformAdministratorAdministrationService>();
        services.AddSingleton<IPlatformAdministratorPolicy>(provider => provider.GetRequiredService<PlatformAdministratorAdministrationService>());
        services.AddSingleton<ExternalIdentityLifecycleLock>();
        services.AddSingleton<ExternalIdentityAdministrationService>();
        services.AddSingleton<IAuthorizationService, PermissionAuthorizationService>();
        services.AddSingleton<SecurityAuditService>();
        services.AddSingleton<ISecurityAuditWriter>(provider => provider.GetRequiredService<SecurityAuditService>());
        services.AddSingleton<ILocalEnvironmentBootstrapper, LocalEnvironmentBootstrapper>();
        services.AddSingleton<IdentityAdministrationService>();
        services.AddScoped<IBootstrapResourceHandler, TenantBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, WorkspaceBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, PrincipalDefaultContextBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, PackInstallationBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ModelProviderBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, RuntimeProfileBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, ModelProfileBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, AgentBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, FlowBootstrapResourceHandler>();
        services.AddScoped<IBootstrapResourceHandler, EntryBootstrapResourceHandler>();
        services.AddSingleton<WorkspaceMembershipAdministrationService>();
        services.AddSingleton<IdentityExperienceService>();
        services.AddSingleton<PrincipalPreferencesService>();
        services.AddSingleton<PersonalAccessTokenService>();
        return services;
    }
}
