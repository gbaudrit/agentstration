# Agentstration control-plane UI foundations

This document records the first incremental UX/UI refactor. It is intentionally limited to shared foundations and the Agent management vertical; routes, APIs, persistence, and runtime behavior remain unchanged.

## Existing UI inventory

- `Agentstration.Web.Components` already owns the reusable shell, theme state, navigation state, feedback states, tables, filters, status badges, health, metrics, event lists, and execution timelines.
- `Agentstration.Web` owns resource pages and typed console API clients. Agent management and Runner pages use canonical Management and Runtime HTTP APIs, even when unrelated dashboard projections are simulated.
- CSS was centralized in the component library, but its original palette used short local aliases and the navigation was a flat resource list.
- Resource forms share `management.css`; pages already reuse `PageHeader`, `DataGrid`, `SearchBox`, `EmptyState`, `LoadingState`, `ErrorState`, and `ConfirmationDialog`.
- The Flow Designer remains isolated in `Agentstration.Web.FlowDesigner`, with Z.Blazor.Diagrams and Monaco. It is not moved into the generic component library.

## Refactor plan

1. **Foundations (this increment):** semantic design tokens, dark/light surfaces, grouped and collapsible shell navigation, populated top bar, shared resource headers, status indicators, toolbars, state comparison, and unsaved-change actions.
2. **Agents (this increment):** operational list hierarchy, contextual filters, non-destructive row actions, quick inspection, resource-oriented Agent header, and desired-versus-active generation visibility.
3. **Agent workspace:** make Overview read-only, separate Definition editing, add deployments/events/source views, and use Monaco for the declarative source.
4. **Agent Workbench:** two-column execution workspace, contextual run actions, reusable timeline, tool calls, and collapsible inspector.
5. **Overview and generalization:** operational health groups, topology, recent activity, then apply the same resource grammar to models, runtimes, work, executions, and events.
6. **Flow Designer:** adopt the tokens and resource grammar while keeping its specialized project, canvas library, source editor, and run inspection boundaries.

## Applying the components

- Use `PageHeader` for collections and task pages. Put counts or scope in `Metadata`; keep the primary action in `Actions`.
- Use `ResourceHeader` for a named Agent, Flow, model, Runtime, Work Item, Execution, or Run. Supply a stable `Domain` so the icon and accent use the correct token.
- Use `StatusIndicator` for operational state. Map transport terms to an explicit user-facing label at the UI boundary; never rely on color alone.
- Use `ResourceToolbar` around search, named workspace filters, and refresh/view actions. A scope value must always include its label (for example, `Workspace: default`).
- Use `StateComparison` wherever desired state and active Runtime state can diverge. Do not infer an active generation when the API does not report one.
- Use `UnsavedChangesBar` only after a form changes. Saving the desired definition and applying it to Runtime must remain visibly distinct actions.
- Add new colors, spacing, radii, shadows, dimensions, or transitions only as semantic variables in `design-tokens.css`. Existing `--bg`, `--surface`, `--text`, and related aliases bridge older components during progressive migration.

Status labels should use the common vocabulary: Draft, Valid, Published, Deploying, Ready, Running, Suspended, Degraded, Failed, Timed out, and Archived. `Accepted` is presented as `Valid` for Management resources; it remains unchanged in transport contracts.
