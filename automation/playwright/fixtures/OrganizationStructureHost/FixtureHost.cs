using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Security.AspNetCoreIdentity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Playwright.OrganizationStructure;

internal static class FixtureHost
{
    public static async Task Main()
    {
        await using var factory = new StructureFactory();
        var address = new Uri(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? throw new InvalidOperationException("The browser fixture requires ASPNETCORE_URLS."));
        factory.UseKestrel(address.Port);
        factory.StartServer();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        using var services = factory.Services.CreateScope();
        var provider = services.ServiceProvider;
        var store = provider.GetRequiredService<IIdentityStore>();
        var admin = await provider.GetRequiredService<ILocalAccountPrincipalResolver>().ResolveByUserNameAsync("admin", lifetime.ApplicationStopping)
            ?? throw new InvalidOperationException("The bootstrap administrator was not provisioned.");
        var workspace = (await store.ListWorkspacesAsync(lifetime.ApplicationStopping)).Single(value => value.Name == "default");
        using var request = provider.GetRequiredService<IRequestContextScopeFactory>()
            .Push(new RequestContext(admin.Id, workspace.TenantId, workspace.Id));
        var accounts = provider.GetRequiredService<LocalAccountAdministrationService>();
        foreach (var (name, role) in new[] { ("tenant-admin", "Admin"), ("workspace-admin", "Admin"), ("structure-viewer", "Viewer") })
        {
            var (account, errors) = await accounts.CreateAsync(new(name, "Fixture973!Access", name, null, workspace.Id, role), lifetime.ApplicationStopping);
            if (account is null) throw new InvalidOperationException(string.Join("; ", errors));
            if (name == "tenant-admin")
            {
                var definition = await store.FindRoleDefinitionByNameAsync("Admin", lifetime.ApplicationStopping)
                    ?? throw new InvalidOperationException("The built-in Admin role is missing.");
                await store.AddRoleAssignmentAsync(new(Guid.NewGuid(), workspace.TenantId, account.PrincipalId,
                    PrincipalType.User, definition.Id, AuthorizationScopes.Tenant(workspace.TenantId)), lifetime.ApplicationStopping);
            }
        }
        var readyFile = Environment.GetEnvironmentVariable("ORGANIZATION_STRUCTURE_READY_FILE")
            ?? throw new InvalidOperationException("The browser fixture requires a readiness file.");
        var ready = "ready";
        if (string.Equals(Environment.GetEnvironmentVariable("SHARED_FORM_PENDING_FIXTURE"), "true", StringComparison.Ordinal))
        {
            var interactionId = await SeedPendingInputAsync(provider, workspace.Id, admin.Id, lifetime.ApplicationStopping);
            ready = JsonSerializer.Serialize(new { interactionId });
        }
        await File.WriteAllTextAsync(readyFile, ready, lifetime.ApplicationStopping);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => shutdown.TrySetResult());
        await shutdown.Task;
    }

    private static async Task<Guid> SeedPendingInputAsync(IServiceProvider provider, Guid workspaceId, Guid principalId, CancellationToken token)
    {
        var repository = provider.GetRequiredService<IWorkplaceRepository>();
        var workspace = new WorkspaceId(workspaceId);
        var now = provider.GetRequiredService<TimeProvider>().GetUtcNow();
        var entry = await repository.GetEntryAsync(workspace, new EntryId("quick-answer"), token)
            ?? new EntryResource
            {
                WorkspaceId = workspace, Id = new EntryId("forms-pending-input"), Name = "forms-pending-input",
                DisplayName = "Pending input fixture", Presentation = new EntryPresentation
                {
                    Fields = [new EntryFieldDefinition { Name = "request", Type = EntryFieldType.Prompt, Label = "Request", Role = EntryFieldRole.PrimaryInput }]
                },
                ResolvedTarget = new EntryResolvedTarget("system-direct-agent-dotnet-expert", "1.0.0"), PublishedAt = now
            };
        await repository.UpsertEntryAsync(entry, token);
        if (await repository.GetDashboardAsync(workspace, new DashboardId("home"), token) is null)
            await repository.UpsertDashboardAsync(new WorkplaceDashboard
            {
                Id = new DashboardId("home"), WorkspaceId = workspace, Name = "home", DisplayName = "Home", IsDefault = true, PublishedAt = now
            }, token);
        var id = InteractionId.New();
        var actionId = PendingActionId.New();
        var fields = new[] { new EntryFieldDefinition { Name = "focus", Type = EntryFieldType.Textarea, Label = "Report focus", Required = true } };
        const string resumeToken = "browser-fixture-pending-input-token";
        await repository.CreateInteractionAsync(new WorkplaceInteraction
        {
            Id = id, WorkspaceId = workspace, OwnerPrincipalId = principalId, EntryId = entry.Id, EntrySnapshot = entry,
            Status = InteractionStatus.WaitingForUser, StartedAt = now, LastActivityAt = now, PendingActionId = actionId,
            ImmediateResult = new RequestInputAction("What should the report focus on?", "A short answer is enough.", fields, actionId, resumeToken)
        }, token);
        await repository.CreatePendingActionAsync(new PendingAction
        {
            Id = actionId, WorkspaceId = workspace, InteractionId = id, Kind = PendingActionKind.InputRequired,
            Title = "What should the report focus on?", Fields = fields, CreatedAt = now, ExpiresAt = now.AddDays(1),
            ResumeTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resumeToken))), ResumeStep = 1
        }, token);
        await repository.AddMessageAsync(new ConversationMessage(Guid.NewGuid(), workspace, id, null,
            ConversationRole.User, "Prepare a report.", now), token);
        return id.Value;
    }

}

internal sealed class StructureFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(Directory.GetCurrentDirectory(), "src", "Agentstration.Web"));
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddCommandLine(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();
        // Public disposable browser-fixture credentials, subject to the regular password policy.
        builder.UseSetting("Agentstration:Bootstrap:Secrets:AdminPassword", "Fixture973!Admin");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }
}
