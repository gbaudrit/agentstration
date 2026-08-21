using Agentstration.Web.Components.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Web.Components;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationWebComponents(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<NavigationState>();
        services.AddScoped<UserPreferencesState>();
        services.AddScoped<NotificationState>();
        services.AddScoped<PlatformStatusState>();
        services.AddScoped<ConsoleContextState>();
        services.TryAddScoped<IConsoleContextProvider, EmptyConsoleContextProvider>();
        services.TryAddScoped<IResourceSearchProvider, EmptyResourceSearchProvider>();
        services.TryAddScoped<IUserPreferencesClient, EmptyUserPreferencesClient>();
        return services;
    }
}
