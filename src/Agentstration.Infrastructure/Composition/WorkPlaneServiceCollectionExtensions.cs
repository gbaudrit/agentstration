using Agentstration.Application.Work;
using Agentstration.Coding;
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
        services.AddSingleton<WorkTaskTreeService>();
        services.AddSingleton<WorkPlanService>();
        services.AddSingleton<WorkProjectService>();
        services.AddSingleton<WorkProjectSharingService>();
        services.AddSingleton<IWorkPlanAdmissionGate, WorkProjectSharingAdmissionGate>();
        services.AddSingleton<IWorkProjectSharingGateway, GitWorkProjectSharingGateway>();
        services.AddSingleton<WorkTaskProviderService>();
        services.AddSingleton<CodexHarnessMaterializer>();
        services.AddSingleton<HarnessProfileService>();
        services.AddSingleton<CodingFlowConfigurationService>();
        services.AddSingleton<IFlowRuntimeDefinitionResolver>(provider => provider.GetRequiredService<CodingFlowConfigurationService>());
        services.AddSingleton<ICodingRuntimeProfileResolver, CodingRuntimeProfileResolver>();
        services.AddSingleton<CodingExecutionService>();
        services.AddSingleton<IWorkPlanAdmissionGate>(provider => provider.GetRequiredService<CodingExecutionService>());
        services.AddSingleton<Agentstration.Runtime.Abstractions.IRuntimeCapabilityMaterialProvider>(provider => provider.GetRequiredService<CodingExecutionService>());
        services.AddSingleton<CodingRepositoryService>();
        services.AddSingleton<CodingIssueCoordinationService>();
        services.AddSingleton<ICodingIssueBindingPolicy, CodingIssueBindingPolicy>();
        services.AddSingleton<IWorkPlanAdmissionGate, CodingIssueAdmissionGate>();
        services.AddSingleton<ICodingProviderGateway, McpCodingProviderGateway>();
        services.AddSingleton<IWorkTaskProviderGateway, McpWorkTaskProviderGateway>();
        services.AddSingleton<WorkProjectContextService>();
        services.AddSingleton<IWorkProjectBindingResolver, WorkProjectBindingResolver>();
        services.AddSingleton<WorkPlanExecutionService>();
        services.AddSingleton<WorkTaskExecutionEvidenceService>();
        services.AddSingleton<IWorkPlanFlowObserver, WorkPlanFlowObserver>();
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
