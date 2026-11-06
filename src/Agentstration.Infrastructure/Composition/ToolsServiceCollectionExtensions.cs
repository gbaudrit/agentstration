using Agentstration.Extensions.Aep;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Infrastructure.Tools;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class ToolsServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationTools(this IServiceCollection services)
    {
        services.AddAgentstrationMcpTools();
        services.AddSingleton<ToolManagementService>();
        services.AddSingleton<IAepEnrollmentResourceProvisioner, AepEnrollmentToolProvisioner>();
        services.AddSingleton<IAepEnrollmentResourceProvisioner, AepEnrollmentBundleProvisioner>();
        services.AddSingleton<ToolCategoryService>();
        services.AddSingleton<ToolSetService>();
        services.AddSingleton<ToolDefinitionService>();
        services.AddSingleton<ToolExecutionHookManagementService>();
        services.AddSingleton<IToolExecutionHook, ArtifactStagingExecutionGuard>();
        services.TryAddSingleton(new ToolExecutionCaptureOptions());
        services.AddSingleton<IToolExecutionEventSink, RuntimeToolExecutionEventSink>();
        services.AddSingleton<IToolExecutionEventSink, FlowToolExecutionEventSink>();
        services.AddSingleton<IToolExecutionHookResolver, ManagementToolExecutionHookResolver>();
        services.AddSingleton<IToolGovernanceAuditReader, ToolGovernanceAuditReader>();
        services.AddSingleton<IToolExecutionPipeline, ToolExecutionPipeline>();
        services.AddSingleton<IFlowToolExecutor, ManagedFlowToolExecutor>();
        services.AddSingleton<IFlowToolSetResolver, ManagementFlowToolSetResolver>();
        services.AddSingleton<IToolSetDeletionGuard, ToolSetDeletionGuard>();
        services.AddSingleton<IToolDefinitionExecutor, ToolDefinitionExecutor>();
        services.AddSingleton<IToolDefinitionAcquisitionFactory, ToolDefinitionAcquisitionFactory>();
        services.AddSingleton<ToolDefinitionAcquisitionExecutor>();
        services.AddSingleton(provider => new Lazy<IToolDefinitionExecutor>(provider.GetRequiredService<IToolDefinitionExecutor>));
        return services;
    }
}
