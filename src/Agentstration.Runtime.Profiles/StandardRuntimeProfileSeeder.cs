using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Profiles;

public sealed class StandardRuntimeProfileSeeder(
    RuntimeProfileManagementService runtimes,
    ICurrentRequestContext requestContext)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized) return;
        await EnsureAsync(ResourceScopeRef.Instance, cancellationToken);
    }

    public async Task EnsureAsync(ResourceScopeRef instanceScope, CancellationToken cancellationToken)
    {
        if (instanceScope != ResourceScopeRef.Instance)
            throw new ArgumentException("The standard RuntimeProfile requires the Instance scope.", nameof(instanceScope));

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;

            if (await runtimes.GetExactAsync(instanceScope, ResourceNamespace.Default, "maf-builtin", cancellationToken) is null)
            {
                await runtimes.CreateAsync(new RuntimeProfileResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = RuntimeProfileResourceKinds.RuntimeProfile,
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
                    },
                    ScopeRef = instanceScope,
                    Generation = 1,
                    Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
                }, cancellationToken);
            }

            initialized = true;
        }
        finally
        {
            gate.Release();
        }
    }
}
