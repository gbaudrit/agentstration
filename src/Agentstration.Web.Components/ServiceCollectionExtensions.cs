using Agentstration.Web.Components.State;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationWebComponents(this IServiceCollection services)
    {
        services.AddScoped<NavigationState>();
        services.AddScoped<UserPreferencesState>();
        services.AddScoped<NotificationState>();
        services.AddScoped<PlatformStatusState>();
        return services;
    }
}

