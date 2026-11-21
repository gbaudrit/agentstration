using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class OrganizationStructureTests
{
    [TestMethod]
    [DataRow(ConsoleContextKind.Instance, 0, 1, 1)]
    [DataRow(ConsoleContextKind.Tenant, 1, 0, 1)]
    [DataRow(ConsoleContextKind.Workspace, 1, 0, 0)]
    public void CurrentContextReplacesItsNavigationAction(ConsoleContextKind kind, int currentCount, int tenantLinks, int workspaceLinks)
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var tenant = new Tenant(Guid.NewGuid(), "acme", "Acme", TenantStatus.Active, DateTimeOffset.UnixEpoch);
        var handler = new StructureHandler(tenant)
        {
            Workspace = new(Guid.NewGuid(), tenant.Id, "production", "Production", WorkspaceStatus.Active, DateTimeOffset.UnixEpoch)
        };
        Register(context, handler, kind);
        var rendered = context.Render<OrganizationWorkspaces>();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='workspace-row']")));

        Assert.HasCount(currentCount, rendered.FindAll(".current-context[aria-current='true']"));
        Assert.HasCount(tenantLinks, rendered.FindAll(".tenant-heading a.scope-link"));
        Assert.HasCount(workspaceLinks, rendered.FindAll(".workspace-row a.scope-link"));
        if (kind == ConsoleContextKind.Instance)
        {
            Assert.HasCount(0, rendered.FindAll(".is-current"));
        }
        else
        {
            var currentRow = rendered.Find(".is-current");
            Assert.HasCount(0, currentRow.QuerySelectorAll("a.scope-link"));
            var strings = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<OrganizationStrings>>();
            Assert.Contains(strings["CurrentContext"].Value, currentRow.TextContent, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void StructureShowsExplicitParentAndSubmitsWorkspaceCreation()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var tenant = new Tenant(Guid.NewGuid(), "acme", "Acme", TenantStatus.Active, DateTimeOffset.UnixEpoch);
        var handler = new StructureHandler(tenant);
        Register(context, handler, ConsoleContextKind.Tenant);
        var rendered = context.Render<OrganizationWorkspaces>();
        rendered.WaitForAssertion(() => Assert.Contains("Acme", rendered.Markup, StringComparison.Ordinal));
        Assert.HasCount(0, rendered.FindAll("[data-testid='workspace-create-form']"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='tenant-create-open']"));
        rendered.Find("[data-testid='workspace-create-open']").Click();
        Assert.Contains("Acme", rendered.Find(".parent-context").TextContent, StringComparison.Ordinal);
        rendered.Find("[data-testid='workspace-display-name']").Input("Production");
        rendered.Find("[data-testid='workspace-technical-name']").Input("production");
        Assert.IsFalse(rendered.Find("[data-testid='workspace-create']").HasAttribute("disabled"));
        Assert.HasCount(0, rendered.FindAll(".unsaved-changes-bar"));
        rendered.Find("form").Submit();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Created));
        Assert.IsNotNull(handler.Created);
        Assert.AreEqual(tenant.Id, handler.Created.TenantId);
        Assert.AreEqual("production", handler.Created.Name);
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='workspace-row']")));
        Assert.HasCount(0, rendered.FindAll("[data-testid='workspace-create-form']"));
    }

    [TestMethod]
    public void WorkspaceCreationDoesNotNavigateToAnUnavailableContext()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var tenant = new Tenant(Guid.NewGuid(), "acme", "Acme", TenantStatus.Active, DateTimeOffset.UnixEpoch);
        var handler = new StructureHandler(tenant);
        Register(context, handler, ConsoleContextKind.Workspace);
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var originalUri = navigation.Uri;
        var rendered = context.Render<OrganizationWorkspaces>();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='workspace-create-open']")));
        rendered.Find("[data-testid='workspace-create-open']").Click();
        rendered.Find("[data-testid='workspace-display-name']").Input("Production");
        rendered.Find("[data-testid='workspace-technical-name']").Input("production");
        rendered.Find("form").Submit();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Created));
        rendered.WaitForAssertion(() => Assert.HasCount(0, rendered.FindAll("[data-testid='workspace-create-form']")));
        Assert.AreEqual(originalUri, navigation.Uri);
    }

    [TestMethod]
    public void SearchCollapseAndReadOnlyActionsReflectTheStructureResponse()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var tenant = new Tenant(Guid.NewGuid(), "acme", "Acme", TenantStatus.Active, DateTimeOffset.UnixEpoch);
        var handler = new StructureHandler(tenant, writable: false)
        {
            Workspace = new(Guid.NewGuid(), tenant.Id, "production", "Production", WorkspaceStatus.Active, DateTimeOffset.UnixEpoch)
        };
        Register(context, handler, ConsoleContextKind.Workspace);
        var rendered = context.Render<OrganizationWorkspaces>();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='workspace-row']")));
        Assert.HasCount(0, rendered.FindAll("[data-testid='workspace-create-open']"));
        Assert.HasCount(0, rendered.FindAll("[data-testid='tenant-create-open']"));
        rendered.Find(".tenant-toggle").Click();
        Assert.HasCount(0, rendered.FindAll("[data-testid='workspace-row']"));
        rendered.Find("input[type='search']").Input("production");
        Assert.HasCount(1, rendered.FindAll("[data-testid='workspace-row']"));
        rendered.Find("input[type='search']").Input("missing");
        Assert.HasCount(0, rendered.FindAll("[data-testid='tenant-row']"));
        Assert.HasCount(1, rendered.FindAll("[role='status']"));
    }

    [TestMethod]
    public void InstanceCanOpenTenantCreationAndCancelItWithoutWriting()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        var tenant = new Tenant(Guid.NewGuid(), "acme", "Acme", TenantStatus.Active, DateTimeOffset.UnixEpoch);
        var handler = new StructureHandler(tenant, instance: true);
        Register(context, handler, ConsoleContextKind.Instance);
        var rendered = context.Render<OrganizationWorkspaces>();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='tenant-create-open']")));
        rendered.Find("[data-testid='tenant-create-open']").Click();
        Assert.Contains("Instance", rendered.Find(".parent-context").TextContent, StringComparison.Ordinal);
        Assert.IsTrue(rendered.Find("[data-testid='workspace-create']").HasAttribute("disabled"));
        rendered.Find(".creation-actions button[type='button']").Click();
        Assert.HasCount(0, rendered.FindAll("[data-testid='workspace-create-form']"));
        Assert.IsNull(handler.Created);
    }

    private static void Register(BunitContext context, StructureHandler handler, ConsoleContextKind kind)
    {
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IIdentityAdministrationApiClient>(new IdentityAdministrationApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));
        context.Services.AddSingleton<IConsoleContextProvider>(new ContextProvider(handler.Tenant, kind, handler.Workspace));
        context.Services.AddSingleton<ConsoleContextState>();
    }

    private sealed class ContextProvider(Tenant tenant, ConsoleContextKind kind, Workspace? workspace) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Admin", kind switch
            {
                ConsoleContextKind.Instance => "/instance",
                ConsoleContextKind.Workspace => $"/workspaces/{workspace?.Id ?? Guid.Empty:D}",
                _ => $"/tenants/{tenant.Id:D}"
            }, kind,
            tenant.Id, tenant.Name, tenant.DisplayName, workspace?.Id ?? Guid.Empty, workspace?.Name ?? "default", workspace?.DisplayName ?? "Default", new HashSet<string>(), [],
            workspace is null ? [] :
            [new($"/tenants/{tenant.Id:D}", ConsoleContextKind.Tenant, tenant.Id, tenant.Name, tenant.DisplayName, null, null, tenant.DisplayName),
             new($"/workspaces/{workspace.Id:D}", ConsoleContextKind.Workspace, tenant.Id, tenant.Name, tenant.DisplayName, workspace.Id, workspace.Name, workspace.DisplayName)]));
    }

    private sealed class StructureHandler(Tenant tenant, bool writable = true, bool instance = false) : HttpMessageHandler
    {
        public Tenant Tenant => tenant;
        public Workspace? Workspace { get; set; }
        public CreateTenantWorkspaceRequest? Created { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Created = await request.Content!.ReadFromJsonAsync<CreateTenantWorkspaceRequest>(cancellationToken);
                Workspace = new(Guid.NewGuid(), tenant.Id, Created!.Name, Created.DisplayName, WorkspaceStatus.Active, DateTimeOffset.UnixEpoch);
                return new(HttpStatusCode.Created) { Content = JsonContent.Create(Workspace) };
            }
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new OrganizationStructureResponse(
                    [new(tenant, Workspace is null ? [] : [Workspace], writable)], instance))
            };
        }
    }
}
