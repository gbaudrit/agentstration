# Shared form and theme contracts

The integration branch is `ux/unification-formulaires`; delivery is tracked by [#1089](https://github.com/gbaudrit/agentstration/issues/1089).

## Forms

`FormSection` and `FormGrid` compose existing panel/grid styles. `FormField` associates a localized label and optional help with one control through a stable generated ID. Its cascading presentation is consumed by `ClearableText`, `ClearableNumber<TValue>` and `ClearableSelect<TValue>`. Required/optional markers and aria-required share the field contract. Put field ValidationMessage content in the Validation slot so its message ID is associated with the input. Validation remains owned by the existing Blazor EditContext and domain model; use the original ValueExpression, normally supplied by `@bind-Value`, and the existing ValidationMessage.

`FieldClearButton` supplies a fixed-size SVG action shared by the input wrappers and the Model Profile summary. The clear action is inside the control wrapper, uses `type="button"`, retains focus and updates the same bound value as typing. It does not submit, delete a resource or choose a default. Nullable numeric/select values clear to absence. In an EditForm, clearing a required number or enum creates an invalid input and retains the last valid typed value until correction; it never selects zero or the first enum option. FormDraftMonitor protects this unparsed draft as well as actual model changes. Outside an EditForm, use an absence-capable value and explicit domain validation. Read-only and disabled controls cannot be cleared. Secret replacement inputs retain their existing no-replacement semantics; clearing must never become secret deletion.

`FormActions` provides presentation and busy semantics; the caller still owns domain operations. Do not treat it as a draft/saving state machine. The Agent editor deduplicates reloads for the same resource and normalized ownership/context coordinates. Resource editors retain visited form panels while hidden so parser state and automatic/manual naming survive tab changes. UnsavedNavigationGuard permits only declared view-query changes (tab on Model Profile); ownership/context changes still require confirmation. Editors supply a live HasUnsavedChanges evaluator so a completed save can navigate before the next parent render without a stale confirmation. Specialized JSON Schema and typed binding editors remain specialized components.

## Collections

`CollectionPager` uses one-based page numbers. The caller supplies HasMore and may supply an authorized exact Total. Omit Total when only continuation or a bounded window is known; do not substitute the number of loaded rows. Disable paging while an authoritative request is pending. For a loaded collection with an exact total, refresh clamps the current page to the remaining range; continuation-only consumers retain a usable previous/reset route. Query changes reset the page; the caller cancels or ignores obsolete responses. A pager does not make an unbounded endpoint bounded.

Reuse ResourceToolbar/SearchBox/FilterBar and DataGrid or the existing mobile card presentation. Family adapters own eligibility, scope, authorization and query transport; the shared UI never invents a generic resource query service.

## Light and dark themes

Light and Dark are the first two shipped themes. The existing Light/Dark/System preference chooses one; System resolves the operating-system preference and is not a third palette. The shell exposes the resolved theme through `data-theme="light|dark"` and its existing theme class. Future brand themes can define corresponding palettes without changing component behavior or adding a parallel preference store.

Override semantic variables on the shell after the base styles. Shared components resolve surface/text/border colors, `--form-focus-color`, `--form-error-color`, `--form-gap`, `--form-section-gap`, `--form-radius`, `--form-action-size` and `--collection-gap` from the active shell. Existing `--field-*`, `--space-*` and `--radius-*` tokens remain supported. Avoid fixed palette values in new component rules.

`SchemaBindingEditor`, shared by Agent, Flow and ToolDefinition contracts, uses `--surface-2`, `--surface`, `--border`, `--text` and `--muted` for its container and empty state. Its active source-mode button pairs `--primary` with `--color-action-on-primary`. These colors follow theme changes without light-only fallback values. See the [contract theme verification](contrats-themes/README.md).

For example, an application-owned stylesheet can override these values without changing a field or collection:

```css
.app-shell[data-brand="example"] {
  --color-action-primary: #6d28d9;
  --color-action-primary-soft: #ede9fe;
  --color-action-on-primary: #fff;
  --form-radius: 14px;
  --field-radius: 14px;
  --form-gap: 1.25rem;
  --collection-gap: 1rem;
}
.app-shell.theme-dark[data-brand="example"] {
  --color-action-primary: #c4b5fd;
  --color-action-primary-soft: #2e1065;
  --color-action-on-primary: #08111f;
}
```

This is an override example, not a shipped theme selector or a new persisted preference. Named-theme authoring, distribution and selection remain future work. Validate contrast, keyboard focus, both appearances and mobile layout for each real theme; changing a primary color alone is not sufficient evidence.

## Bounded model collections

The Console client uses `GET /api/modelprofiles/queries/lookup` for inline profile choices and `GET /api/modelprofiles/{name}/usages/page` for authorized direct consumers. Continuations are protected and query-bound; existing list/usages endpoints remain compatible. Choice rows omit usage totals rather than report a fabricated zero. The shared summary contract represents an unknown UsageCount with null; legacy list responses still provide their known totals.

Models owns IModelProfileLookupQueries; Agents owns IAgentProfileUsageQueries. Existing SQLite/PostgreSQL adapters implement these narrow read ports without new project dependencies. Search filters run before storage paging. SQLite uses ordinal case-insensitive matching, including accented names; PostgreSQL uses its ILIKE semantics. Existing reference resolution verifies candidate consumers against the exact profile UID, so homonyms do not become usages. Authorization is reapplied on every request.

Each lookup materializes at most 20 candidates; usage requests inspect at most eight authorized scopes with 20 candidates each and return at most one result page. Empty scopes are skipped within that bound. Unknown totals stay unknown. These bounds limit materialization, resolution and payload, not the cost of a database search scan; no new search-index or latency guarantee is claimed. New providers can retain the bounded enumeration fallback until they implement the family ports.

The inline Model Profile picker presents one summary with its actions and opens a filtered collection below it. A current choice is identified and disabled as a redundant action. Each choice button describes its exact profile identity. Opening focuses search, choosing returns focus to the disclosure, and a single-page result omits inactive pagination. The [correction report](corrections-champs-selection/README.md) includes visual evidence and validation.

## Resource composition

`ResourceIdentitySummary` presents the common read-only identity, explicit empty description, optional owner scope/namespace and short badges. Family-specific configuration stays in adjacent `FormSection` panels. Model Profile demonstrates this summary. Agent exposes identity and description in its header and omits a duplicate overview identity panel. `CopyableValue` copies only its supplied value through the clipboard module and reports success/failure accessibly.

`ResourceHeader` composes `ResourceIcon` and `ResourceActionBar`. Icons accept an image URL (SVG/PNG/WebP through an image element), an optional dark-theme variant, a catalog name or trusted caller content. Images keep their proportions and fall back to the family icon on failure; no arbitrary SVG markup is injected. This is the rendering contract only: persisted icon metadata, Bootstrap asset lifecycle and automatic type inheritance remain owned by [#1110](https://github.com/gbaudrit/agentstration/issues/1110). Header actions support primary, secondary and optional overflow content; each screen retains permissions and commands.

The resource icon aligns with the top of the title rather than the center of the title/description/metadata block. An optional `Namespace` qualifies `TechnicalName` into one final, folder-marked identity chip. It displays and copies `namespace/name`, with no spaces around the slash; omitted namespaces preserve the existing unqualified copy behavior. The full value remains available through the chip and copy-button tooltips when the path is truncated. See the [qualified header identity](entete-identite-qualifiee/README.md).

`LabelsEditor` edits the existing newline-separated key=value binding as explicit rows. It keeps invalid drafts, reports missing/invalid/duplicate keys in the EditContext, and never replaces duplicate values silently. `LabelsSummary` renders saved maps as wrapping badges. Agent is the first adopter; the transport schema is unchanged.

The Agent Definition tab groups Identity, Behavior, Model, Execution and Labels. Overview owns saved configuration and desired/active comparison. Discard restores the last saved resource snapshot, replaces the EditContext and child input state, clears validation/YAML drafts and retains the selected tab. `UnsavedChangesBar` is a neutral sticky action surface that clears the mobile navigation height through `--mobile-navigation-height`; it does not own the restoration operation. `PageNotification` presents dismissible, eight-second action feedback, separate from persisted state; the notification center retains session history.

The existing `ResourceAccessPanel` puts permissions ahead of collapsible identity/governance details. It searches the authorized target inventory, renders at most 50 matching options with an honest count, and keeps exact scope coordinates. This bounds the selector rendering, not the existing inventory API. Discover/use/propagation remain independent rights with existing domain validation. Cancel restores the last loaded/saved policy without calling the API; saves preserve the ETag contract and read-only permissions.

## Incomplete label drafts and YAML

`LabelsEditor` ignores entirely empty placeholder rows. Partial/duplicate entries remain local and mark the EditContext invalid without publishing an unserializable map into the resource binding. Publishing resumes only for a valid set of non-empty rows. Keep the inline row feedback; do not add a second field-level ValidationMessage beneath this editor.

The Agent YAML view uses the saved definition while the form has invalid values and remains read-only until the form is corrected. The incomplete form stays mounted, and Apply/Save handlers also enforce that guard. The shared header places type/name/state on one line and compact metadata below it. The default section gap is 12 px; theme overrides remain supported. See the [graphical correction report](corrections-affichage/README.md).

## Header references and direct navigation

`ResourceHeader.TechnicalName` is rendered after the caller's metadata; the full value remains copyable while the visible code may ellipsize. Description is a single line with a full native tooltip. Header flex items are width-constrained so long identifiers/descriptions cannot widen the mobile document.

The Agent overview omits duplicate Identity and Execution panels. `StateComparison.RuntimeUrl` can link the runtime name when the active revision provides an exact name/namespace; absent an authoritative destination, keep plain text. The declared/resolved Model Profile cards and selected-profile summary link resource names directly instead of adding separate Open/View actions. Definition retains execution configuration. See [review captures](references-directes/README.md).
