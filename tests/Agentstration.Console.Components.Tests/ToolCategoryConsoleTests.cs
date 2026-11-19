using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolCategoryConsoleTests
{
    [TestMethod]
    public async Task EditorShowsProviderProvenanceMissingMembersAndSafeDeleteExplanation()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(new ToolsClient());
        context.Services.AddSingleton<IToolCategoriesClient>(new CategoriesClient());

        ConfigureNavigation(context);
        var rendered = context.Render<ToolCategoryEditor>(parameters => parameters.Add(component => component.Name, "operations"));

        await rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "Operations MCP");
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "Documentation MCP");
            StringAssert.Contains(rendered.Find("[data-testid='category-members']").TextContent, "removed.tool");
            Assert.AreEqual("/tools/operations.run?namespace=default&scopeRef=%2Fworkspaces%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&context=%2Fworkspaces%2Faaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", rendered.Find("[data-provider='/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa|default/operations'] .tool-category-member a").GetAttribute("href"));
            var providerBlocks = rendered.FindAll(".tool-category-provider[data-provider]");
            Assert.HasCount(2, providerBlocks);
            var operationsBlock = providerBlocks.Single(block => block.GetAttribute("data-provider") == "/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa|default/operations");
            var documentationBlock = providerBlocks.Single(block => block.GetAttribute("data-provider") == "/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa|default/documentation");
            StringAssert.Contains(operationsBlock.TextContent, "Run operation");
            Assert.IsFalse(operationsBlock.TextContent.Contains("Search documentation", StringComparison.Ordinal));
            StringAssert.Contains(documentationBlock.TextContent, "Search documentation");
            Assert.IsFalse(documentationBlock.TextContent.Contains("Run operation", StringComparison.Ordinal));
        });

        rendered.Find("button.button-danger").Click();
        var deleteExplanation = rendered.Find("[role='alertdialog']").TextContent;
        Assert.IsTrue(deleteExplanation.Contains("ne seront pas modifiés", StringComparison.Ordinal)
            || deleteExplanation.Contains("will not be changed", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProviderAndToolHomonymsKeepSeparateGroupsLinksAndSelectionIds()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var categories = new CategoriesClient();
        categories.Resource = categories.Resource with { Definition = categories.Resource.Definition with { Tools = [] } };
        context.Services.AddSingleton<IToolCategoriesClient>(categories);
        var firstScope = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var secondScope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var spaces = new[] { ResourceNamespace.Parse("team-a"), ResourceNamespace.Parse("team-b") };
        var owners = new[] { firstScope, secondScope };
        var providers = owners.Select((scope, index) => new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new() { Name = "same-provider", Namespace = spaces[index] },
            ScopeRef = scope,
            Definition = new() { DisplayName = "Same provider", ProviderType = ToolProviderType.Mcp }
        }).ToArray();
        var tools = owners.Select((scope, index) => ToolsClient.Tool with
        {
            Metadata = new() { Name = "same-tool", Namespace = spaces[index] },
            ScopeRef = scope,
            Definition = ToolsClient.Tool.Definition with { Provider = new("same-provider", scope, spaces[index]) }
        }).ToArray();
        context.Services.AddSingleton<IToolsClient>(new ToolsClient(tools, providers));
        ConfigureNavigation(context);
        var cut = context.Render<ToolCategoryEditor>(parameters => parameters.Add(value => value.Name, "operations"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        var groups = cut.FindAll(".tool-category-provider[data-provider]");
        Assert.HasCount(2, groups);
        var inputs = groups.Select(group => group.QuerySelector(".tool-category-member input")!.Id).ToArray();
        Assert.AreNotEqual(inputs[0], inputs[1]);
        for (var index = 0; index < groups.Count; index++)
        {
            Assert.HasCount(1, groups[index].QuerySelectorAll(".tool-category-member"));
            var link = groups[index].QuerySelector(".collection-identity a")!.GetAttribute("href")!;
            StringAssert.Contains(link, $"namespace={spaces[index].Value}");
            StringAssert.Contains(link, $"scopeRef={Uri.EscapeDataString(owners[index].Value)}");
        }
    }

    [TestMethod]
    public async Task RenamingPreservesRelativeAndMissingReferencesWithTheOriginalEtag()
    {
        var client = new CategoriesClient();
        var relative = new ResourceReference("operations.run");
        var missing = new ResourceReference("removed.tool", @namespace: new ResourceNamespace("shared"));
        client.Resource = client.Resource with { Definition = client.Resource.Definition with { Tools = [relative, missing] } };
        using var context = Context(client);
        var cut = context.Render<ToolCategoryEditor>(p => p.Add(c => c.Name, "operations"));
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        var operation = cut.FindAll(".tool-category-member").Single(row => row.TextContent.Contains("Run operation", StringComparison.Ordinal));
        Assert.IsTrue(operation.QuerySelector("input")!.HasAttribute("checked"));
        await cut.InvokeAsync(() => nav.NavigateSection("definition/identity"));
        cut.Find("[data-testid=category-display-name]").Input("Renamed category");
        await cut.InvokeAsync(() => nav.NavigateSection("overview"));
        Assert.DoesNotContain("Renamed category", cut.Find(".resource-identity-summary").TextContent);
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        await cut.Find("#category-resource-form").SubmitAsync();
        Assert.IsNotNull(client.Updated);
        CollectionAssert.AreEqual(new[] { relative, missing }, client.Updated.Properties.Tools.ToArray());
        Assert.AreEqual("\"etag\"", client.LastEtag);
        Assert.AreEqual(1, client.Reads);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
    }

    [TestMethod]
    public async Task ResolvedSavedMemberOutsideTheBoundedCatalogRemainsVisibleAndSelected()
    {
        var client = new CategoriesClient(); using var context = Context(client, emptyCatalog: true);
        var cut = context.Render<ToolCategoryEditor>(p => p.Add(c => c.Name, "operations"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        var operation = cut.FindAll(".tool-category-member").Single(row => row.TextContent.Contains("Run operation", StringComparison.Ordinal));
        Assert.IsTrue(operation.QuerySelector("input")!.HasAttribute("checked"));
        await cut.Find("#category-resource-form").SubmitAsync();
        Assert.IsNotNull(client.Updated);
        CollectionAssert.AreEqual(client.Resource.Definition.Tools.ToArray(), client.Updated.Properties.Tools.ToArray());
        Assert.AreEqual(1, client.Reads);
    }

    [TestMethod]
    public async Task MemberSelectionStaysACheckboxAndDiscardRestoresTheSavedMembership()
    {
        var client = new CategoriesClient(); using var context = Context(client);
        var cut = context.Render<ToolCategoryEditor>(p => p.Add(c => c.Name, "operations"));
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        var operation = cut.FindAll(".tool-category-member").Single(row => row.TextContent.Contains("Run operation", StringComparison.Ordinal));
        operation.QuerySelector("input")!.Change(false);
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsEmpty(cut.FindAll("[role=switch]"));
        await cut.InvokeAsync(() => nav.NavigateSection("overview"));
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        Assert.IsTrue(cut.FindAll(".tool-category-member").Single(row => row.TextContent.Contains("Run operation", StringComparison.Ordinal)).QuerySelector("input")!.HasAttribute("checked"));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsNull(client.Updated);
    }

    [TestMethod]
    [DataRow("en-US", "Create category")]
    [DataRow("fr-FR", "Créer une catégorie")]
    public async Task CreationAllowsIncompleteStepsAndKeepsItsExactNamespaceAndScope(string locale, string category)
    {
        using var culture = new TestCultureScope(locale);
        var client = new CategoriesClient(); using var context = Context(client, create: true);
        var cut = context.Render<ToolCategoryEditor>();
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.IsNull(client.Created);
        Assert.Contains(category, cut.Find("h1").TextContent);
        cut.Find("[data-testid=category-display-name]").Input("New category");
        cut.Find("[data-testid=category-name]").Input("new-category");
        cut.Find("[data-testid=category-namespace]").Change("team-a");
        await cut.InvokeAsync(() => cut.FindComponent<ResourceScopeFormField>().Instance.ValueChanged.InvokeAsync(client.Resource.ScopeRef));
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.IsNotNull(client.Created);
        Assert.AreEqual("team-a", client.Created.Namespace);
        Assert.AreEqual(client.Resource.ScopeRef, client.Created.ScopeRef);
        Assert.Contains("namespace=team-a", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Contains("scopeRef=%2Fworkspaces%2Faaaaaaaa", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    public void AResponseFromAnotherOwnerCannotReplaceTheRequestedCategory()
    {
        var client = new CategoriesClient(); using var context = Context(client, query: "?scopeRef=%2Fworkspaces%2Fbbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var cut = context.Render<ToolCategoryEditor>(p => p.Add(c => c.Name, "operations"));
        Assert.IsNull(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.Resource);
        Assert.AreEqual(ResourceLoadStatus.Failed, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
        Assert.IsNotNull(cut.FindComponent<ErrorState>());
        Assert.IsEmpty(cut.FindAll("button.button-danger, button[type=submit]"));
        Assert.IsNull(client.Updated);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadOnlyCategoryContextsHaveNoDefinitionMutationCommands(bool managed)
    {
        var client = new CategoriesClient();
        if (managed) client.Resource = client.Resource with { Governance = ResourceGovernance.Pack("fixture", "fixture/category", "1.0.0") };
        using var context = Context(client, current: managed ? client.Resource.ScopeRef : ResourceScopeRef.Workspace(Guid.NewGuid()));
        var cut = context.Render<ToolCategoryEditor>(p => p.Add(c => c.Name, "operations"));
        Assert.IsNotNull(cut.Find(".resource-identity-summary"));
        Assert.IsEmpty(cut.FindAll("button.button-danger, .unsaved-changes-bar"));
        Assert.HasCount(2, cut.FindAll("a[data-resource-section^='definition/']"));
        Assert.AreEqual(managed ? 1 : 0, cut.FindAll("a[data-resource-section=access]").Count);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/identity"));
        Assert.IsTrue(cut.Find("#category-resource-form fieldset").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task RefreshRetainsAnInvalidIdentityAndMembershipEditedWhileTheRequestIsInFlight()
    {
        var client = new CategoriesClient(); using var context = Context(client);
        var cut = context.Render<ToolCategoryEditor>(p => p.Add(c => c.Name, "operations"));
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("definition/identity"));
        var form = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        cut.Find("[data-testid=category-display-name]").Input("");
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        await cut.Find("#category-resource-form").SubmitAsync();
        Assert.AreEqual("definition/identity", nav.CurrentPath);
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        var pending = new TaskCompletionSource<ResourceSnapshot<ToolCategoryResource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Loader = () => pending.Task;
        var refresh = cut.InvokeAsync(() => cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        cut.WaitForAssertion(() => Assert.AreEqual(ResourceLoadStatus.Refreshing, cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status));
        var operation = cut.FindAll(".tool-category-member").Single(row => row.TextContent.Contains("Run operation", StringComparison.Ordinal));
        operation.QuerySelector("input")!.Change(false);
        client.Resource = client.Resource with { Generation = 3, Definition = client.Resource.Definition with { DisplayName = "New server category" } };
        await cut.InvokeAsync(() => pending.SetResult(new(client.Resource, "\"external\"")));
        await refresh;
        Assert.AreSame(form, cut.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.IsFalse(cut.FindAll(".tool-category-member").Single(row => row.TextContent.Contains("Run operation", StringComparison.Ordinal)).QuerySelector("input")!.HasAttribute("checked"));
        await cut.InvokeAsync(() => nav.NavigateSection("definition/identity"));
        Assert.AreEqual("", cut.Find("[data-testid=category-display-name]").GetAttribute("value"));
        Assert.HasCount(1, cut.FindAll(".validation-message"));
        await cut.InvokeAsync(() => nav.NavigateSection("overview"));
        Assert.Contains("Operations", cut.Find(".resource-identity-summary").TextContent);
        cut.Find(".unsaved-changes-bar .button-secondary").Click();
        Assert.Contains("New server category", cut.Find(".resource-identity-summary").TextContent);
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        Assert.IsTrue(cut.FindAll(".tool-category-member").Single(row => row.TextContent.Contains("Run operation", StringComparison.Ordinal)).QuerySelector("input")!.HasAttribute("checked"));
        await cut.InvokeAsync(() => nav.NavigateSection("definition/identity"));
        cut.Find("[data-testid=category-display-name]").Input("Saved category");
        await cut.Find("#category-resource-form").SubmitAsync();
        Assert.AreEqual("\"external\"", client.LastEtag);
        CollectionAssert.AreEqual(client.Resource.Definition.Tools.ToArray(), client.Updated!.Properties.Tools.ToArray());
        Assert.AreSame(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionState, cut.FindComponent<ResourceDefinitionForm>().Instance.SectionState);
        Assert.AreSame(cut.FindComponent<ConsoleResourceDetailsTemplate>().Instance.SectionPresentations, cut.FindComponent<ResourceDefinitionForm>().Instance.Sections);
    }

    [TestMethod]
    public async Task MissingCreationScopeKeepsItsValidationInsideTheCommonScopeField()
    {
        var client = new CategoriesClient(); using var context = Context(client, create: true);
        var cut = context.Render<ToolCategoryEditor>();
        cut.Find("[data-testid=category-display-name]").Input("New category");
        cut.Find("[data-testid=category-name]").Input("new-category");
        var wizard = cut.FindComponent<CreationWizard>().Instance;
        await cut.InvokeAsync(() => wizard.GoToStepAsync("review"));
        await cut.InvokeAsync(wizard.FinishAsync);
        Assert.AreEqual("identity", wizard.CurrentStepId);
        Assert.IsNull(client.Created);
        Assert.HasCount(1, cut.FindAll(".form-field .validation-message"));
        var field = cut.Find(".form-field .validation-message").Closest(".form-field")!;
        Assert.IsNotNull(field.QuerySelector("select"));
        Assert.IsTrue(field.QuerySelector("select")!.HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task ConfirmedDeletionReleasesTheIndependentPolicyDraftAndLeavesTheDeletedResource()
    {
        var client = new CategoriesClient(); using var context = Context(client);
        context.ComponentFactories.AddStub<ResourceAccessPanel>();
        var cut = context.Render<ToolCategoryEditor>(p => p.Add(c => c.Name, "operations"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("access"));
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        await cut.InvokeAsync(() => policy.Parameters.Get(c => c.DraftChanged).InvokeAsync(true));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        cut.Find(".resource-header button.button-danger").Click();
        await cut.Find("[role=alertdialog] .button-danger").ClickAsync(new());
        Assert.AreEqual(1, client.Deletes);
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.Contains("/tools/categories", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.IsEmpty(cut.FindAll(".resource-form, .unsaved-changes-bar"));
    }

    private sealed class OwnerContext(ResourceScopeRef owner) : IConsoleNavigationContext
    {
        public string? ScopeRef => owner.Value;
        public string AddToUri(string uri) => uri;
    }
    private static BunitContext Context(CategoriesClient client, bool create = false, string query = "", ResourceScopeRef? current = null, bool emptyCatalog = false)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(new ToolsClient(emptyCatalog ? [] : null));
        context.Services.AddSingleton<IToolCategoriesClient>(client);
        if (current is { } scope) context.Services.AddSingleton<IConsoleNavigationContext>(new OwnerContext(scope));
        ConfigureNavigation(context, "http://localhost/tools/categories/" + (create ? "new" : "operations") + query);
        return context;
    }
    private static void ConfigureNavigation(BunitContext context, string? uri = null)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var nav = context.Services.GetRequiredService<NavigationManager>();
        if (uri is not null) nav.NavigateTo(uri);
        var current = nav.Uri.Split('#')[0];
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "access", "yaml" })
            module.Setup<string>("navigate", c => c.Arguments[0] is string url && url.EndsWith("#" + path, StringComparison.Ordinal)).SetResult(current + "#" + path);
    }

    private sealed class CategoriesClient : IToolCategoriesClient
    {
        private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        private static readonly ToolCategoryResource Category = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Uid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Kind = ToolResourceKinds.ToolCategory,
            Metadata = new ResourceMetadata { Name = "operations" },
            ScopeRef = Scope,
            Definition = new ToolCategoryProperties
            {
                DisplayName = "Operations",
                Tools = [new("operations.run", Scope), new("removed.tool", Scope)]
            }
        };
        public ToolCategoryResource Resource { get; set; } = Category;
        public PutToolCategoryRequest? Updated { get; private set; }
        public CreateToolCategoryRequest? Created { get; private set; }
        public string? LastEtag { get; private set; }
        public int Reads { get; private set; }
        public int Deletes { get; private set; }
        public Func<Task<ResourceSnapshot<ToolCategoryResource>>>? Loader { get; set; }
        public Task<ResourceSnapshot<ToolCategoryResource>> GetCategoryAsync(string name, CancellationToken cancellationToken = default) { Reads++; return Loader?.Invoke() ?? Task.FromResult(new ResourceSnapshot<ToolCategoryResource>(Resource, "\"etag\"")); }
        public Task<IReadOnlyList<ToolCategoryMember>> GetMembersAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolCategoryMember>>(Resource.Definition.Tools.Select(reference => new ToolCategoryMember(reference, reference.Name == "operations.run" ? ToolsClient.Tool : null, reference.Name == "operations.run" ? "available" : "missing")).ToArray());
        public Task<IReadOnlyList<ToolCategoryResource>> GetCategoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolCategoryResource>>([Resource]);
        public Task<ResourceSnapshot<ToolCategoryResource>> CreateCategoryAsync(CreateToolCategoryRequest request, CancellationToken cancellationToken = default)
        {
            Created = request;
            Resource = Resource with { Metadata = new() { Name = request.Name, Namespace = ResourceNamespace.Parse(request.Namespace) }, ScopeRef = request.ScopeRef, Definition = request.Properties };
            return Task.FromResult(new ResourceSnapshot<ToolCategoryResource>(Resource, "\"created\""));
        }
        public Task<ResourceSnapshot<ToolCategoryResource>> UpdateCategoryAsync(string name, PutToolCategoryRequest request, string etag, CancellationToken cancellationToken = default)
        {
            Updated = request; LastEtag = etag; Resource = Resource with { Definition = request.Properties, Generation = Resource.Generation + 1 };
            return Task.FromResult(new ResourceSnapshot<ToolCategoryResource>(Resource, "\"updated\""));
        }
        public Task DeleteCategoryAsync(string name, string etag, CancellationToken cancellationToken = default) { Deletes++; return Task.CompletedTask; }
    }

    private sealed class ToolsClient(IReadOnlyList<ToolResource>? tools = null, IReadOnlyList<ToolProviderResource>? providers = null) : IToolsClient
    {
        internal static readonly ToolResource Tool = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = "operations.run" },
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            Definition = new ToolResourceProperties
            {
                DisplayName = "Run operation",
                Provider = new("operations"),
                Enabled = true,
                Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
            }
        };
        private static readonly ToolResource DocumentationTool = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = "documentation.search" },
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            Definition = new ToolResourceProperties
            {
                DisplayName = "Search documentation",
                Provider = new("documentation"),
                Enabled = true,
                Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
            }
        };
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ToolResource>>(tools ?? [Tool, DocumentationTool]);
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolProviderResource>>(providers ?? [
            new ToolProviderResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")), Metadata = new ResourceMetadata { Name = "operations" }, Definition = new ToolProviderProperties { DisplayName = "Operations MCP", ProviderType = ToolProviderType.Mcp } },
            new ToolProviderResource { ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")), Metadata = new ResourceMetadata { Name = "documentation" }, Definition = new ToolProviderProperties { DisplayName = "Documentation MCP", ProviderType = ToolProviderType.Mcp } }
        ]);
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
