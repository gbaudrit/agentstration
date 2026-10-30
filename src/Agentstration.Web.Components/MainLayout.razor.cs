using Agentstration.Web.Components.Localization;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components;

public partial class MainLayout
{

    private sealed record NavigationItem(string LabelKey, string Url, string Icon, string Domain = "neutral", IReadOnlyList<string>? RequiredPermissions = null);
    private sealed record NavigationGroup(string LabelKey, string Tone, IReadOnlyList<NavigationItem> Items);
    private sealed record CommandDefinition(string LabelKey, string Url, string Icon, string CategoryKey, string Keywords = "", IReadOnlyList<string>? RequiredPermissions = null);
    private sealed record CommandItem(string Label, string Url, string Icon, string Category, string Keywords = "", string? Detail = null, bool IsFallback = false);
    private sealed record ContextTenantGroup(
        Guid TenantId,
        string DisplayName,
        ConsoleContextOption? Tenant,
        IReadOnlyList<ConsoleContextOption> Workspaces);

    private static readonly NavigationGroup[] NavigationGroups =
    [
        new("", "overview", [new("Nav.Overview", "/", "home")]),
        new("Group.Design", "design", [new("Nav.Agents", "/agents", "agent", "agent"), new("Nav.Flows", "/flows", "workflow", "flow"), new("Nav.Schemas", "/schemas", "document", "flow", ["resources/read"]), new("Nav.Entries", "/entries", "entry", "work"), new("Nav.ResourcePlans", "/resource-plans", "layers", "work", ["resources/read"]), new("Nav.ModelProfiles", "/modelprofiles", "adjustments", "model"), new("Nav.DataSourceProfiles", "/data-source-profiles", "database-cog", "tool", ["resources/read"])]),
        new("Group.Automate", "automate", [new("Nav.Triggers", "/triggers", "clock", "work")]),
        new("Group.Operate", "operate", [new("Nav.Conversations", "/conversations", "message-circle", "work", ["runs/read"]), new("Nav.Tasks", "/tasks", "tasks", "work")]),
        new("Group.Observe", "observe", [new("Nav.AgentInstances", "/agent-instances", "server", "runtime"), new("Nav.AgentRuns", "/agent-runs", "play-circle", "execution"), new("Nav.FlowRuns", "/flow-runs", "flow-run", "flow"), new("Nav.RunEvents", "/run-events", "activity")]),
        new("Group.Workplace", "workplace", [new("Nav.WorkplaceSetup", "/workspaces", "layout-grid", "work")]),
        new("Group.Resources", "resources", [new("Nav.Packs", "/packs", "package"), new("Nav.DataSources", "/data-sources", "database", "tool", ["resources/read"]), new("Nav.KnowledgeSources", "/knowledge-sources", "brain", "tool", ["resources/read"]), new("Nav.Artifacts", "/artifacts", "file", "flow"), new("Nav.Sources", "/settings/sources", "books"), new("Nav.SourceRegistries", "/settings/source-registries", "database-search")]),
        new("Group.Integrations", "integrations", [new("Nav.Tools", "/tools", "wrench", "tool"), new("Nav.Extensions", "/extensions", "puzzle"), new("Nav.ModelProviders", "/modelproviders", "cpu", "model"), new("Nav.SourceProviders", "/sourceproviders", "plug-connected", "source")]),
        new("Group.Configuration", "configuration", [new("Nav.RuntimeProfiles", "/runtimeprofiles", "cube", "runtime"), new("Nav.Secrets", "/secrets", "key"), new("Nav.ResourceScopes", "/settings/resource-scopes", "binary-tree", RequiredPermissions: ["resources/read"])]),
        new("Group.System", "system", [new("Nav.RuntimeWorkers", "/settings/runtime-workers", "server"), new("Nav.Organization", "/settings/organization", "building"), new("Nav.Bootstrap", "/settings/bootstrap", "cloud-upload"), new("Nav.Cleanup", "/cleanup", "trash", RequiredPermissions: ["resources/delete", "runs/delete"]), new("Nav.Settings", "/settings", "settings")])
    ];

    private static readonly CommandDefinition[] CommandDefinitions =
    [
        new("Nav.Overview", "/", "home", "Navigate"),
        new("Nav.Agents", "/agents", "agent", "Group.Design", "agent resources agents ressources"),
        new("Command.CreateAgent", "/agents/new", "plus", "Command", "new nouveau agent"),
        new("Nav.ModelProfiles", "/modelprofiles", "adjustments", "Group.Design", "models modèles"),
        new("Command.CreateModelProfile", "/modelprofiles/new", "plus", "Command", "new nouveau model modèle"),
        new("Nav.DataSourceProfiles", "/data-source-profiles", "database-cog", "Group.Design", "data source profiles acquisition profils sources données", ["resources/read"]),
        new("Nav.Flows", "/flows", "workflow", "Group.Design", "workflow designer flux conception"),
        new("Command.CreateFlow", "/flows/new", "plus", "Command", "new nouveau workflow flux"),
        new("Nav.Entries", "/entries", "entry", "Group.Design", "workplace entry entrée"),
        new("Command.CreateEntry", "/entries/new", "plus", "Command", "new nouvelle workplace entry entrée"),
        new("Nav.ResourcePlans", "/resource-plans", "layers", "Group.Design", "planning plans resources ressources review revue", ["resources/read"]),
        new("Nav.AgentInstances", "/agent-instances", "server", "Group.Observe", "agent runtime instances workers exécution"),
        new("Nav.AgentRuns", "/agent-runs", "play-circle", "Group.Observe", "agent execution history exécution historique"),
        new("Nav.FlowRuns", "/flow-runs", "flow-run", "Group.Observe", "workflow executions flux exécutions"),
        new("Nav.Tasks", "/tasks", "tasks", "Group.Operate", "work tasks supervision tâches"),
        new("Nav.Conversations", "/conversations", "message-circle", "Group.Operate", "conversation interaction history resume reprendre historique", ["runs/read"]),
        new("Nav.Triggers", "/triggers", "clock", "Group.Automate", "schedule automation planification automatisation"),
        new("Nav.RunEvents", "/run-events", "activity", "Group.Observe", "persisted runtime flow activity événements"),
        new("Nav.ModelProviders", "/modelproviders", "cpu", "Group.Integrations", "providers fournisseurs"),
        new("Nav.Tools", "/tools", "wrench", "Group.Integrations", "tool catalog providers outils fournisseurs MCP AEP"),
        new("Command.CreateToolCategory", "/tools/categories/new", "plus", "Command", "new nouvelle category catégorie tools outils"),
        new("Command.CreateToolProvider", "/tools/providers/new", "plus", "Command", "new nouveau MCP AEP provider fournisseur"),
        new("Nav.WorkplaceSetup", "/workspaces", "layout-grid", "Group.Workplace", "workspace composition primary entries espace composition"),
        new("Nav.Packs", "/packs", "package", "Group.Resources", "package distribution install archive resources paquet installation"),
        new("Nav.Schemas", "/schemas", "document", "Group.Design", "schema schemas contract contracts schéma schémas contrat contrats", ["resources/read"]),
        new("Command.CreateSchema", "/schemas/new", "plus", "Command", "new nouveau schema schéma", ["resources/write"]),
        new("Nav.KnowledgeSources", "/knowledge-sources", "brain", "Group.Resources", "knowledge source retrieval ingestion connaissances", ["resources/read"]),
        new("Nav.DataSources", "/data-sources", "database", "Group.Resources", "data sources acquisition origins sources données", ["resources/read"]),
        new("Nav.Artifacts", "/artifacts", "file", "Group.Resources", "staged durable artifacts retention purge artefacts stockage", ["resources/read"]),
        new("Command.CreateModelProvider", "/modelproviders/new", "plus", "Command", "new nouveau provider fournisseur"),
        new("Nav.SourceProviders", "/sourceproviders", "plug-connected", "Group.Integrations", "source providers fournisseurs Git AEP"),
        new("Command.CreateSourceProvider", "/sourceproviders/new", "plus", "Command", "new nouveau source provider fournisseur"),
        new("Nav.RuntimeProfiles", "/runtimeprofiles", "cube", "Group.Configuration", "runtime configuration exécution"),
        new("Command.CreateRuntimeProfile", "/runtimeprofiles/new", "plus", "Command", "new nouveau runtime profile profil"),
        new("Nav.Secrets", "/secrets", "key", "Group.Configuration", "credentials Vaults write only secrets identifiants coffres"),
        new("Command.CreateSecret", "/secrets/new", "plus", "Command", "new nouveau credential secret"),
        new("Command.Vaults", "/vaults", "vault", "Group.Configuration", "secret storage providers coffres stockage"),
        new("Command.CreateVault", "/vaults/new", "plus", "Command", "new nouveau secret storage coffre"),
        new("Nav.Settings", "/settings", "settings", "Group.System", "configuration paramètres"),
        new("Nav.ResourceScopes", "/settings/resource-scopes", "binary-tree", "Group.Configuration", "resource ownership hierarchy scopes ressources propriété hiérarchie", ["resources/read"]),
        new("Nav.Sources", "/settings/sources", "books", "Group.Resources", "sources catalogs reusable content catalogues contenu réutilisable"),
        new("Nav.SourceRegistries", "/settings/source-registries", "database-search", "Group.Resources", "registry registries discovery trust registre registres découverte confiance"),
        new("Command.DiscoverSources", "/settings/source-registries/discovery", "search", "Command", "registry catalog source discovery registre catalogue découverte"),
        new("Command.ProfileSettings", "/settings/profile", "user-circle", "Nav.Profile", "appearance theme personal preferences apparence thème préférences"),
        new("Nav.Extensions", "/extensions", "puzzle", "Group.Integrations", "AEP option contracts compatibility extensions"),
        new("Nav.Cleanup", "/cleanup", "trash", "Group.System", "cleanup clean delete purge nettoyage suppression", ["resources/delete", "runs/delete"]),
        new("Nav.Organization", "/settings/organization", "building", "Group.System", "tenant organization organisation"),
        new("Nav.Bootstrap", "/settings/bootstrap", "cloud-upload", "Group.System", "bootstrap profiles configuration profils configuration"),
        new("Nav.RuntimeWorkers", "/settings/runtime-workers", "server", "Group.System", "runtime workers AWP sessions capacity capacité"),
        new("Command.OrganizationWorkspaces", "/settings/organization/workspaces", "layout-grid", "Group.System", "tenant workspaces espaces"),
        new("Command.OrganizationMembers", "/settings/organization/members", "users", "Group.System", "users memberships roles membres rôles")
    ];

    private IJSObjectReference? commandModule;
    private IJSObjectReference? contextModule;
    private IJSObjectReference? themeModule;
    private IJSObjectReference? navigationTooltipModule;
    private IJSObjectReference? commandRegistration;
    private IJSObjectReference? navigationTooltipRegistration;
    private DotNetObjectReference<MainLayout>? selfReference;
    private ElementReference shellElement;
    private ElementReference commandInput;
    private string commandQuery = string.Empty;
    private bool commandPaletteOpen;
    private bool focusCommandInput;
    private bool searchingResources;
    private bool themeReady;
    private bool contextSelectorOpen;
    private bool switchingContext;
    private string contextQuery = string.Empty;
    private int selectedCommandIndex;
    private IReadOnlyList<CommandItem> resourceCommands = [];
    private Task? platformStatusRefresh;
    private IReadOnlyList<CommandItem> fallbackCommands = [];
    private CancellationTokenSource? resourceSearchCancellation;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private IReadOnlyList<CommandItem> Commands => CommandDefinitions.Where(command => CanNavigate(command.RequiredPermissions)).Select(command => new CommandItem(
        T(command.LabelKey), command.Url, command.Icon, T(command.CategoryKey), command.Keywords)).ToArray();
    private IReadOnlyList<CommandItem> FilteredCommands => Commands
        .Where(command => string.IsNullOrWhiteSpace(commandQuery)
            || command.Label.Contains(commandQuery, StringComparison.OrdinalIgnoreCase)
            || command.Category.Contains(commandQuery, StringComparison.OrdinalIgnoreCase)
            || command.Keywords.Contains(commandQuery, StringComparison.OrdinalIgnoreCase))
        .Take(8)
        .ToArray();
    private IReadOnlyList<CommandItem> DisplayedCommands =>
        FilteredCommands.Concat(resourceCommands)
            .DistinctBy(command => command.Url, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Concat(fallbackCommands)
            .ToArray();
    private IReadOnlyList<ConsoleContextOption> FilteredContexts => ContextState.Current?.Contexts
        .Where(option => string.IsNullOrWhiteSpace(contextQuery)
            || option.DisplayName.Contains(contextQuery, StringComparison.CurrentCultureIgnoreCase)
            || (option.TenantDisplayName?.Contains(contextQuery, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (option.WorkspaceName?.Contains(contextQuery, StringComparison.OrdinalIgnoreCase) ?? false)
            || option.Kind.ToString().Contains(contextQuery, StringComparison.OrdinalIgnoreCase))
        .ToArray() ?? [];
    private bool IsFilteringContexts => !string.IsNullOrWhiteSpace(contextQuery);
    private IReadOnlyList<ConsoleContextOption> InstanceContexts => ContextState.Current?.Contexts
        .Where(option => option.Kind == ConsoleContextKind.Instance)
        .ToArray() ?? [];
    private IReadOnlyList<ContextTenantGroup> ContextTenantGroups
    {
        get
        {
            var contexts = ContextState.Current?.Contexts ?? [];
            return contexts
                .Where(option => option.TenantId.HasValue
                    && option.Kind is ConsoleContextKind.Tenant or ConsoleContextKind.Workspace)
                .GroupBy(option => option.TenantId!.Value)
                .Select(group =>
                {
                    var tenant = group.FirstOrDefault(option => option.Kind == ConsoleContextKind.Tenant);
                    var workspaces = group.Where(option => option.Kind == ConsoleContextKind.Workspace).ToArray();
                    var displayName = tenant?.DisplayName
                        ?? workspaces.FirstOrDefault()?.TenantDisplayName
                        ?? string.Empty;
                    return new ContextTenantGroup(group.Key, displayName, tenant, workspaces);
                })
                .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    private string CurrentSection
    {
        get
        {
            var path = "/" + NavigationManager.ToBaseRelativePath(NavigationManager.Uri).Split('?', '#')[0];
            return NavigationGroups.SelectMany(group => group.Items)
                .Where(item => item.Url == "/" ? path == "/" : path.StartsWith(item.Url, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Url.Length)
                .FirstOrDefault() is { } item ? T(item.LabelKey) : T("ControlPlane");
        }
    }

    private string CurrentContextPath => ContextState.Current is { } context
        ? context.SelectedKind switch
        {
            ConsoleContextKind.Instance => T("InstanceAdministration"),
            ConsoleContextKind.Tenant => context.TenantDisplayName,
            ConsoleContextKind.Workspace => $"{context.TenantDisplayName} / {context.WorkspaceDisplayName}",
            _ => T("OrganizationFallback")
        }
        : T("OrganizationFallback");

    protected override async Task OnInitializedAsync()
    {
        Navigation.Changed += StateHasChanged;
        Preferences.Changed += StateHasChanged;
        Notifications.Changed += StateHasChanged;
        PlatformStatus.Changed += OnPlatformStatusChanged;
        ContextState.Changed += OnContextChanged;
        NavigationManager.LocationChanged += OnLocationChanged;
        platformStatusRefresh = PlatformStatus.RefreshAsync(lifetimeCancellation.Token);
        await Task.WhenAll(
            ContextState.LoadAsync(lifetimeCancellation.Token),
            Preferences.LoadAsync(lifetimeCancellation.Token));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            commandModule = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Agentstration.Web.Components/command-palette.js");
            contextModule = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Agentstration.Web.Components/context-selector.js");
            if (ContextState.Current is { } context)
                await contextModule.InvokeVoidAsync("ensureContextUrl", context.SelectedScopeRef);
            themeModule = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Agentstration.Web.Components/theme-preferences.js");
            navigationTooltipModule = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Agentstration.Web.Components/navigation-tooltips.js");
            Preferences.SetSystemTheme(await themeModule.InvokeAsync<bool>("prefersDarkTheme"));
            themeReady = true;
            StateHasChanged();
            selfReference = DotNetObjectReference.Create(this);
            commandRegistration = await commandModule.InvokeAsync<IJSObjectReference>("registerCommandPalette", selfReference);
            navigationTooltipRegistration = await navigationTooltipModule.InvokeAsync<IJSObjectReference>("registerCollapsedNavigationTooltips", shellElement);
            if (CultureNavigation.NavigateToPreferredCulture(NavigationManager, Preferences.Language)) return;
        }

        if (focusCommandInput && commandRegistration is not null)
        {
            focusCommandInput = false;
            await commandRegistration.InvokeVoidAsync("focus", commandInput);
        }
    }

    [JSInvokable]
    public Task ToggleCommandPalette() => InvokeAsync(() =>
    {
        if (commandPaletteOpen) CloseCommandPalette();
        else OpenCommandPalette();
        StateHasChanged();
    });

    private void OpenCommandPalette()
    {
        commandPaletteOpen = true;
        commandQuery = string.Empty;
        resourceCommands = [];
        fallbackCommands = [];
        selectedCommandIndex = 0;
        focusCommandInput = true;
    }

    private void CloseCommandPalette()
    {
        commandPaletteOpen = false;
        commandQuery = string.Empty;
        resourceCommands = [];
        fallbackCommands = [];
        searchingResources = false;
        resourceSearchCancellation?.Cancel();
        selectedCommandIndex = 0;
    }

    private async Task UpdateCommandQuery(ChangeEventArgs args)
    {
        commandQuery = args.Value?.ToString() ?? string.Empty;
        selectedCommandIndex = 0;
        resourceCommands = [];
        fallbackCommands = [];
        resourceSearchCancellation?.Cancel();
        resourceSearchCancellation?.Dispose();
        resourceSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        var cancellationToken = resourceSearchCancellation.Token;
        if (commandQuery.Trim().Length < 2) { searchingResources = false; return; }

        try
        {
            await Task.Delay(250, cancellationToken);
            searchingResources = true;
            var resources = await ResourceSearch.SearchAsync(commandQuery, cancellationToken);
            resourceCommands = resources.Select(item => new CommandItem(
                item.Label,
                item.Url,
                item.Icon,
                item.ResourceType,
                item.SearchText ?? item.Identifier,
                $"{item.Status} · {ShortIdentifier(item.Identifier)}")).ToArray();
            if (FilteredCommands.Count == 0 && resourceCommands.Count == 0)
            {
                var fallbacks = await CommandFallback.ResolveAsync(commandQuery, cancellationToken);
                fallbackCommands = fallbacks.Select(fallback => new CommandItem(
                        F("AskEntry", fallback.Label, commandQuery),
                        fallback.Url,
                        fallback.Icon,
                        T("Entry"),
                        Detail: fallback.Detail,
                        IsFallback: true))
                    .ToArray();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) searchingResources = false;
        }
    }

    private void HandleCommandKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape") { CloseCommandPalette(); return; }
        if (DisplayedCommands.Count == 0) return;
        if (args.Key == "ArrowDown") selectedCommandIndex = (selectedCommandIndex + 1) % DisplayedCommands.Count;
        else if (args.Key == "ArrowUp") selectedCommandIndex = (selectedCommandIndex - 1 + DisplayedCommands.Count) % DisplayedCommands.Count;
        else if (args.Key == "Enter") ExecuteCommand(DisplayedCommands[Math.Clamp(selectedCommandIndex, 0, DisplayedCommands.Count - 1)]);
    }

    private void ExecuteCommand(CommandItem command)
    {
        CloseCommandPalette();
        NavigationManager.NavigateTo(command.Url);
    }

    private Task ToggleThemeAsync() => Preferences.ToggleThemeAsync(lifetimeCancellation.Token);

    private void ToggleContextSelector()
    {
        contextSelectorOpen = !contextSelectorOpen;
        if (!contextSelectorOpen) contextQuery = string.Empty;
    }

    private void UpdateContextQuery(ChangeEventArgs args) => contextQuery = args.Value?.ToString() ?? string.Empty;

    private async Task SelectContextAsync(ConsoleContextOption option)
    {
        if (contextModule is null || option.ScopeRef == ContextState.Current?.SelectedScopeRef) return;
        switchingContext = true;
        try
        {
            _ = await contextModule.InvokeAsync<bool>("selectContext", option.ScopeRef, T("ConfirmContextChange"));
        }
        catch (JSException exception)
        {
            Notifications.Add(new NotificationItem(Guid.NewGuid(), T("ContextChangeFailed"), exception.Message,
                DateTimeOffset.Now, UiStatus.Danger));
        }
        finally { switchingContext = false; }
    }

    private static string ShortIdentifier(string value) => value.Length <= 48 ? value : $"…{value[^47..]}";
    private bool CanNavigate(IReadOnlyList<string>? permissions) => permissions is null || permissions.All(ContextState.HasPermission);
    private string T(string key) => string.IsNullOrEmpty(key) ? string.Empty : Localizer[key];
    private string PlatformStatusLabel => T($"PlatformStatus.{PlatformStatus.Kind}");
    private string F(string key, params object[] arguments) => Localizer[key, arguments];
    private string ContextKindLabel(ConsoleContextKind kind) => T($"ContextKind.{kind}");
    private string SelectedContextLabel(ConsoleContextSnapshot context) =>
        context.Contexts.FirstOrDefault(option => option.ScopeRef == context.SelectedScopeRef)?.DisplayName
        ?? CurrentContextPath;
    private string ContextOptionPath(ConsoleContextOption option) => option.Kind switch
    {
        ConsoleContextKind.Instance => T("GlobalAdministration"),
        ConsoleContextKind.Tenant => T("TenantAdministration"),
        ConsoleContextKind.Workspace => option.TenantDisplayName ?? string.Empty,
        _ => string.Empty
    };

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args) => _ = InvokeAsync(async () =>
    {
        var requestedContext = ConsoleNavigationContext.GetScopeRef(args.Location);
        if (requestedContext is not null
            && ContextState.Current is { } current
            && !string.Equals(requestedContext, current.SelectedScopeRef, StringComparison.Ordinal))
        {
            await ContextState.LoadAsync(lifetimeCancellation.Token);
        }
        StateHasChanged();
        try
        {
            await JS.InvokeVoidAsync("window.scrollTo", 0, 0);
            if (contextModule is not null && ContextState.Current is { } context)
                await contextModule.InvokeVoidAsync("ensureContextUrl", context.SelectedScopeRef);
        }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
    });
    private void OnContextChanged() => _ = InvokeAsync(StateHasChanged);
    private void OnPlatformStatusChanged() => _ = InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        Navigation.Changed -= StateHasChanged;
        Preferences.Changed -= StateHasChanged;
        Notifications.Changed -= StateHasChanged;
        PlatformStatus.Changed -= OnPlatformStatusChanged;
        ContextState.Changed -= OnContextChanged;
        NavigationManager.LocationChanged -= OnLocationChanged;
        lifetimeCancellation.Cancel();
        if (platformStatusRefresh is not null)
        {
            try { await platformStatusRefresh; }
            catch (OperationCanceledException) { }
        }
        resourceSearchCancellation?.Cancel();
        resourceSearchCancellation?.Dispose();
        lifetimeCancellation.Dispose();
        try
        {
            if (commandRegistration is not null)
            {
                await commandRegistration.InvokeVoidAsync("dispose");
                await commandRegistration.DisposeAsync();
            }
            if (navigationTooltipRegistration is not null)
            {
                await navigationTooltipRegistration.InvokeVoidAsync("dispose");
                await navigationTooltipRegistration.DisposeAsync();
            }
            if (commandModule is not null) await commandModule.DisposeAsync();
            if (contextModule is not null) await contextModule.DisposeAsync();
            if (themeModule is not null) await themeModule.DisposeAsync();
            if (navigationTooltipModule is not null) await navigationTooltipModule.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        selfReference?.Dispose();
        GC.SuppressFinalize(this);
    }
}
