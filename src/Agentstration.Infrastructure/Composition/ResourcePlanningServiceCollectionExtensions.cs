using Agentstration.Infrastructure.ResourcePlanning;
using Agentstration.ResourcePlanning;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class ResourcePlanningServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationResourcePlanning(this IServiceCollection services)
    {
        services.AddSingleton<IResourcePlanContentValidator, FunctionalResourcePlanValidator>();
        services.AddSingleton<ResourcePlanService>();
        services.AddSingleton(new ResourcePlanningMaterializationOptions());
        services.AddSingleton<IResourcePlanningStateReader, ManagementResourcePlanningStateReader>();
        services.AddSingleton<ResourcePlanMaterializationService>();
        services.AddSingleton<ResourceChangeSetService>();
        services.AddSingleton<IPlannedResourceValidator, CanonicalPlannedResourceValidator>();
        services.AddSingleton<ResourceChangeSetValidationService>();
        services.AddSingleton<IPlannedResourceApplier, CanonicalPlannedResourceApplier>();
        services.AddSingleton<ResourceChangeSetApplicationService>();
        return services;
    }
}
