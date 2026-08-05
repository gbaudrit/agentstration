using Agentstration.Web.FlowDesigner.State;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.FlowDesigner.DependencyInjection;

public static class FlowDesignerServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationFlowDesigner(this IServiceCollection services)
    {
        services.AddScoped<FlowEditorStore>();
        return services;
    }
}
