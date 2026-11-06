using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Local;
using Agentstration.Security.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SecurityServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSecurity(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var dataDirectory = context.Options.DataDirectory;
        var secretPath = Path.Combine(dataDirectory, "secrets");
        services.AddSingleton(_ => new EnvironmentMasterKeyProvider(Path.Combine(secretPath, "master.key")));
        services.AddSingleton<IMasterKeyProvider>(provider => provider.GetRequiredService<EnvironmentMasterKeyProvider>());
        services.AddSingleton<ISecretVaultProvider>(provider => new LocalSecretVaultProvider(
            secretPath,
            provider.GetRequiredService<IMasterKeyProvider>()));
        services.AddSingleton<ISecretVaultProvider, SharedKeyFileSecretVaultProvider>();
        services.AddSingleton<DescendantResourceUseAuthorizer>();
        services.AddSingleton<IResourceIdentityContributor, FlowResourceIdentityContributor>();
        services.AddSingleton<IResourceIdentityCatalog, ResourceIdentityCatalog>();
        services.AddSingleton<ResourceAccessPolicyService>();
        services.AddSingleton<IResourceAccessPolicyService>(provider => provider.GetRequiredService<ResourceAccessPolicyService>());
        services.AddSingleton<ParameterManagementService>();
        services.AddSingleton<IParameterResolver>(provider => provider.GetRequiredService<ParameterManagementService>());
        services.AddSingleton<ConnectedServiceManagementService>();
        services.AddSingleton<IConnectedServiceResolver>(provider => provider.GetRequiredService<ConnectedServiceManagementService>());
        services.AddSingleton<IParameterUsageProvider, ConnectedServiceParameterUsageProvider>();
        services.AddSingleton<SecretManagementService>();
        services.AddSingleton<ISecretResolver>(provider => provider.GetRequiredService<SecretManagementService>());
        services.AddSingleton<ISecretAccessAuthorizer>(provider => provider.GetRequiredService<SecretManagementService>());
        services.AddSingleton<ISecretCapabilityService, SecretCapabilityService>();
        services.AddSingleton<IPrincipalResolver, ExternalIdentityPrincipalResolver>();
        services.AddSingleton<IInitialPrincipalProvisioner, InitialPrincipalProvisioner>();
        services.AddSingleton<IInitialTopologyProvisioner, InitialTopologyProvisioner>();
        services.AddSingleton<IOperationalTopologyProvisioner, OperationalTopologyProvisioner>();
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
        services.AddSingleton<WorkspaceMembershipAdministrationService>();
        services.AddSingleton<IdentityExperienceService>();
        services.AddSingleton<PrincipalPreferencesService>();
        services.AddSingleton<PersonalAccessTokenService>();
        return services;
    }
}
