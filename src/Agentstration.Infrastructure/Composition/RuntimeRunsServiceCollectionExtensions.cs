using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Runtime.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Infrastructure;

internal static class RuntimeRunsServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationRuntimeRuns(this IServiceCollection services, AgentstrationCompositionContext context)
    {
        var runtimeWorkerLeaseOptions = context.Options.RuntimeWorkerLease;
        var runtimeWorkerDispatchOptions = context.Options.RuntimeWorkerDispatch;
        services.AddSingleton<IRuntimeRunQueue, AwpRuntimeRunQueue>();
        services.AddSingleton<IRuntimeRunExecutionScope, WorkspaceRuntimeRunExecutionScope>();
        services.AddSingleton<RuntimeRunStateManager>();
        services.AddSingleton<RuntimeRunService>();
        services.TryAddSingleton(runtimeWorkerLeaseOptions ?? new RuntimeWorkerLeaseOptions());
        services.TryAddSingleton(runtimeWorkerDispatchOptions ?? new RuntimeWorkerDispatchOptions());
        services.AddSingleton<RuntimeAssignmentAvailabilitySignal>();
        services.AddSingleton<RuntimeAssignmentLeaseGuardRegistry>();
        services.AddSingleton<FlowRunTokenUsageConsolidator>();
        services.AddSingleton<IRuntimeAssignmentProjection, AwpAssignmentProjection>();
        services.AddSingleton<RuntimeWorkerAssignmentService>();
        services.AddSingleton<IRuntimeExecutionMaterialResolver, RuntimeExecutionMaterialResolver>();
        services.AddSingleton<RuntimeWorkerExecutionService>();
        services.AddSingleton<IRuntimeWorkerOperationGateway, RuntimeWorkerOperationGateway>();
        services.AddSingleton<RuntimeWorkerDispatchService>();
        services.AddSingleton<IRuntimeObservabilityQueryService, Runtime.RuntimeObservabilityQueryService>();
        return services;
    }
}
