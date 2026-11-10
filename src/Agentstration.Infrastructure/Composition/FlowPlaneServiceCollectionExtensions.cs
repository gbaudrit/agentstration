using Agentstration.Agents;
using Agentstration.Application.Work;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Tools;
using Agentstration.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class FlowPlaneServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationFlowPlane(this IServiceCollection services)
    {
        services.AddSingleton<IFlowScopeHierarchy, ResourceFlowScopeHierarchy>();
        services.AddSingleton<IFlowDiscoveryPolicy, FlowDiscoveryPolicy>();
        services.AddSingleton<IFlowUsePolicy, FlowUsePolicy>();
        services.AddSingleton<IFlowAgentReferenceResolver, FlowAgentReferenceResolver>();
        services.AddSingleton<DirectExecutionPayloadPersistence>();
        services.AddSingleton<Agentstration.Flows.Storage.Abstractions.IFlowPayloadPersistence>(provider => provider.GetRequiredService<DirectExecutionPayloadPersistence>());
        services.AddSingleton<Agentstration.Runtime.Abstractions.IRuntimeAssignmentPayloadPersistence>(provider => provider.GetRequiredService<DirectExecutionPayloadPersistence>());
        services.AddSingleton<FlowService>();
        services.AddSingleton<IToolDefinitionFlowResolver, ToolDefinitionFlowResolver>();
        services.AddSingleton<IFlowVersionActivationGuard, ToolDefinitionFlowActivationGuard>();
        services.AddSingleton<IFlowVersionActivationGuard, KnowledgeFlowActivationGuard>();
        services.AddSingleton<IFlowVersionActivationGuard, ArtifactTransformationFlowActivationGuard>();
        services.AddSingleton<IFlowVersionActivationGuard, DataSourceFlowActivationGuard>();
        services.AddSingleton<IFlowDeletionGuard, ToolDefinitionFlowDeletionGuard>();
        services.AddSingleton<IFlowDeletionGuard, KnowledgeFlowDeletionGuard>();
        services.AddSingleton<IFlowDeletionGuard>(provider => provider.GetRequiredService<EntryResourceDeletionGuard>());
        services.AddSingleton<IFlowRunDeletionGuard, KnowledgeSnapshotFlowRunDeletionGuard>();
        services.AddSingleton<IFlowRunQueue, AwpFlowRunQueue>();
        services.AddSingleton<IFlowRunCancellationRegistry, LocalFlowRunCancellationRegistry>();
        services.AddSingleton<WorkspaceFlowRunExecutionScope>();
        services.AddSingleton<IFlowRunExecutionScope>(provider => provider.GetRequiredService<WorkspaceFlowRunExecutionScope>());
        // HTTP request and ambient Flow scopes are complementary, ordered contributions.
        services.AddSingleton<IWorkExecutionScopeAccessor, CurrentWorkExecutionScopeAccessor>();
        services.AddSingleton<IWorkExecutionScopeAccessor>(provider => provider.GetRequiredService<WorkspaceFlowRunExecutionScope>());
        services.AddSingleton<IRootFlowTargetResolver, RootFlowTargetResolver>();
        services.AddSingleton<IRootFlowRunGateway, RootFlowRunGateway>();
        services.AddSingleton<IRootFlowSubmissionAuthorizer, RootFlowSubmissionAuthorizer>();
        services.TryAddSingleton<IFlowRunEventSink, NullFlowRunEventSink>();
        services.AddSingleton<FlowRevisionRetentionService>();
        services.AddSingleton<IAgentRevisionRunRetention, AgentRevisionRunRetention>();
        services.AddSingleton<IFlowResourceReferenceResolver, ManagementFlowResourceReferenceResolver>();
        services.AddSingleton<FlowExpressionParser>();
        services.AddSingleton<IExpressionParser>(provider => provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IExpressionValidator>(provider => provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IExpressionEvaluator>(provider => provider.GetRequiredService<FlowExpressionParser>());
        services.AddSingleton<IFlowDefinitionValidator, FlowGraphValidator>();
        services.AddSingleton<IFlowStepArtifactCapture, FlowStepArtifactCapture>();
        services.AddSingleton<FlowDraftService>();
        services.AddSingleton<FlowRunService>();
        services.AddSingleton<RootFlowSubmissionService>();
        return services;
    }
}
