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

    private sealed record NavigationItem(string LabelKey, string Url, string Icon, string Domain = "neutral");
    private sealed record NavigationGroup(string LabelKey, IReadOnlyList<NavigationItem> Items);
    private sealed record CommandDefinition(string LabelKey, string Url, string Icon, string CategoryKey, string Keywords = "");
    private sealed record CommandItem(string Label, string Url, string Icon, string Category, string Keywords = "", string? Detail = null);

    private static readonly NavigationGroup[] NavigationGroups =
    [
        new("", [new("Nav.Overview", "/", "home")]),
        new("Group.Build", [new("Nav.Agents", "/agents", "agent", "agent"), new("Nav.ModelProfiles", "/modelprofiles", "layers", "model"), new("Nav.Flows", "/flows", "workflow", "flow"), new("Nav.Entries", "/entries", "entry", "work")]),
        new("Group.Operate", [new("Nav.Triggers", "/triggers", "clock", "work"), new("Nav.Deployments", "/deployments", "server", "runtime"), new("Nav.Tasks", "/tasks", "tasks", "work")]),
        new("Group.Runs", [new("Nav.AgentRuns", "/agent-runs", "play-circle", "execution"), new("Nav.FlowRuns", "/flow-runs", "flow-run", "flow"), new("Nav.RunEvents", "/run-events", "activity")]),
        new("Group.Configure", [new("Nav.WorkplaceSetup", "/workspaces", "layout-grid", "work"), new("Nav.Packs", "/packs", "package"), new("Nav.Tools", "/tools", "wrench", "tool"), new("Nav.ModelProviders", "/modelproviders", "cpu", "model"), new("Nav.RuntimeProfiles", "/runtimeprofiles", "cube", "runtime"), new("Nav.Secrets", "/secrets", "key")]),
        new("Group.System", [new("Nav.Extensions", "/extensions", "puzzle"), new("Nav.Organization", "/settings/organization", "building"), new("Nav.Bootstrap", "/settings/bootstrap", "upload-cloud"), new("Nav.Profile", "/settings/profile", "user-circle"), new("Nav.Settings", "/settings", "settings")])
    ];

    private static readonly CommandDefinition[] CommandDefinitions =
    [
        new("Nav.Overview", "/", "⌂", "Navigate"),
        new("Nav.Agents", "/agents", "◎", "Group.Build", "agent resources agents ressources"),
        new("Command.CreateAgent", "/agents/new", "+", "Command", "new nouveau agent"),
        new("Nav.ModelProfiles", "/modelprofiles", "◇", "Group.Build", "models modèles"),
        new("Command.CreateModelProfile", "/modelprofiles/new", "+", "Command", "new nouveau model modèle"),
        new("Nav.Flows", "/flows", "⌘", "Group.Build", "workflow designer flux conception"),
        new("Command.CreateFlow", "/flows/new", "+", "Command", "new nouveau workflow flux"),
        new("Nav.Entries", "/entries", "↳", "Group.Build", "workplace entry entrée"),
        new("Command.CreateEntry", "/entries/new", "+", "Command", "new nouvelle workplace entry entrée"),
        new("Nav.Deployments", "/deployments", "◉", "Group.Operate", "agent runtime deployments déploiements"),
        new("Nav.AgentRuns", "/agent-runs", "▶", "Group.Runs", "agent execution history exécution historique"),
        new("Nav.FlowRuns", "/flow-runs", "▷", "Group.Runs", "workflow executions flux exécutions"),
        new("Nav.Tasks", "/tasks", "✓", "Group.Operate", "work tasks supervision tâches"),
        new("Nav.Triggers", "/triggers", "◷", "Group.Operate", "schedule automation planification automatisation"),
        new("Nav.RunEvents", "/run-events", "≋", "Group.Runs", "persisted runtime flow activity événements"),
        new("Nav.ModelProviders", "/modelproviders", "⬡", "Group.Configure", "providers fournisseurs"),
        new("Nav.Tools", "/tools", "⌁", "Group.Configure", "tool catalog providers outils fournisseurs MCP AEP"),
        new("Command.CreateToolProvider", "/tools/providers/new", "+", "Command", "new nouveau MCP AEP provider fournisseur"),
        new("Nav.WorkplaceSetup", "/workspaces", "▦", "Group.Configure", "workspace composition primary entries espace composition"),
        new("Nav.Packs", "/packs", "▣", "Group.Configure", "package distribution install archive resources paquet installation"),
        new("Command.CreateModelProvider", "/modelproviders/new", "+", "Command", "new nouveau provider fournisseur"),
        new("Nav.RuntimeProfiles", "/runtimeprofiles", "◈", "Group.Configure", "runtime configuration exécution"),
        new("Command.CreateRuntimeProfile", "/runtimeprofiles/new", "+", "Command", "new nouveau runtime profile profil"),
        new("Nav.Secrets", "/secrets", "◆", "Group.Configure", "credentials Vaults write only secrets identifiants coffres"),
        new("Command.CreateSecret", "/secrets/new", "+", "Command", "new nouveau credential secret"),
        new("Command.Vaults", "/vaults", "▰", "Group.Configure", "secret storage providers coffres stockage"),
        new("Command.CreateVault", "/vaults/new", "+", "Command", "new nouveau secret storage coffre"),
        new("Nav.Settings", "/settings", "⚙", "Group.System", "configuration paramètres"),
        new("Command.ProfileSettings", "/settings/profile", "○", "Group.System", "appearance theme personal preferences apparence thème préférences"),
        new("Nav.Extensions", "/extensions", "⬢", "Group.System", "AEP option contracts compatibility extensions"),
        new("Nav.Organization", "/settings/organization", "♙", "Group.System", "tenant organization organisation"),
        new("Nav.Bootstrap", "/settings/bootstrap", "⇧", "Group.System", "bootstrap profiles configuration profils configuration"),
        new("Command.OrganizationWorkspaces", "/settings/organization/workspaces", "▦", "Group.System", "tenant workspaces espaces"),
        new("Command.OrganizationMembers", "/settings/organization/members", "♙", "Group.System", "users memberships roles membres rôles")
    ];

    private IJSObjectReference? commandModule;
    private IJSObjectReference? contextModule;
    private IJSObjectReference? themeModule;
    private IJSObjectReference? commandRegistration;
    private DotNetObjectReference<MainLayout>? selfReference;
    private ElementReference commandInput;
    private string commandQuery = string.Empty;
    private bool commandPaletteOpen;
    private bool focusCommandInput;
    private bool searchingResources;
    private int selectedCommandIndex;
    private IReadOnlyList<CommandItem> resourceCommands = [];
    private CancellationTokenSource? resourceSearchCancellation;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private IReadOnlyList<CommandItem> Commands => CommandDefinitions.Select(command => new CommandItem(
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
            .ToArray();

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

    protected override async Task OnInitializedAsync()
    {
        Navigation.Changed += StateHasChanged;
        Preferences.Changed += StateHasChanged;
        Notifications.Changed += StateHasChanged;
        PlatformStatus.Changed += StateHasChanged;
        ContextState.Changed += OnContextChanged;
        NavigationManager.LocationChanged += OnLocationChanged;
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
            themeModule = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Agentstration.Web.Components/theme-preferences.js");
            Preferences.SetSystemTheme(await themeModule.InvokeAsync<bool>("prefersDarkTheme"));
            selfReference = DotNetObjectReference.Create(this);
            commandRegistration = await commandModule.InvokeAsync<IJSObjectReference>("registerCommandPalette", selfReference);
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
        selectedCommandIndex = 0;
        focusCommandInput = true;
    }

    private void CloseCommandPalette()
    {
        commandPaletteOpen = false;
        commandQuery = string.Empty;
        resourceCommands = [];
        searchingResources = false;
        resourceSearchCancellation?.Cancel();
        selectedCommandIndex = 0;
    }

    private async Task UpdateCommandQuery(ChangeEventArgs args)
    {
        commandQuery = args.Value?.ToString() ?? string.Empty;
        selectedCommandIndex = 0;
        resourceCommands = [];
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

    private bool switchingWorkspace;
    private Task ToggleThemeAsync() => Preferences.ToggleThemeAsync(lifetimeCancellation.Token);
    private async Task SelectWorkspaceAsync(ChangeEventArgs args)
    {
        if (contextModule is null || !Guid.TryParse(args.Value?.ToString(), out var workspaceId) || workspaceId == ContextState.Current?.WorkspaceId) return;
        switchingWorkspace = true;
        try { await contextModule.InvokeVoidAsync("selectWorkspace", workspaceId); }
        finally { switchingWorkspace = false; }
    }

    private static string ShortIdentifier(string value) => value.Length <= 48 ? value : $"…{value[^47..]}";
    private string T(string key) => string.IsNullOrEmpty(key) ? string.Empty : Localizer[key];
    private string F(string key, params object[] arguments) => Localizer[key, arguments];
    private static string WorkspaceLabel(ConsoleContextSnapshot context, ConsoleWorkspaceOption workspace) =>
        context.Workspaces.Select(value => value.TenantId).Distinct().Skip(1).Any()
            ? $"{workspace.TenantDisplayName} / {workspace.DisplayName}"
            : workspace.DisplayName;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args) => _ = InvokeAsync(async () =>
    {
        StateHasChanged();
        try { await JS.InvokeVoidAsync("window.scrollTo", 0, 0); }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
    });
    private void OnContextChanged() => _ = InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        Navigation.Changed -= StateHasChanged;
        Preferences.Changed -= StateHasChanged;
        Notifications.Changed -= StateHasChanged;
        PlatformStatus.Changed -= StateHasChanged;
        ContextState.Changed -= OnContextChanged;
        NavigationManager.LocationChanged -= OnLocationChanged;
        lifetimeCancellation.Cancel();
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
            if (commandModule is not null) await commandModule.DisposeAsync();
            if (contextModule is not null) await contextModule.DisposeAsync();
            if (themeModule is not null) await themeModule.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        selfReference?.Dispose();
        GC.SuppressFinalize(this);
    }
}
