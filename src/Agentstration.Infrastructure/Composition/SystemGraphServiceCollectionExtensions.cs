using Agentstration.Agents;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.SystemGraph;
using Agentstration.Models;
using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class SystemGraphServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationSystemGraph(this IServiceCollection services)
    {
        services.AddSingleton<ISystemGraphContributor, AgentSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, ModelSystemGraphContributor>();
        services.AddSingleton<ISystemGraphContributor, FlowSystemGraphContributor>();
        services.AddSingleton<ISystemGraphReadScope, SystemGraphReadScope>();
        services.AddSingleton<ISystemGraphAuthorization, SystemGraphAuthorization>();
        services.AddSingleton<ISystemGraphQueryService, SystemGraphQueryService>();
        return services;
    }
}
