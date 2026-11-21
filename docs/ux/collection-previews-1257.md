# Resource collection Actions, saved Summaries and Preview

Implementation of [#1257](https://github.com/gbaudrit/agentstration/issues/1257), starting at main `51969396`. The [adoption/exception matrix](collection-previews-1257.json) retains every frame and specialized surface from `collection-surfaces-1121.json`, including embedded and operational lists. A pending entry is unfinished work, not an exception. Each list is delivered in its own commit; existing Agents/Flows/Extensions implementations are reviewed as baselines.

## Ownership and navigation

`CollectionActions` and `ResourceCollectionPreview` own menu and modal/history presentation. Native hashchange/popstate is observed as well as Blazor navigation, so a closed Preview can reopen through the same fragment without a reload. The observer changes presentation only and never pushes/replaces navigation or bypasses dirty-form guards. `ResourceSummary` supplies the shared spacing and wrapping two-column saved-configuration layout in both full details and Preview. Each screen supplies its own authorized row actions and exact resource address. A family Preview owns the bounded scoped snapshot read, rejects returned owner/namespace/kind/name mismatches, and cancels/rejects superseded requests. It never scans collection pages or loads editor pickers. Summary receives saved values and independently supplied quick actions; it owns no reads or mutations.

Names and Open select `#overview`; Edit definition selects the existing definition form. `#preview/{kind}/{encoded-owner}/{namespace}/{name}` preserves the current collection query and supports direct/off-window targets. Open/Edit leave that history entry available for browser Back. Explicit close replaces the fragment; context replacement closes the old Preview. Existing return-to-list links remain explicit collection links and dirty-form guards remain in place.

Summaries reuse the template's Identity fragment in full details and render a complete copyable standalone Identity in Preview, including labels and applicable Extension provenance. Identity and Configuration initially open in Preview and remain independently foldable without unmounting child content or refetching. Preview hides the Configuration view selector while preserving automatic narrow-screen cards.

## Parameters

The list adds Open, authorized Edit definition and Preview. There is no row Delete command to duplicate the full editor's usage/ETag workflow. The shared saved Summary shows value type and readable value; it never resolves secrets or performs usage/picker reads. The endpoint only supports the default namespace, so other namespaces are rejected before any read. Exact owner is mandatory. The overview retains its existing usage metric and saved/draft lifecycle.

Validation: Console component/test project Release build passed with zero warnings/errors; 41 Parameter component/model/collection/resource-screen/Preview tests passed. Full offline and executable desktop/mobile checks remain pending. New reusable browser journeys remain deferred in native child [#1258](https://github.com/gbaudrit/agentstration/issues/1258) until the required maintainer UX validation and merge. Neither issue is closed by these implementation increments.

## Schema

Saved format, dialect, schema content and Source evidence. No formats catalog or version-history read in Preview. The family Preview uses `ISchemasClient.GetSchemaAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed with zero warnings/errors; 91 Schema tests passed, including exact-address Preview, bilingual disclosure and existing editor/collection regressions. Browser checks remain pending.
