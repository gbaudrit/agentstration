# Shared form and theme contracts

The integration branch is `ux/unification-formulaires`; delivery is tracked by [#1089](https://github.com/gbaudrit/agentstration/issues/1089).

## Forms

`FormSection` and `FormGrid` compose existing panel/grid styles. `FormField` associates a localized label and optional help with one control through a stable generated ID. Its cascading presentation is consumed by `ClearableText`, `ClearableNumber<TValue>` and `ClearableSelect<TValue>`. Validation remains owned by the existing Blazor EditContext and domain model; use the original ValueExpression, normally supplied by `@bind-Value`, and the existing ValidationMessage.

The clear action is inside the control wrapper, uses `type="button"`, retains focus and updates the same bound value as typing. It does not submit, delete a resource or choose a default. Numeric/select values must support absence to offer clearing; a non-nullable enum cannot represent absence. Read-only and disabled controls cannot be cleared. Secret replacement inputs retain their existing no-replacement semantics; clearing must never become secret deletion.

`FormActions` provides presentation and busy semantics; the caller still owns domain operations. Do not treat it as a draft/saving state machine. Specialized JSON Schema and typed binding editors remain specialized components.

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
  --form-radius: 14px;
  --field-radius: 14px;
  --form-gap: 1.25rem;
  --collection-gap: 1rem;
}
.app-shell.theme-dark[data-theme="example"] {
  --color-action-primary: #c4b5fd;
  --color-action-primary-soft: #2e1065;
}
```

This is an override example, not a shipped theme selector or a new persisted preference. Named-theme authoring, distribution and selection remain future work. Validate contrast, keyboard focus, both appearances and mobile layout for each real theme; changing a primary color alone is not sufficient evidence.
