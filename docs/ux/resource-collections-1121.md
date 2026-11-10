# Resource collection adoption (#1121)

Implementation scope: the five routes below, on `ux/unification-formulaires`.
The reusable cross-family browser scenario remains deferred in [#1122](https://github.com/gbaudrit/agentstration/issues/1122) until explicit maintainer UX acceptance and merge. This implementation does not close either issue.

`CollectionView<TItem>` uses the existing `DataGrid` for tables and a semantic list of cards with labelled definition lists. It renders one view at a time. Narrow screens initially select cards; an accessible view switch retains the user's choice through resizing. Family adapters own requests, filters, error/loading/empty states, commands and confirmations. `CollectionValue` reuses each family's information template in both renderers.

| Route / adapter | Business information retained | State / governance | Final Actions | Data contract and aliases |
| --- | --- | --- | --- | --- |
| `/agents` / `Agents` | Handler/definition, generation, exact Model Profile link, deployment link and operational state, last activity | Existing provisioning labels (Accepted → Valid, Succeeded → Published, Failed → Failed); Unknown neutral. Managed marker separate. | Open, existing default-namespace Run, Edit definition and Delete where editable/authorized, Quick inspect. Delete keeps ETag and confirmation. | Existing loaded batch: Agents `top=1000`, Deployments `top=1000`; local search/status filters. No new paging/fetch-all. `/agents/{name}` and `/namespaces/{namespace}/agents/{name}`, including Run destinations. |
| `/modelprofiles` / `ModelProfiles` | Provider, model, known usage count or `—` | Existing readiness/provider/model/configuration states; Unknown neutral. Managed marker separate. | Open; Delete when permitted, unmanaged and in owner context. Existing usage conflict and ETag confirmation flow. | Existing filtered `ValueResponse` catalog. Search/provider/status arguments unchanged; no manufactured total or continuation. `/modelprofiles/{name}?namespace=…&scopeRef=…`. |
| `/data-source-profiles` / `DataSourceProfiles` | Managing Extension and distinct Extension/Bundle versions and link, active version, draft version | No business state. Managed/read-only is a separate lock marker, not a state column. | Existing Open. Detail access-policy administration remains available under its existing rules. | Existing catalog plus one shared Extension inventory load; ownership lookup in memory. `/data-source-profiles/{name}?namespace=…&scopeRef=…&context=…`. |
| `/runtimeprofiles` / `RuntimeProfiles` | Runtime type, execution/session mode, streaming, deployment usages | No business state. Managed marker separate. | Existing Open. | Existing `ValueResponse` catalog, Instance ownership. `/runtimeprofiles/{name}?namespace=…`. |
| `/flows` Definitions / `Flows` | Flow kind, version, update time | Existing Active/Disabled meanings; unrecognized/Unknown tone neutral. Managed marker separate. | Open (also the first display-name link). | Existing first batch `top=100`; no fabricated total or implicit continuation fetch. `/flows/{id}` and `/namespaces/{namespace}/flows/{id}`. Runs and Deployments navigation retained. |

All five use `CollectionIdentity` → `ResourceScopeBadge` → applicable `StatusBadge` → family information → `CollectionActions`. Identity uses the existing `ResourceIcon` renderer, a primary display-name anchor and a folder-marked secondary qualified name. Copy writes the complete invariant `namespace/name`. Copy and action controls are siblings of the anchor and stop click propagation. Long names truncate visually; complete text remains in the DOM, title and clipboard, and qualified identities expand on keyboard focus.

Owner names come exclusively from the already-authorized `ConsoleContextState` snapshot. Collection adapters opt into `ResourceScopeBadge.ShowName`; other callers retain their existing compact presentation. There is no per-row name resolution or invented name for an inaccessible owner. Type and name are distinct from namespace. Long tenant/workspace names use a bounded single line, complete screen-reader text, hover/focus disclosure and a tap-pinned small native popover; Escape and leaving focus dismiss it. Native top-layer panels avoid clipping in horizontally scrollable tables. Instance is named by its localized type.

The final Actions disclosure has native Enter/Space activation, arrow/Home/End navigation, Escape, outside dismissal, Tab exit and focus restoration. Command-specific permissions, management restrictions and detail-page access policies remain family-owned. Shared tables preserve local horizontal scrolling; mobile defaults to cards without dropping identity, scope, applicable state or actions.

## Parallel work and shared ownership

Base: `1ec6d94` (#1114), including #1097. No open PR targeted the base branch when implementation started. Potential overlaps to reconcile before merge:

- #1092/#1093/#1094: Model Profile list/editor and usage/query work. This change touches the collection adapter and adds only an additive summary `IsManaged` field; it does not change pickers, usage queries or editor workflows.
- #1095: bounded collection adoption. The new frame has no query/pagination service; existing bounds and total semantics stay in each adapter.
- #1110: `ResourceIcon` artwork and inheritance. The renderer and storage are unchanged; collections supply family fallbacks only.
- Shared components: `ResourceScopeBadge` (name opt-in), `StatusBadge` (decorative semantic icon), `CopyableValue` (complete-value disclosure and click isolation), `SharedFormStrings`, collection CSS/JS. No changes to `ResourceActionBar`, resource headers or the form framework.
- Existing governed Extension inventory page object accepts table rows and cards; no new reusable journey is introduced.

## Validation evidence

Validation results and real-application captures are recorded in the implementation PR and the issue progress update. The evidence must be reviewed before UX acceptance; component tests and smoke checks do not satisfy the deferred scenario gate.
