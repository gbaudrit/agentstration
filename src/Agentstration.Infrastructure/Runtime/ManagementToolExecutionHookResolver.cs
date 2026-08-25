using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Runtime;

public sealed class ManagementToolExecutionHookResolver(IControlPlaneStore store) : IToolExecutionHookResolver
{
    public async ValueTask<IReadOnlyList<IToolExecutionHook>> ResolveAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.WorkspaceId is not { } workspaceId)
            return [];

        var resources = await store.ListAllAsync<ToolExecutionHookResource>(ResourceKinds.ToolExecutionHook, cancellationToken);
        var hooks = new List<IToolExecutionHook>();
        foreach (var stored in resources)
        {
            var resource = stored.Value;
            if (resource.WorkspaceId != workspaceId.Value
                || context.TenantId is { } tenantId && resource.TenantId != tenantId
                || !resource.Definition.Enabled
                || !Matches(resource.Definition.Selector.Tools, context.ToolId)
                || !Matches(resource.Definition.Selector.Providers, context.ToolProviderId)
                || !Matches(resource.Definition.Selector.Agents, context.AgentId))
                continue;

            ToolExecutionHookManagementService.Validate(resource);
            hooks.Add(Create(resource));
        }
        return hooks;
    }

    private static bool Matches(IReadOnlyList<string> selector, string? value) =>
        selector.Count == 0 || value is not null && selector.Contains(value, StringComparer.Ordinal);

    private static IToolExecutionHook Create(ToolExecutionHookResource resource) => resource.Definition.Handler switch
    {
        ToolExecutionHookHandlers.Deny => new DenyToolExecutionHook(
            $"managed:{resource.Address}",
            resource.Definition.Order,
            resource.Address.ToString(),
            resource.Generation,
            ToolExecutionHookManagementService.RequiredString(resource.Definition.Configuration, "code"),
            ToolExecutionHookManagementService.RequiredString(resource.Definition.Configuration, "message")),
        _ => throw new ToolExecutionHookValidationException(
            $"Unsupported Tool execution hook handler '{resource.Definition.Handler}'.")
    };

    private sealed class DenyToolExecutionHook(
        string id,
        int order,
        string resourceId,
        long resourceGeneration,
        string code,
        string message) : IToolExecutionHook
    {
        public string Id { get; } = id;
        public int Order { get; } = order;
        public ToolExecutionHookIdentity Identity { get; } = new(
            id,
            order,
            ToolExecutionHookSource.Managed,
            resourceId,
            resourceGeneration);

        public ValueTask<ToolExecutionHookDecision> BeforeInvokeAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolExecutionHookDecision.Deny(code, message));
    }
}
