# Resource navigation component (#1233)

This increment starts from main `52c4b1da` and delivers the navigation component first, as requested by the maintainer. The resource-detail layout, Agent pilot, family migrations and reusable scenario #1234 remain subsequent work. No production route is migrated by this PR. Existing flat `ResourcePageTabs` and `ResourceTabs` consumers are unchanged.

## Configuration and ownership

`ResourceNavigation` renders a desktop menu and a mobile Sections button/right-hand drawer. It does not render the resource header, content, action bar or page grid. The future layout places its desktop menu in the right-hand column. The caller owns resource loading, permissions, models, drafts, commands and view rendering.

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

Paths use one or two lowercase ASCII kebab-case segments, each at most 64 characters; examples: `#overview`, `#definition/tools`, `#yaml`. Percent-encoded aliases, translated IDs, empty segments, uppercase, whitespace and deeper paths are invalid. Section links target the current canonical resource path and preserve namespace, ownership and administrative-context query values. Obsolete `view`/`tab` selectors are removed from generated links and never interpreted.

An absent fragment uses `DefaultPath` if allowed, otherwise the first visible/enabled navigable entry. Unknown, malformed, hidden, removed or disabled destinations use that same fallback and a generic localized notice, with no hidden destination echoed. Group destinations/defaults and invalid fragments are canonicalized with history replacement; explicit section navigation creates history entries. No allowed destination yields `CurrentPath == null`, a localized empty state and no protected view selection.

The component observes Blazor location changes and native hash/history changes. Real anchors preserve copy/new-tab/modified-click behavior. The small JS bridge only intercepts ordinary activation and observes responsive state; it uses native hash history for sections, observes Blazor resource-location changes and reuses the existing `ModalFrame`/dialog focus infrastructure. Resource/context navigation remains owned by Blazor. Selecting the active destination does not add a duplicate history entry.

`SelectionChanged(string?)` notifies the caller after client initialization and when the resolved section changes. It is the caller's view-selection input, not a resource loading or save command. `data-navigation-ready=true` exposes completed browser initialization. Keep a single navigation instance per exact resource identity; key it by canonical route/namespace/owner/context so an instance belonging to the previous resource does not normalize a different resource/context URL. Resource/context changes retain the application's existing leave guard.

## Drafts and accessibility

Keep the owning resource model and visited/stateful editors mounted during section changes (for example using `hidden` panes). Do not reload the saved representation on fragment changes. Retain `UnsavedNavigationGuard`; when obsolete selectors can occur on incoming URLs, exclude `view`/`tab` from that guard's resource-identity query comparison. This permits their removal without a discard prompt; their values never select a section. Draft recovery after a full document reload is outside this component.

Desktop uses native navigation links, `aria-current`, labeled expansion controls and nested lists. Mobile reuses the same configuration in a native modal drawer, with localized heading/close controls, Escape/outside dismissal, focus containment and return, and closure on accepted selection. Resizing an open drawer to desktop closes it and focuses the active rail entry; focused rail navigation moving to mobile transfers focus to Sections. Long labels wrap within the navigation width. Caller content/editor state is untouched by these transitions.

## Validation boundary

Component tests cover URL resolution, permissions, grouping, locale labels, retained owner state and late interop disposal. JS tests cover modified clicks, hash/history observation, responsive notifications and disposal. Real browser checks use an isolated temporary component host, explicitly labeled as a specimen with no Agent data; those captures are not evidence of a production Agent layout. The new reusable Agent journey #1234 remains blocked until the layout/pilot is implemented, explicitly UX-validated and merged. Existing regressions and required functional lanes still run in this component PR.
