# Configurable step progress

`StepProgress` lives in `Agentstration.Web.Components`. It presents an ordered
list of steps beside the content of the inspected step. It is independent of
Bootstrap, providers, Flow execution and Work tasks. Consuming features supply
their own localized content and authoritative execution observations.

## Contract

Each `ProgressStep` has a stable, unique, non-empty `Id`, a `Title`, a
`ProgressStepStatus` and a `RenderFragment Content`. Optional `Summary` and
`IconName` enrich the list. `Selectable = false` keeps a step visible while
excluding it from inspection and keyboard selection.

`ParentId` optionally places a step under an existing top-level step. Parents
can expand/collapse their substeps; keyboard navigation follows only visible
selectable steps. Following an active substep expands its parent. Collapsing the
inspected substep's parent returns inspection to that parent and pauses following.
Substep IDs remain globally unique. Features supply only actual groups from their
execution plan, rather than a fixed inventory of possible resource kinds.

Statuses are `Pending`, `Running`, `Waiting`, `Succeeded`, `Failed`, `Skipped`,
`Cancelled` and `Interrupted`. Waiting is distinct from failure. Status labels,
empty states and follow controls use the neutral English and French resource
catalogs. The caller localizes its titles, summaries and panel content.

| Component parameter | Responsibility |
| --- | --- |
| `Steps` | Ordered presentation descriptors, including each right-pane fragment |
| `ActiveStepId` | The current execution step, supplied by the feature |
| `SelectedStepId` / `SelectedStepIdChanged` | Optional two-way binding of the inspected step |
| `FollowActiveStep` / `FollowActiveStepChanged` | Optional two-way binding of automatic following; defaults to true |
| `AutoCollapseCompletedGroups` | Fold completed top-level groups once while retaining operator reopening and inspection |
| `FitToViewport` | Opt-in bounded desktop panes sized from their actual position in the available viewport; default false |
| `ProgressKey` | Feature-owned observation token that triggers following the current resource within a stable step |
| `Label` | List heading and accessible label; defaults to localized preparation steps |
| `HeaderContent` | Optional caller-owned overall summary above both panes |
| `EmptyContent` | Optional replacement for the empty-list message |
| `Class`, `TestId` | Styling hook and test identity |

## Composition

```razor
@using Agentstration.Web.Components

<StepProgress Steps="steps"
              ActiveStepId="@activeStepId"
              @bind-SelectedStepId="selectedStepId"
              @bind-FollowActiveStep="follow">
    <HeaderContent>
        @* The feature supplies its localized overall summary. *@
    </HeaderContent>
</StepProgress>

@code {
    private string? selectedStepId;
    private string? activeStepId;
    private bool follow = true;
    private IReadOnlyList<ProgressStep> steps = [];

    // These localized fragments can contain components, forms or callbacks.
    private void SetSteps(string prepareTitle, RenderFragment preparation,
        string verifyTitle, RenderFragment verification)
    {
        steps = [
            new("prepare", prepareTitle, ProgressStepStatus.Succeeded, preparation),
            new("verify", verifyTitle, ProgressStepStatus.Waiting, verification)
        ];
        activeStepId = "verify";
    }
}
```

The component does not choose a panel based on a provider or adapter type. For
example, a feature's model-discovery step uses the same provider-neutral panel
with different input data. Other functional steps may supply entirely different
fragments without changing this shared component.

## Selection and updates

- Initial selection follows the active selectable step, or the first selectable
  step when the active ID is unavailable. A supplied different selection starts
  inspection with automatic following paused.
- Clicking a step, or choosing one with the keyboard, pauses following. Progress
  updates still refresh the selected content without stealing selection or focus.
  The active step retains its own marker independently of the inspected step.
- The follow control selects the current step, restores focus to its tab and
  resumes following subsequent active-step changes. A parent can also resume
  following through the bound parameter.
- Replacing or reordering descriptors preserves selection by ID. If that step is
  removed or becomes unselectable, selection falls back to the active selectable
  step, then the first selectable step. Empty lists clear the selection binding.
- Missing external IDs fall back safely; duplicate/blank IDs, blank titles,
  missing fragments and invalid statuses are rejected.

Only the selected fragment is mounted. Its identity is keyed by step ID: ordinary
data refreshes preserve its component state, while changing steps unmounts the old
panel. Keep durable drafts, results and execution state in the consuming feature
if they must survive switching panels. Selection never invokes a business action.

## Accessibility and layout

The vertical tab list uses a single tab stop, labelled panels, separate current
and selected semantics, and Up/Down/Home/End navigation that skips disabled steps.
Tab, Enter and Space retain native behavior. An isolated JavaScript listener only
prevents scrolling for the four handled navigation keys; selection remains in
Blazor. It is detached with the component, and nested instances remain isolated.

Active-step status changes have a small polite live region. Updating a pane does
not announce its entire interactive content. Cards stack on small viewports,
long labels wrap, wide panel content can scroll locally, and reduced-motion
preferences stop the running indicator animation. Styling uses the existing
theme variables and the host's normal scoped-CSS bundle.

## Ownership and delivery

The caller owns data refresh, persistence, authorization, action handlers,
counters, execution, cancellation/retry and navigation after completion. The
shared component introduces no network client, timer, Bootstrap dependency or
provider-specific UI.

This prepares the shared component for [#1250](https://github.com/gbaudrit/agentstration/issues/1250).
It does not implement the authenticated Bootstrap page, startup admission policy
or progress API. That routed journey and its deferred reusable browser scenario
remain in #1250 and [#1251](https://github.com/gbaudrit/agentstration/issues/1251).
The standalone library preparation adds no product route; component tests and an
isolated desktop/mobile smoke validate the primitive without starting that new
reusable journey or migrating existing Flow/Workplace progress components.

The [standalone browser smoke evidence](evidence/1250/step-progress/README.md)
contains the desktop/mobile captures and observed assertions for this primitive.

## Completed groups

`AutoCollapseCompletedGroups` is opt-in. A top-level step folds its substeps once
its own state and all of its children are Succeeded/Skipped. Failed, interrupted
or incomplete groups stay visible. An operator inspecting a child keeps that
panel and focus; completion never closes it underneath them. Selecting a closed
parent reopens it, and another selection of the open parent or the group control
closes it. Manually reopened completed groups remain open across refreshes.

In follow mode, a completed active child resolves to the folded parent summary
until activity advances. This also handles observations where completion arrives
before the next active-step identity.

## Viewport following

Bootstrap opts into `FitToViewport`. The navigation and detail panes scroll
independently inside the remaining desktop viewport; headers and summaries remain
outside those scroll regions. Resize observation recalculates the available
height. Narrow layouts keep stacked panes with bounded scrolling regions.

While following, selection changes reveal the selected tab, and `ProgressKey`
changes reveal the feature-owned row marked `data-progress-current="true"`.
Scrolling either pane or interacting with detail controls pauses following.
The existing follow command restores it without moving document scroll or
changing execution. Paging/search adapters bind the same follow state, so manual
inspection stays stable through polling. Features remain responsible for bounded
resource queries and determining the current or failed resource.
