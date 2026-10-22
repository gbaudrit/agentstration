using Agentstration.Artifacts.Contracts;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class ArtifactStagingExecutionGuard : IToolExecutionHook
{
    public string Id => "artifact-staging-internal-only";
    public int Order => int.MinValue;

    public ValueTask<ToolExecutionHookDecision> BeforeInvokeAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var isStagingCapability = context.ExternalToolId is ArtifactCapabilities.Create
            or ArtifactCapabilities.Write
            or ArtifactCapabilities.Read
            or ArtifactCapabilities.Stat
            or ArtifactCapabilities.Delete;
        var isStorageImplementation = context.ExternalToolId is ArtifactStorageWriteMcpTool.Name or ArtifactStorageReadMcpTool.Name;
        return ValueTask.FromResult((!isStagingCapability && !isStorageImplementation)
            || context.OwnerKind == ToolExecutionOwnerKind.ArtifactService
            || isStorageImplementation && context.OwnerKind == ToolExecutionOwnerKind.FlowRun
            ? ToolExecutionHookDecision.Allowed
            : ToolExecutionHookDecision.Deny(
                "artifact_staging_direct_invocation_denied",
                "Artifact backend implementation Tools can only be invoked by the governed Artifact service or a Storage Flow."));
    }
}
