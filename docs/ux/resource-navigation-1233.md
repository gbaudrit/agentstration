# Resource navigation component (#1233)

PR #1237 delivered the component-first increment from main `52c4b1da`. The #1235 follow-up integrates it through `ResourceDetailsTemplate` on the editable/scoped Agent route `/agents/{Name}`. Other family migrations and the reusable scenario #1234 remain separate work; their validation/merge gates remain in force.

## Configuration and ownership

`ResourceNavigation` renders a desktop menu and a mobile Sections button/right-hand drawer. It does not render the resource header, content, action bar or page grid. `ResourceDetailsTemplate` places its desktop menu in a sticky right-hand column and the shared `ResourceNavigationTrigger` in its header. `ExternalTriggerId`, `DrawerOpenChanged` and `OpenDrawerAsync()` reuse the same drawer and focus return; omitting the external trigger keeps standalone component use unchanged. `ExpandGroupsInitially` opens new groups once without overriding subsequent user collapse choices. The caller owns resource loading, permissions, models, drafts, commands and view rendering.

```razor
<ResourceNavigation @key="ResourceIdentity" Items="Sections" DefaultPath="overview"
                    SelectionChanged="path => selected = path" />

@code {
    private string? selected;
    private IReadOnlyList<ResourceNavigationItem> Sections =>
    [
        new("overview", Localizer["Overview"], "home"),
        new("definition", Localizer["Definition"], "settings") { Navigable = false },
        new("definition/configuration", Localizer["Configuration"], "forms"),
        new("definition/tools", Localizer["Tools"], "tool"),
        new("access", Localizer["Access"], "key") { Visible = canViewAccess },
        new("yaml", Localizer["Yaml"], "code")
    ];
}
```

Every item has a technical path, caller-localized label, existing Tabler catalog icon, and `Visible`, `Enabled`, `Navigable` flags. Primary paths contain one segment; children contain `parent/child`. Sibling order follows configuration. Primary parents must exist, paths must be unique, and grouping-only entries must be primary entries with children. Invalid shapes, identifiers, icons or defaults throw deterministic argument errors. Validation includes hidden items, avoiding delayed failures on visibility changes.

A navigable parent has an ordinary section link plus an independent expansion button. A grouping-only parent only expands/collapses; its direct fragment resolves to its first authorized navigable child. A directly selected child expands its parent. Explicit expansion/collapse does not navigate or mutate drafts. An enabled child of a disabled parent remains disabled. Hidden parents hide their descendants. `Visible` is presentation supplied by the caller, never a replacement for server authorization.

The screen places YAML last as an independent primary entry when viewing YAML is authorized. The generic component does not invent a YAML capability or a resource-specific hierarchy.

## Fragment-only URL contract

Paths use one or two lowercase ASCII kebab-case segments, each at most 64 characters; examples: `#overview`, `#definition/tools`, `#yaml`. Percent-encoded aliases, translated IDs, empty segments, uppercase, whitespace and deeper paths are invalid. Section links target the current canonical resource path and preserve namespace, ownership and administrative-context query values. Obsolete `view`/`tab` selectors are removed from generated links and never interpreted. Query-key identity comparison/grouping follows ASP.NET case-insensitive parsing; resource, owner and context values remain exact.

An absent fragment uses `DefaultPath` if allowed, otherwise the first visible/enabled navigable entry. Unknown, malformed, hidden, removed or disabled destinations use that same fallback and a generic localized notice, with no hidden destination echoed. Group destinations/defaults and invalid fragments are canonicalized with history replacement; explicit section navigation creates history entries. No allowed destination yields `CurrentPath == null`, a localized empty state and no protected view selection.

The component observes accepted Blazor location changes and public browser Navigation API entry changes (where supported). Real anchors preserve copy/new-tab/modified-click behavior. The small JS bridge only intercepts ordinary activation and observes responsive state; it uses the public `Blazor.navigateTo` API for section history and reuses the existing `ModalFrame`/dialog focus infrastructure. Guarded Back/Forward traversal is reported by `NavigationManager` after acceptance; the bridge does not race the guard with its own popstate listener. Other code should use the public Blazor navigation API rather than mutate `window.location.hash`. Resource/context navigation remains owned by Blazor. Selecting the active destination does not add a duplicate history entry.

`SelectionChanged(string?)` notifies the caller after client initialization and when the resolved section changes. It is the caller's view-selection input, not a resource loading or save command. `data-navigation-ready=true` exposes completed browser initialization. Keep a single navigation instance per exact resource identity; key it by canonical route/namespace/owner/context so an instance belonging to the previous resource does not normalize a different resource/context URL. Resource/context changes retain the application's existing leave guard.

## Drafts and accessibility

Keep the owning resource model and visited/stateful editors mounted during section changes (for example using `hidden` panes). Do not reload the saved representation on fragment changes. Retain `UnsavedNavigationGuard`; when obsolete selectors can occur on incoming URLs, exclude `view`/`tab` from that guard's resource-identity query comparison. This permits their removal without a discard prompt; their values never select a section. Draft recovery after a full document reload is outside this component.

Desktop uses native navigation links, `aria-current`, labeled expansion controls and nested lists. Mobile reuses the same configuration in a native modal drawer, with localized heading/close controls, Escape/outside dismissal, focus containment and return, and closure on accepted selection. Resizing an open drawer to desktop closes it and focuses the active rail entry; focused rail navigation moving to mobile transfers focus to Sections. Long labels wrap within the navigation width. Caller content/editor state is untouched by these transitions.

## Validation boundary

Component tests cover URL resolution, permissions, grouping, locale labels, retained owner state and late interop disposal. JS tests cover modified clicks, public fragment-history writes/entry observation, duplicate suppression, responsive focus notifications and disposal. Real browser checks use an isolated temporary component host, explicitly labeled as a specimen with no Agent data; those captures are not evidence of a production Agent layout. The new reusable Agent journey #1234 remains blocked until the layout/pilot is implemented, explicitly UX-validated and merged. Existing regressions and required functional lanes still run in this component PR.

## Pilot integration follow-up (#1235)

The component-first evidence below records the original native-hash/guard failure. The Agent integration cancels native hash pushes before commit through the browser Navigation API and sends them through the same public Blazor navigation path as menu links. Traversals remain owned by Blazor and the existing leave guard. External trigger focus, optional state and disposal retain the same component contract.

Default Console context normalization now also uses the public Blazor API, preserving application history state, instead of silently replacing browser history. Agent's exact resource/context key retains its navigation instance across fragments and replaces it when initial context coordinates become explicit. Initial resource loading is not a draft; preferred-culture redirects preserve the fragment.

The component observes public Navigation API events where supported. Standard component links apply their resolved selection directly; they do not require that optional observer. The pilot's browser smoke and native-hash probes cover the installed Edge/Chromium desktop/mobile environment. The original isolated-host captures and failing probe remain historical evidence at [evidence/1233](evidence/1233/README.md); they must not be mistaken for the follow-up result. New reusable scenario #1234 still waits for maintainer UX validation and merge.
