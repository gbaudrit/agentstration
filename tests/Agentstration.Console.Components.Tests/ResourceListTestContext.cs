using Agentstration.Identity.Contracts;
using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

internal static class ResourceListTestContext
{
    public static BunitContext WithResourceListContext(this BunitContext context, ResourceListContextProvider? provider = null)
    {
        provider ??= new();
        context.Services.AddSingleton<IConsoleContextProvider>(provider);
        context.Services.AddSingleton<ConsoleContextState>();
        return context;
    }
}

internal sealed class ResourceListContextProvider : IConsoleContextProvider
{
    public Guid PhysicalWorkspaceId { get; set; }
    public string Scope { get; set; } = "/instance";
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AuthorizationPermissions.ResourcesRead, AuthorizationPermissions.ResourcesWrite
    };
    public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
        Guid.Empty, "Fixture", Scope, ConsoleContextKind.Instance, Guid.Empty, "tenant", "Tenant", PhysicalWorkspaceId, "workspace", "Workspace", Permissions, [], []));
}
