using Agentstration.Management.Abstractions;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class EntryAdministrationComponentTests
{
    [TestMethod]
    public async Task EntryEditorSwitchesResourceKindEditsFieldsAndPublishesPinnedFlow()
    {
        using var context = new BunitContext();
        var client = new FakeEntryAdministrationApiClient();
        context.Services.AddSingleton<IEntryAdministrationApiClient>(client);
        var rendered = context.Render<EntryEditor>();

        Assert.HasCount(5, rendered.FindAll("[role='tab']"));
        Assert.AreEqual("true", rendered.Find("[data-testid='entry-tab-overview']").GetAttribute("aria-selected"));
        await rendered.Find("[data-testid='entry-tab-definition']").ClickAsync(new());
        Assert.IsTrue(rendered.Markup.Contains("Direct Agent Flow", StringComparison.Ordinal));
        Assert.AreEqual(EntryBindingKind.Agent, client.RequestedKinds.Single());
        await rendered.Find("[data-testid='target-kind']").ChangeAsync(new ChangeEventArgs { Value = "Flow" });
        Assert.AreEqual(EntryBindingKind.Flow, client.RequestedKinds.Last());
        await rendered.Find("[data-testid='target-resource']").ChangeAsync(new ChangeEventArgs { Value = FakeEntryAdministrationApiClient.FlowResourceId });
        await rendered.Find("[data-testid='presentation-kind']").ChangeAsync(new ChangeEventArgs { Value = "Form" });
        await rendered.Find("[data-testid='add-field']").ClickAsync(new());
        var primaryRadios = rendered.FindAll("input[name='primary-input']");
        Assert.IsTrue(rendered.FindAll("input[type='checkbox'], input[type='radio']").All(value => value.ParentElement?.ClassList.Contains("entry-toggle") == true));
        Assert.IsTrue(rendered.FindAll(".entry-row-actions").All(value => !value.ClassList.Contains("form-actions")));
        await primaryRadios[1].ClickAsync(new());
        await rendered.FindAll("button").Single(value => value.TextContent.Contains("Publish pinned version", StringComparison.Ordinal)).ClickAsync(new());

        Assert.IsNotNull(client.SavedEntry);
        Assert.AreEqual(EntryBindingKind.Flow, client.SavedEntry.Binding.Kind);
        Assert.AreEqual(FakeEntryAdministrationApiClient.FlowResourceId, client.SavedEntry.Binding.ResourceId);
        Assert.AreEqual(EntryPresentationKind.Form, client.SavedEntry.Presentation.Kind);
        Assert.HasCount(2, client.SavedEntry.Presentation.Fields);
        Assert.AreEqual("field2", client.SavedEntry.Presentation.Fields.Single(value => value.Role == EntryFieldRole.PrimaryInput).Name);
        Assert.IsTrue(rendered.Markup.Contains("Published Entry v1 with pinned Flow 1.0.0", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Markup.Contains("Validate and publish", StringComparison.Ordinal));
        await rendered.Find("[data-testid='entry-tab-preview']").ClickAsync(new());
        Assert.IsTrue(rendered.Markup.Contains("Current draft presentation", StringComparison.Ordinal));
        Assert.IsTrue(rendered.FindAll(".entry-renderer input").All(value => value.HasAttribute("disabled")));
    }

    [TestMethod]
    public async Task ExistingEntryExposesRealOverviewUsageAndPublishedVersionAcrossTabs()
    {
        using var context = new BunitContext();
        var client = new FakeEntryAdministrationApiClient();
        context.Services.AddSingleton<IEntryAdministrationApiClient>(client);
        var rendered = context.Render<EntryEditor>(parameters => parameters.Add(value => value.Name, "primary"));

        Assert.IsTrue(rendered.Markup.Contains("Draft and published", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Personal", StringComparison.Ordinal));
        await rendered.Find("[data-testid='entry-tab-usage']").ClickAsync(new());
        Assert.IsTrue(rendered.Markup.Contains("Depends on", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Used by", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("Primary · position 1", StringComparison.Ordinal));
        Assert.HasCount(2, rendered.FindAll("details"));

        await rendered.Find("[data-testid='entry-tab-versions']").ClickAsync(new());
        Assert.IsTrue(rendered.Markup.Contains("Version 1", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("current published version", StringComparison.Ordinal));
        Assert.HasCount(1, rendered.FindAll(".entry-version-card"));
    }

    [TestMethod]
    public async Task NamespacedPackEntryUsesItsNamespaceAndIsReadOnly()
    {
        using var context = new BunitContext();
        var client = new FakeEntryAdministrationApiClient();
        context.Services.AddSingleton<IEntryAdministrationApiClient>(client);
        var @namespace = new Agentstration.Resources.ResourceNamespace("agentstration.daily-life-assistant");
        var rendered = context.Render<EntryEditor>(parameters => parameters
            .Add(value => value.Name, "main")
            .Add(value => value.EntryNamespace, @namespace.Value));

        Assert.AreEqual(@namespace, client.RequestedNamespace);
        Assert.HasCount(4, rendered.FindAll("[role='tab']"));
        Assert.IsFalse(rendered.Markup.Contains("entry-tab-definition", StringComparison.Ordinal));
        Assert.IsTrue(rendered.Markup.Contains("managed by its namespaced Pack source", StringComparison.Ordinal));
        var workplaceLink = rendered.FindAll("a").Single(value => value.TextContent.Contains("Use in Workplace", StringComparison.Ordinal));
        Assert.AreEqual("workspaces?entry=main&entryNamespace=agentstration.daily-life-assistant", workplaceLink.GetAttribute("href")?.TrimStart('/'));
        await rendered.Find("[data-testid='entry-tab-usage']").ClickAsync(new());
        Assert.AreEqual(@namespace, client.RequestedDependencyNamespace);
    }

    [TestMethod]
    public async Task WorkspaceEditorPublishesRequestedPackEntryWithItsNamespace()
    {
        using var context = new BunitContext();
        var client = new FakeEntryAdministrationApiClient();
        context.Services.AddSingleton<IEntryAdministrationApiClient>(client);
        var @namespace = new Agentstration.Resources.ResourceNamespace("agentstration.daily-life-assistant");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/workspaces?entry=main&entryNamespace=agentstration.daily-life-assistant");
        var rendered = context.Render<Workspaces>();

        Assert.IsTrue(rendered.Markup.Contains("ready to be added to Personal", StringComparison.Ordinal));
        await rendered.FindAll("button").Single(value => value.TextContent.Contains("Publish workspace", StringComparison.Ordinal)).ClickAsync(new());

        Assert.IsNotNull(client.SavedWorkspace);
        Assert.IsTrue(client.SavedWorkspace.Entries.Any(value => value.EntryResourceId == new EntryId("main", @namespace)));
    }

    [TestMethod]
    public async Task WorkspaceEditorKeepsExactlyOnePrimaryEntryWhenPublishing()
    {
        using var context = new BunitContext();
        var client = new FakeEntryAdministrationApiClient();
        context.Services.AddSingleton<IEntryAdministrationApiClient>(client);
        var rendered = context.Render<Workspaces>();

        var roles = rendered.FindAll("[data-testid='workspace-entry-role']");
        Assert.HasCount(2, roles);
        await roles[1].ChangeAsync(new ChangeEventArgs { Value = "Primary" });
        await rendered.FindAll("button").Single(value => value.TextContent.Contains("Publish workspace", StringComparison.Ordinal)).ClickAsync(new());

        Assert.IsNotNull(client.SavedWorkspace);
        Assert.HasCount(1, client.SavedWorkspace.Entries.Where(value => value.Role == WorkspaceEntryRole.Primary));
        Assert.AreEqual("secondary", ResourceName(client.SavedWorkspace.Entries.Single(value => value.Role == WorkspaceEntryRole.Primary).EntryResourceId.Value));
    }

    private static string ResourceName(string resourceId) => resourceId[(resourceId.LastIndexOf('/') + 1)..];

    private sealed class FakeEntryAdministrationApiClient : IEntryAdministrationApiClient
    {
        internal const string AgentResourceId = "deterministic";
        internal const string FlowResourceId = "router";
        private const string WorkspaceResourceId = "personal";
        private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

        public List<EntryBindingKind> RequestedKinds { get; } = [];
        public Agentstration.Resources.ResourceNamespace RequestedNamespace { get; private set; }
        public Agentstration.Resources.ResourceNamespace RequestedDependencyNamespace { get; private set; }
        public EntryDraft? SavedEntry { get; private set; }
        public WorkplaceWorkspaceDraft? SavedWorkspace { get; private set; }

        public Task<IReadOnlyList<EntryDraftResponse>> GetEntriesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EntryDraftResponse>>([]);
        public Task<EntryDraftResponse> GetEntryAsync(string name, CancellationToken cancellationToken)
            => GetEntryAsync(Agentstration.Resources.ResourceNamespace.Default, name, cancellationToken);
        public Task<EntryDraftResponse> GetEntryAsync(Agentstration.Resources.ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
        {
            RequestedNamespace = @namespace;
            var published = PublishedEntry(name, @namespace);
            var draft = new EntryDraft
            {
                Id = EntryId(name, @namespace),
                Name = name,
                DisplayName = "Prepare a report",
                Description = "Prepare a useful report.",
                Revision = 4,
                UpdatedAt = Now,
                Presentation = published.Presentation with
                {
                    Suggestions = [new EntrySuggestion("Monthly report", "Prepare a monthly report")]
                },
                Binding = new EntryBinding(EntryBindingKind.Flow, FlowResourceId),
                PublishedBinding = new EntryBinding(EntryBindingKind.Flow, FlowResourceId)
            };
            return Task.FromResult(new EntryDraftResponse(draft, published));
        }
        public Task<EntryDraft> SaveEntryAsync(EntryDraft draft, CancellationToken cancellationToken) { SavedEntry = draft with { Revision = 2, UpdatedAt = Now }; return Task.FromResult(SavedEntry); }
        public Task<EntryValidationResponse> ValidateEntryAsync(string name, CancellationToken cancellationToken) => Task.FromResult(new EntryValidationResponse(true, []));
        public Task<EntryResource> PublishEntryAsync(string name, CancellationToken cancellationToken) => Task.FromResult(PublishedEntry(name));
        public Task<IReadOnlyList<EntryDependencyResponse>> GetDependenciesAsync(string name, CancellationToken cancellationToken) =>
            GetDependenciesAsync(Agentstration.Resources.ResourceNamespace.Default, name, cancellationToken);
        public Task<IReadOnlyList<EntryDependencyResponse>> GetDependenciesAsync(Agentstration.Resources.ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
        {
            RequestedDependencyNamespace = @namespace;
            return Task.FromResult<IReadOnlyList<EntryDependencyResponse>>([new(FlowResourceId, "Flow", "ResolvedTarget")]);
        }

        public Task<IReadOnlyList<ResourcePickerItem>> GetResourcesAsync(EntryBindingKind kind, CancellationToken cancellationToken)
        {
            RequestedKinds.Add(kind);
            var item = kind == EntryBindingKind.Agent
                ? new ResourcePickerItem(AgentResourceId, "Deterministic agent", "Local agent", "1", "Succeeded", ResourceKinds.Agent, new Dictionary<string, string> { ["modelProfile"] = "deterministic" })
                : new ResourcePickerItem(FlowResourceId, "Router", "Published router", "1.0.0", "Active", ResourceKinds.Flow);
            return Task.FromResult<IReadOnlyList<ResourcePickerItem>>([item]);
        }

        public Task<IReadOnlyList<EntryResponse>> GetPublishedEntriesAsync(CancellationToken cancellationToken)
        {
            var packNamespace = new Agentstration.Resources.ResourceNamespace("agentstration.daily-life-assistant");
            return Task.FromResult<IReadOnlyList<EntryResponse>>([ToResponse(PublishedEntry("primary")), ToResponse(PublishedEntry("secondary")), ToResponse(PublishedEntry("main", packNamespace))]);
        }

        public Task<IReadOnlyList<WorkplaceWorkspaceDraftResponse>> GetWorkspacesAsync(CancellationToken cancellationToken)
        {
            var draft = new WorkplaceWorkspaceDraft
            {
                Id = new(WorkspaceResourceId),
                Name = "personal",
                DisplayName = "Personal",
                UpdatedAt = Now,
                Entries = [new() { EntryResourceId = EntryId("primary"), Role = WorkspaceEntryRole.Primary, Order = 0 }, new() { EntryResourceId = EntryId("secondary"), Role = WorkspaceEntryRole.Standard, Order = 10 }]
            };
            var published = new WorkplaceWorkspace { Id = new(WorkspaceResourceId), Name = "personal", DisplayName = "Personal", Entries = draft.Entries, Version = 2, PublishedAt = Now };
            return Task.FromResult<IReadOnlyList<WorkplaceWorkspaceDraftResponse>>([new(draft, published)]);
        }

        public Task<WorkplaceWorkspaceDraftResponse> GetWorkspaceAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkplaceWorkspaceDraft> SaveWorkspaceAsync(WorkplaceWorkspaceDraft draft, CancellationToken cancellationToken) { SavedWorkspace = draft with { Revision = 2, UpdatedAt = Now }; return Task.FromResult(SavedWorkspace); }
        public Task<WorkplaceWorkspace> PublishWorkspaceAsync(string name, CancellationToken cancellationToken) => Task.FromResult(new WorkplaceWorkspace { Id = new(WorkspaceResourceId), Name = name, DisplayName = "Personal", Entries = SavedWorkspace?.Entries ?? [], PublishedAt = Now });

        private static EntryId EntryId(string name, Agentstration.Resources.ResourceNamespace @namespace = default) => new(name, @namespace);
        private static EntryResource PublishedEntry(string name, Agentstration.Resources.ResourceNamespace @namespace = default) => new()
        {
            Id = EntryId(name, @namespace),
            Name = name,
            DisplayName = name,
            PublishedAt = Now,
            Presentation = new EntryPresentation
            {
                Fields = [new EntryFieldDefinition { Name = "request", Label = "Request", Type = EntryFieldType.Prompt, Required = true, Role = EntryFieldRole.PrimaryInput }]
            },
            ResolvedTarget = new EntryResolvedTarget(FlowResourceId, "1.0.0")
        };
        private static EntryResponse ToResponse(EntryResource value) => new(value.Id.Value, value.Name, value.Type, value.ApiVersion, value.DisplayName, value.Description, value.Presentation, value.ResolvedTarget, value.Behavior, value.Version, value.PublishedAt) { Namespace = value.Id.Namespace };
    }
}
