# Authoring resource list screens

This guide implements the page-composition part of [#1241](https://github.com/gbaudrit/agentstration/issues/1241). The first adopters are Connected Services (`/connected-services`) and Flows (`/flows`). Persisted resource details follow [their separate template](resource-screen-authoring.md); creation uses `CreationWizard`.

## Required composition

Use `src/Agentstration.Web.Components/ResourceCollectionTemplate.razor`. Supply localized declarations and screen-owned query results; do not rebuild its header, toolbar, state branches or pager in the page. Keep global Console navigation in the application shell. A list has full-width content and no resource-detail navigation rail.

The template imposes this order:

1. `PageHeader`: title, optional useful description, authorized primary and secondary commands. No redundant Agentstration eyebrow.
2. Optional family navigation, only where the screen has distinct, legitimate collection destinations. The Flows pilot deliberately has no Definitions/Runs/Deployments tabs; these unrelated destinations remain in global navigation.
3. Optional adaptive metric grid, using actual authorized `MetricCard` values.
4. Optional operation feedback.
5. One collection control region: search, declared direct filters, More filters, optional Apply, Refresh and the existing table/card selector.
6. Additional filter controls when expanded, applied-filter chips and relevant load/refresh feedback.
7. Results in the existing `CollectionView<TItem>`, or one shared loading/error/empty state.
8. Capability-aware `CollectionPager` when meaningful.

Spacing and responsive wrapping belong to the template. Do not add page-specific CSS for common toolbar/panel gaps. Family cell content may retain meaningful styles, such as Flow publication alignment.

## Required components

Paths below are relative to `src/Agentstration.Web.Components/`.

| Element | Component and responsibility |
| --- | --- |
| Page composition | `ResourceCollectionTemplate.razor`: required ordering, named regions, state presentation, filter controls and responsive spacing. |
| Header | `PageHeader.razor`, constructed by the template. Supply `PrimaryActions` and `Actions`; use existing permission gates and family callbacks. |
| Optional metrics | `MetricCard.razor` in `Metrics`. Do not turn a loaded-page count into a global metric. |
| Search and filters | `ResourceToolbar.razor`, `SearchBox.razor`, `CollectionFilterField.razor`, `FormField.razor`, `ClearableText.razor`, `ClearableSelect.razor`. The template constructs them from declarations. |
| Table and cards | `CollectionView.razor`, retaining its existing `DataGrid.razor` and semantic card list. One representation renders at a time. The same declarations supply headings, labels and cell values. |
| Identity | `CollectionIdentity.razor` with `ResourceIcon`, exact qualified copy and applicable scope/status/governance badges. Preserve the [collection contract](resource-collections-1121.md). |
| Information | `CollectionColumn<TItem>` content is wrapped by `CollectionValue.razor` in either representation. Use `CollectionVersion.razor` for actual publication/generation facts. |
| Row commands | `CollectionActions.razor` in `ItemActions`. Preserve primary/secondary commands, authorization, confirmations and ETags. |
| States | `ResourceLoadState`, `ResourceLoadFeedback.razor`, `LoadingState.razor`, `EmptyState.razor`, `ErrorState.razor`. Collection wording comes from the shared English/French catalog. |
| Pagination | `CollectionPagination` and `CollectionPager.razor`. Declare successful page facts and real navigation capabilities. |

## Columns and filters

`IdentityColumn` is required. `Columns` contains the remaining information columns. Each `CollectionColumn<TItem>` has a stable nonempty `Key`, localized `Label`, `RenderFragment<TItem> Content`, optional specialized `Heading` and explicit `Filters`. Column keys and filter keys must each be unique within the page. Commands are supplied separately and are never filterable.

A filter is opt-in. Associate it with the column that presents its information, including identity metadata such as the Availability badge. Do not derive a filter from HTML, infer supported server operators or duplicate a state column merely to display its filter.

`CollectionFilter` declares a stable query key, localized label, control kind and authorized options. Supported kinds are Text, Choice, Number and DateRange. Choice values and date/number payloads remain invariant; localize their presentation. For DateRange, `CollectionFilterValue.Value` is the start and `End` is the end. Families validate and evaluate the values according to their actual query contract.

- Column filters appear directly unless `Advanced` is set.
- `AdditionalFilters` are independent declarations under More filters, for example namespace when it has no separate information column.
- Search is separate from column filters and only appears when `SearchEnabled` is true. Supply a localized label and useful placeholder.
- Applied criteria remain visible as removable chips, including additional filters while their panel is closed. Reset clears search and all filters while preserving page size.
- `ApplyMode` defaults to Immediate. Explicit mode retains draft control values and invokes `QueryChanged` only on Apply; chips continue to describe applied criteria. Explicit chip removal/reset is an immediate query action.

Example declaration from a family adapter:

```csharp
new CollectionColumn<ServiceSummary>("type", strings["Type"], renderType)
{
    Filters = [new("type", strings["TypeFilter"], CollectionFilterKind.Choice)
    {
        Placeholder = strings["AllTypes"],
        Options = authorizedTypes.Select(type => new CollectionFilterOption(type.Id, type.Label)).ToArray()
    }]
};
```

This declaration describes presentation and a query key, not a predicate or endpoint. The adapter owns local evaluation or server query arguments. Never filter just one loaded server page while presenting the result as a full-query match.

## Query and pagination ownership

`CollectionQuery` is requested presentation state: `Search`, `Filters`, one-based `Page` and `PageSize`. Treat its filter dictionary as an immutable snapshot; `WithFilter` copies it and removes empty values. Keep the current query instance stable until a real query change. The template invokes `QueryChanged` with a new snapshot. The adapter assigns it before awaiting I/O and supplies successful results separately.

Search/filter application and page-size changes reset to page one. Page navigation and refresh retain applied criteria. Table/card changes never execute a query or mark a business edit form dirty. The same `CollectionView` remains mounted through temporary empty/loading/error states so the selected view survives; changing `ContextKey` deliberately resets local presentation for the new identity.

`CollectionPagination` describes the successful visible page rather than an unfulfilled request:

| Contract | Declaration and behavior |
| --- | --- |
| Exact total with direct paging | Set `Total`, `SupportsDirectNavigation = true`, actual `Page`, `PageSize`, `HasMore` and applicable sizes. A bounded set of page buttons is shown, even for a large total. |
| Offset paging without total | Leave `Total` null and direct navigation false. Previous/next use actual page facts. A loaded-row count is explicitly labelled loaded, never a total. |
| Cursor/continuation | Leave direct navigation false; set `HasPrevious` only if the adapter has a prior cursor. Map sequential page callbacks to family-owned cursor history. Do not invent arbitrary jumps or a global total. |
| No pagination capability | Omit `Pagination`. Do not fetch all server pages to emulate local paging. |

An empty first complete page has no grid or irrelevant pager. An empty continuation window retains useful page controls and explicitly says the page is empty; it must not claim that the whole catalog is empty.

## States, authorization and retained results

Pass `LoadState` and family-owned `OnRefresh`/`OnRetry`. Initial loading and initial failure block rows. Empty collection and no matches have separate headings and actions. Supply only authorized creation controls through `EmptyActions`; no-match reset is shared. Access denied is not an empty collection and suppresses result rows and header commands.

During a same-query refresh, preserve the last valid rows and selected view. Set Refreshing with `HasContent = true`; on expected failure use Failed with that same content flag and actionable retry. Keep the successful pager facts when a requested next page fails, so prior rows are not labelled as the failed page. A new query whose criteria no longer match the visible rows must clear or explicitly distinguish them.

The adapter owns cancellation and request revisions. Check the latest request, Principal and selected scope before accepting a response. Clear rows, option sources and incompatible filters immediately when that identity changes, and clear cached data on denied access. Never display another context's rows/counts during a replacement load. The pilots observe `ConsoleContextState`, include Principal/scope in `ContextKey`, preserve exact resource links and gate reads/creation using the existing permissions.

The template contains no resource client, repository, automatic retries, business predicates or mutation command. Family-specific dialogs remain outside it when needed.

For resources physically stored in an internal operational Workspace, present the logical Instance/Tenant/Workspace administrative scope. Internal operational Workspace names, IDs and badges do not belong in collection identities, summaries or filter choices. Keep the physical coordinates internally for exact queries and commands; an inherited Flow target's owner is a separate fact, not the Trigger's scope. Follow the [resource-screen operational Workspace rule](resource-screen-authoring.md#internal-operational-workspaces) and verify all three administrative modes.

## Pilot data contracts

| Pilot | Query and count semantics |
| --- | --- |
| Connected Services | The existing `GET /api/connected-services` returns the complete authorized catalog array through `ConnectedServiceManagementService.ListAsync`/`store.ListAllAsync`. The adapter searches names and filters Type/Availability locally, adds namespace as an additional filter, then pages that snapshot. Its exact filtered total is therefore known. No new server query, broader request or fetch-all continuation loop is introduced. Existing catalog loading remains unchanged; this work does not claim server-side scaling improvements. Refresh preserves current filters and safely clamps a page if the snapshot shrinks. |
| Flows | The current API uses bounded `skip`/`top`, with UI sizes 20/50/100 and nextLink-derived `HasMore`. It has no global total or server filter contract. The pilot declares no search/filter controls and does not filter a loaded page locally. Failed page requests retain the previous successful page and retry the actual requested page. A removed terminal page triggers at most one additional request for page one, not a scan of all pages. The maintainer removed the list's former Definitions/Runs/Deployments tabs; global navigation and exact namespace/owner/context links remain intact. |

Supply `ItemActions` for the screen's meaningful navigation and commands. The Flows POC now provides Open, authorized Edit and Preview; it preserves its existing query and pagination contracts.

The [Agents and Flows actions/Summary POC](agents-summary-poc.md) explores the next convention: the name and Open select Overview, Edit selects the definition form, and each list retains its own action set independently of Summary quick actions. Preview is mounted on demand and recorded in a fragment containing the exact owner, namespace and resource name, so Back can reopen it without searching the loaded page. Do not infer commands from a rendered Summary. Connected Services keeps its existing composition.

For destructive row commands, supply the optional `CollectionActions.Destructive` fragment only when applicable and authorized. The shared component places its separator and final group; `ShowDestructiveActions="false"` hides both. Omitting that region never creates a Delete command or an empty separator. The Agents POC exposes the choice through `ShowDeleteAction`; a presentation flag never grants a permission. Existing `Primary`, `Secondary` and unnamed child content remain supported.

Future query support belongs to the family API/application boundary. Add an explicit filter declaration only once its evaluation over the declared query is real and tested.

## Validation and adoption

Maintain affected existing component and browser regressions. Focused template tests cover column/additional filters, explicit apply, dates/numbers, reset, localized empty/error states, retained view, known totals and continuation controls. Pilot cases cover complete-catalog filtering, snapshot shrink, failed refresh/page retry, permissions, superseded responses and context replacement. Verify desktop/mobile against the deterministic executable and report actual checks and limitations.

The collection source inventory counts each concrete template adapter as one shared frame; the generic template, filter options and pagination controls are not separate functional collections. Do not claim historical screenshots validate the new page composition.

The new reusable browser scenario is tracked separately in native child [#1242](https://github.com/gbaudrit/agentstration/issues/1242). It remains blocked until explicit maintainer UX validation and implementation merge. This does not defer required smoke checks or repairs to existing regressions. Do not close #1241 until the child and coverage catalog are complete, or the maintainer records a reasoned waiver.
