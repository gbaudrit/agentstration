using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Infrastructure.Storage;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class StorageResourcesServiceCollectionExtensions
{
    internal static IServiceCollection AddStorageResources(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.AddSingleton<StorageTypeManagementService>();
        services.AddSingleton<StorageManagementService>();
        services.AddSingleton<IStorageResolver>(provider => provider.GetRequiredService<StorageManagementService>());
        services.AddSingleton<IConnectedServiceUsageProvider, StorageConnectedServiceUsageProvider>();
        services.AddSingleton<IStorageTypeAvailabilityProvider, StorageTypeAvailabilityProvider>();
        services.AddSingleton<StoragePlatformResourceProvisioner>();
        services.AddSingleton<LocalStorageDefaultsProvisioner>();
        services.AddSingleton<LocalStoragePaths>();
        services.AddSingleton<LocalStorageObjectProvider>();
        services.AddSingleton<IStorageDefinitionValidator, LocalStorageDefinitionValidator>();
        services.AddSingleton<StorageAspRemoteAdapter>();
        services.AddSingleton<IStorageContentGateway, StorageContentGateway>();
        services.AddSingleton<IAepEnrollmentResourceProvisioner, AepStorageTypeProvisioner>();
        return services;
    }
}
