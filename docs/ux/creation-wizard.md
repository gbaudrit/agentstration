# Configurable creation Wizard

Baseline: main `efbf247a` after #1174. The maintainer requested implementation of the approved Data Source mockup on a new branch, with the feature issue recorded at delivery.

`CreationWizard` is a shared presentation component in Agentstration.Web.Components. A screen supplies an ordered `IReadOnlyList<CreationWizardStep>`: stable ID, localized title, RenderFragment content and optional visibility. The screen supplies its step-change validation and final command. The shell has no resource client, domain model, authorization lookup or loading API.

```razor
<CreationWizard Steps="Steps" OnStepChanging="ValidateStep"
                OnFinish="CreateAsync" Busy="saving" Disabled="!canManage"
                CanFinish="canCreate" FinishLabel="@Localizer["Create"]" />

@code {
    private IReadOnlyList<CreationWizardStep> Steps =>
    [
        new("identity", Localizer["Identity"], IdentityContent),
        new("configuration", Localizer["Configuration"], ConfigurationContent),
        new("review", Localizer["Review"], ReviewContent)
    ];
}
```

The callback receives `CreationWizardStepChange.From` and `.To`; setting `Cancel` blocks navigation. It may validate asynchronously. The shell serializes navigation and creation, blocks queued commands when disabled/busy/disposed and permits direct navigation only to previously visited steps. Page validation also runs on forward revisits. A newly visible earlier step becomes current before finishing can proceed. Step IDs must be nonempty and unique, with at least one visible step.

Visible steps remain mounted while inactive and use native `hidden` content panes. Ordinary Back/Continue navigation preserves both the page model and stateful child editors. Steps removed by a visibility condition are unmounted; the caller still owns their model. This is in-page state, without persisted checkpoints or automatic saving.

The shell provides localized controls, an ordered progress list with one `aria-current="step"`, responsive navigation and focus transfer to the newly active content region. It uses ordinary buttons, not tabs: unvisited steps cannot bypass validation. All commands are type button; submitting the enclosing Data Source form advances its current step and cannot create before review.

## Data Source pilot

The dataset creation route `/data-sources/new?composition=dataset` uses:

1. Identity: display/technical name, namespace, owner scope, enablement and description.
2. Acquisition: existing authorized Connected Service and exact governed operation selection, plus saved parameters.
3. Storage: retained/direct mode, content/document collection requirements, prefix, freshness and history durations.
4. Processing: existing source orchestration and ordered composition editors.
5. Contracts: existing optional acquisition/output schema bindings.
6. Review: current configuration and per-section Edit commands, followed by explicit creation.

Field markup is screen-owned RenderFragments reused by creation and ordinary editing. The Data Source adapter owns every permission, selection, schema, lifecycle and command rule. The explicit finish command retains the enclosing EditContext validation before calling the original save adapter. Existing exact operation/storage pins, ETags, namespace/owner navigation and server validation are retained. Namespace shown/copied at review uses the same canonical ResourceNamespace value sent on creation. Domain generations remain generations. Review exposes no invented publication version, owner or readiness verdict. Creation does not start acquisition.

The existing legacy-profile creation path and saved-resource tabs remain available. Other resource families are future adopters; this delivery does not migrate Flows or Tool Definitions. The existing Source freshness browser journey is maintained for the changed creation flow; a new reusable scenario is not applicable to this pilot because that existing journey now covers the changed creation flow. Future family migrations retain their own UX follow-up requirements.

The collection inventory classifies the shell's configured progress/content iteration as a navigation control. Its addition changes the repeated-source audit to 146; functional collections remain 346.

## Validation and evidence

Current command results and real application captures are recorded at delivery. Generated mockups in the conversation describe the proposal; they are not implementation evidence. No earlier #1174 green run is claimed as validation of this branch.
