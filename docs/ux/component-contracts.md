# Shared form and theme contracts

The integration branch is `ux/unification-formulaires`; delivery is tracked by [#1089](https://github.com/gbaudrit/agentstration/issues/1089).

## Forms

`FormSection` and `FormGrid` compose existing panel/grid styles. `FormField` associates a localized label and optional help with one control through a stable generated ID. Its cascading presentation is consumed by `ClearableText`, `ClearableNumber<TValue>` and `ClearableSelect<TValue>`. Required/optional markers and aria-required share the field contract. Put field ValidationMessage content in the Validation slot so its message ID is associated with the input. Validation remains owned by the existing Blazor EditContext and domain model; use the original ValueExpression, normally supplied by `@bind-Value`, and the existing ValidationMessage.

The clear action is inside the control wrapper, uses `type="button"`, retains focus and updates the same bound value as typing. It does not submit, delete a resource or choose a default. Nullable numeric/select values clear to absence. In an EditForm, clearing a required number or enum creates an invalid input and retains the last valid typed value until correction; it never selects zero or the first enum option. FormDraftMonitor protects this unparsed draft as well as actual model changes. Outside an EditForm, use an absence-capable value and explicit domain validation. Read-only and disabled controls cannot be cleared. Secret replacement inputs retain their existing no-replacement semantics; clearing must never become secret deletion.

`FormActions` provides presentation and busy semantics; the caller still owns domain operations. Do not treat it as a draft/saving state machine. Resource editors retain visited form panels while hidden so parser state and automatic/manual naming survive tab changes. UnsavedNavigationGuard permits only declared view-query changes (tab on Model Profile); ownership/context changes still require confirmation. Editors supply a live HasUnsavedChanges evaluator so a completed save can navigate before the next parent render without a stale confirmation. Specialized JSON Schema and typed binding editors remain specialized components.

## Collections

`CollectionPager` uses one-based page numbers. The caller supplies HasMore and may supply an authorized exact Total. Omit Total when only continuation or a bounded window is known; do not substitute the number of loaded rows. Disable paging while an authoritative request is pending. Query changes reset the page; the caller cancels or ignores obsolete responses. A pager does not make an unbounded endpoint bounded.

Reuse ResourceToolbar/SearchBox/FilterBar and DataGrid or the existing mobile card presentation. Family adapters own eligibility, scope, authorization and query transport; the shared UI never invents a generic resource query service.

## Future named themes

Appearance remains the existing Light/Dark/System user preference. A future named theme is a separate visual identity. Do not extend the appearance enum with brand names or change its API in a component refactor.

Override semantic variables on the shell after the base styles. Shared components resolve surface/text/border colors, `--form-focus-color`, `--form-error-color`, `--form-gap`, `--form-section-gap`, `--form-radius`, `--form-action-size` and `--collection-gap` from the active shell. Existing `--field-*`, `--space-*` and `--radius-*` tokens remain supported. Avoid fixed palette values in new component rules.

For example, an application-owned stylesheet can override these values without changing a field or collection:

```css
.app-shell[data-theme="example"] {
  --color-action-primary: #6d28d9;
  --color-action-primary-soft: #ede9fe;
  --color-action-on-primary: #fff;
  --form-radius: 14px;
  --field-radius: 14px;
  --form-gap: 1.25rem;
  --collection-gap: 1rem;
}
.app-shell.theme-dark[data-theme="example"] {
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
