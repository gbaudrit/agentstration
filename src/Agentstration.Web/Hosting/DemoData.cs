using Agentstration.Application;
using Agentstration.Application.Workspaces;
using Agentstration.Contracts;

namespace Agentstration.Web.Hosting;

public static class DemoData
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IPlatformStore>();
        if ((await store.ListWorkspacesAsync(cancellationToken)).Count > 0) return;
        var workspaceService = services.GetRequiredService<WorkspaceService>();
        var workspace = (await workspaceService.CreateAsync("Demo workspace", cancellationToken)).Value!;
        await workspaceService.CreateInboxAsync(workspace.Id, new CreateInboxRequest("Research", "research", "Documents and watch material"), cancellationToken);
    }
}
