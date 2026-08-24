using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class StandardRuntimeProfileSeeder(
    RuntimeProfileManagementService runtimes,
    ICurrentRequestContext requestContext)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<Guid> initializedWorkspaces = [];

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized) return;
        var workspaceId = requestContext.Current.WorkspaceId;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initializedWorkspaces.Contains(workspaceId)) return;

            if (await runtimes.GetAsync("maf-builtin", cancellationToken) is null)
            {
                await runtimes.CreateAsync(new RuntimeProfileResource
                {
                    ApiVersion = ManagementApiVersions.CoreV1,
                    Kind = ResourceKinds.RuntimeProfile,
                    Metadata = new ResourceMetadata
                    {
                        Name = "maf-builtin",
                        Annotations = new Dictionary<string, string>
                        {
                            [ResourceProvenanceAnnotations.BuiltIn] = "true"
                        }
                    },
                    Definition = new RuntimeProfileProperties
                    {
                        DisplayName = "Microsoft Agent Framework · Built-in",
                        RuntimeType = "microsoft-agent-framework",
                        Execution = new RuntimeExecutionDefaults
                        {
                            SessionMode = RuntimeSessionMode.Transient,
                            ToolInvocation = RuntimeToolInvocationMode.Automatic,
                            Streaming = StreamingMode.Automatic
                        }
                    }
                }, cancellationToken);
            }

            initializedWorkspaces.Add(workspaceId);
        }
        finally
        {
            gate.Release();
        }
    }
}
