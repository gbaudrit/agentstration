using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Profiles;

public sealed class StandardRuntimeProfileSeeder(
    RuntimeProfileManagementService runtimes,
    ICurrentRequestContext requestContext)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<Guid> initializedTenants = [];

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized) return;
        await EnsureAsync(ResourceScopeRef.Tenant(requestContext.Current.TenantId), cancellationToken);
    }

    public async Task EnsureAsync(ResourceScopeRef tenantScope, CancellationToken cancellationToken)
    {
        if (tenantScope.Kind != ResourceScopeKind.Tenant)
            throw new ArgumentException("The standard RuntimeProfile requires a Tenant scope.", nameof(tenantScope));
        var tenantId = tenantScope.TargetId
            ?? throw new ArgumentException("The Tenant scope has no target ID.", nameof(tenantScope));

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initializedTenants.Contains(tenantId)) return;

            if (await runtimes.GetExactAsync(tenantScope, ResourceNamespace.Default, "maf-builtin", cancellationToken) is null)
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
                    ScopeRef = tenantScope,
                    Generation = 1,
                    Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
                }, cancellationToken);
            }

            initializedTenants.Add(tenantId);
        }
        finally
        {
            gate.Release();
        }
    }
}
