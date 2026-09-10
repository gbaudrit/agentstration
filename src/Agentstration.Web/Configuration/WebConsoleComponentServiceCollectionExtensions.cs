using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.DependencyInjection;
using Agentstration.Web.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Web.Configuration;

internal static class WebConsoleComponentServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationConsoleComponents(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddAgentstrationWebComponents();
        services.AddScoped<IConsoleContextProvider, ConsoleContextProvider>();
        services.AddScoped<IResourceSearchProvider, ConsoleResourceSearchProvider>();
        services.AddAgentstrationFlowDesigner();
        services.AddScoped<PlatformDashboardService>();
        services.AddScoped<SourceConsoleManagementService>();
        services.AddScoped<IFlowDesignerBackend, FlowDesignerBackend>();
        services.AddScoped<IFlowDesignerResourceProvider, FlowDesignerResourceProvider>();
        return services;
    }
}
