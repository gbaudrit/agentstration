using System.Net;
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
        // Configure before Build; UseKestrel(port) mutates addresses after the minimal app can start.
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, address.Port));
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
        await File.WriteAllTextAsync(readyFile, "ready", lifetime.ApplicationStopping);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => shutdown.TrySetResult());
        await shutdown.Task;
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
