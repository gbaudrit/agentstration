using Agentstration.Application.Work;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Work;
using Agentstration.ResourceManagement;
using Agentstration.Runtime.Local;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure;

internal static class WorkPlaneServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationWorkPlane(this IServiceCollection services)
    {
        services.AddSingleton<LocalWorkExecutionGateway>();
        services.AddSingleton<IWorkExecutionGateway>(provider => provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<ILocalWorkExecutionQueue>(provider => provider.GetRequiredService<LocalWorkExecutionGateway>());
        services.AddSingleton<WorkItemService>();
        services.AddSingleton<WorkplaceService>();
        services.AddSingleton<IEntryDiscoveryAuthorization, EntryDiscoveryAuthorization>();
        services.AddSingleton<EntryDiscoveryService>();
        services.AddSingleton<IWorkOperationsQueryService, WorkOperationsQueryService>();
        services.AddSingleton<WorkTaskDeletionService>();
        services.AddSingleton<IWorkTaskEventSink, WorkplaceProjectionSink>();
        services.AddSingleton<IEntryExecutionResolver, EntryExecutionResolver>();
        services.AddSingleton<IEntryTargetResolver, EntryTargetResolver>();
        services.AddSingleton<EntryResourceDeletionGuard>();
        services.AddSingleton<IResourceDeletionGuard>(provider => provider.GetRequiredService<EntryResourceDeletionGuard>());
        services.AddSingleton<EntryAdministrationService>();
        services.AddSingleton<IWorkplaceContext, CurrentWorkplaceContext>();
        services.AddSingleton<DashboardAdministrationService>();
        services.AddSingleton<IFlowInputRequestSink, WorkplaceFlowInputProjectionSink>();
        services.AddSingleton<IWorkplaceExternalInputResponder, WorkplaceFlowInputResponder>();
        return services;
    }
}
