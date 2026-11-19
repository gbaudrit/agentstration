# Authoring resource detail screens

This chapter implements the resource-screen composition part of [#1235](https://github.com/gbaudrit/agentstration/issues/1235). It applies to persisted resources. Collection pages and creation flows remain separate. Resource creation uses `CreationWizard`; the screen owns its stages, field fragments, validation and commands. Do not change the global Console/Workplace layout to implement a resource screen.

The first adopter is `src/Agentstration.Console.Components/Components/Pages/AgentEditor.razor`, route `/agents/{Name}` with optional `namespace`, exact owner `scopeRef` and administrative `context`. Creation at `/agents/new` uses the shared wizard with the existing standard identity fields, model/runtime pickers, Tools and contract editors. Pack-managed Agents retain their separate read-only route and capability rules.

## Required structure

Persisted Console screens use `src/Agentstration.Console.Components/Components/ConsoleResourceDetailsTemplate.razor`. It composes the shared `src/Agentstration.Web.Components/ResourceDetailsTemplate.razor`, which constructs `ResourceHeader`; do not pass an arbitrary replacement header or duplicate its page layout in the business screen.

1. Full-width resource header: icon/type, saved display name, useful description, exact owner scope, qualified copyable technical name, relevant status and commands. Omit the redundant Agentstration eyebrow.
2. Below the header: content column and resource-local navigation column on the right. Global navigation remains in the application layout.
3. Caller feedback spans the resource area below the header and above the two body columns, keeping the first panel aligned with its navigation. Blocking resource load/error states replace editor content and draft actions; refresh feedback retains them.
4. Draft actions follow content in that same column. Reuse the shared sticky surface, mobile navigation offset and safe-area rules. Never stretch it behind the local rail.

The template owns placement, spacing, the standard section tree and the mounting/visibility of its section regions. The business screen owns data loading, exact resource coordinates, permissions, confirmations, commands, ETags, validation, feedback and draft restoration. It has no generic resource client, repository, domain-aware base page, inferred save state or automatic save on navigation.

## Standard sections

New and migrated screens supply the named regions below, without `Content`, `Sections` or `DefaultSection`. The template constructs this fixed navigation:

```text
Overview
Definition (group only)
  Identity
  Configuration
  [optional family definition children]
[optional family primary sections]
Access (when authorized content is supplied)
YAML
```

Overview, Definition/Identity, Definition/Configuration and YAML cannot be removed, renamed or replaced by family entries. Shared labels are localized centrally; YAML is always the last independent primary entry. Families can add Definition children or primary sections between Definition and Access. Their stable paths must not collide with the reserved destinations.

The template builds Overview in this order: optional metrics grid → `ResourceIdentitySummary` using the saved header identity and labels → Configuration panel → optional family panels. The Console adapter supplies labels from `Resource.Metadata.Tags`; render them inside Identity using `LabelsSummary`, including the localized empty state, without a separate Labels panel. Draft labels never replace the saved summary before a successful save. Its metrics slot accepts existing `MetricCard` components and controls the adaptive grid and spacing. Agent supplies Desired generation, Active generation and Deployment cards instead of `StateComparison`; values come from the saved generation and runtime readiness, while pending changes remain visible in the detail/status.

Definition, additional sections, Access and YAML mount on first visit and remain mounted while hidden. Definition uses one retained fragment for its child sections so a family can keep a single validated form and `EditContext`. The Console adapter supplies `ResourceAccessPanel` from the saved `Resource` identity and existing ownership permissions. It combines access drafts with definition drafts in one leave guard, preserves policy edits across generation refreshes and rejects callbacks from replaced resource identities. The shared Web component has no dependency on Console clients or permission models. The adapter also supplies `ResourceYamlEditor` inside the shared YAML panel; parsing, authorization and save semantics remain family-owned.

Pass one `ResourceSectionState` instance to the adapter and read its `CurrentPath`, `DefinitionPath` and visited destinations in the family screen. The shared template updates that same instance after accepted navigation. Derive display predicates from it; do not maintain mutable per-section mode flags or a second visited-section registry. Request persisted section changes through navigation and let the accepted destination update the state. Creation may use the state directly for its form/YAML switch and record visited wizard editors.

Declare section presentation in `SectionPresentations`, keyed by an exact navigable section path. Each `ResourceSectionPresentation` declares `HasRequiredFields`, `ReadOnly`, optional `ReadOnlyReason`/localized `ReadOnlyMessage`, its root model `Fields` and authorized `Actions`/`ReadOnlyActions`. Pass the same declaration to the retained `ResourceDefinitionForm`. The template adds one localized required-fields legend on a discreet guidance row above both body columns, outside header metadata, only for an editable section declaring required fields. Content and navigation keep the same starting position. State/loading views suppress the legend. Agent declares required fields for Identity, Configuration and Model/execution; Overview, Tools, Contracts, Access and YAML have no page-level legend. Creation keeps its existing form-start guidance.

Declared section actions render in the content column below the section and before the draft bar, through `FormActions` with a localized group label and the supplied busy state. An absent action fragment reserves no row. Resource-wide commands remain in the header; definition Save/Discard remains in the draft bar; Access retains its specialized policy commands. Agent declares Apply to form as its YAML section action. Read-only sections hide `Actions` and may explicitly supply inspection/navigation commands through `ReadOnlyActions`. Callers provide localized labels, authorization, disabled states, callbacks and any external form target. The template does not execute commands or bypass validation. Actual form controls and application commands still enforce permissions and validation.

Existing adopters using `Content` and `Sections` remain supported during migration. Do not combine that compatibility contract with standard region content. Agent demonstrates the standard contract; this increment does not migrate other resource families or creation flows.

## Inputs and named regions

| Input/region | Required use |
| --- | --- |
| `Resource`, `RequestedAddress` (Console adapter) | Saved base resource: exact UID, kind, technical name, namespace, owner, generation and governance. Adapter-owned Access and technical header identity use this snapshot. Null is allowed only during a blocking load state; optional `RequestedAddress` supplies route identity without implying ownership. |
| `Name`, `ResourceType` | Required, localized presentation; saved title on an existing resource. |
| `TechnicalName`, `Namespace`, `ScopeRef` (shared template) | The Console adapter derives these from `Resource`. Never infer Instance ownership from null. |
| `Description`, `StatusText`, `Status` | Useful saved presentation; omit unsupported state. |
| `Domain`, `Icon`, `IconName`, `ImageSource`, `DarkImageSource`, `IconContent` | Existing family icon contract. Trusted icon content only. |
| `Metadata` | Small relevant metadata such as generation. Do not repeat technical identity or expose UID/digest diagnostics by default. |
| `PrimaryActions`, `Actions`, `MoreActions` | Existing authorized primary, secondary and optional overflow commands. |
| `Enabled`, `EnabledChanged`, `EnabledDisabled`, `EnabledTestId` | Optional header switch. Null hides it; no callback is read-only. Family owns confirmation, writes, ETag and synchronization with other drafts. Agent currently has no enablement property: omit the switch. |
| `DefinitionSections`, `AdditionalSections` | Optional typed family navigation extensions; Definition children and independent primary sections respectively. Reserved destinations cannot be replaced. |
| `SelectionChanged` | Resolved section callback for family-specific editor modes and YAML initialization. Standard navigation is constructed by the template. |
| `SectionState` | One resource-local presentation state shared by the screen, Console adapter and Web template. Fragment changes update it; accepted identity changes reset it. |
| `SectionPresentations` | Optional path-to-`ResourceSectionPresentation` dictionary for required-field guidance, read-only presentation and authorized section actions. Unknown/group-only paths are rejected. |
| `DefinitionReadOnlyReason` (Console adapter) | Optional explicit reason, such as insufficient write permissions. Saved governance and inherited ownership also impose read-only presentation. The adapter preserves an owner-context link when the exact owner is known. |
| `ResourceIdentity` | Required when sections exist: exact route/namespace/owner/context identity, stable across fragments. The template combines it with normalized URL coordinates, observes accepted location changes and keys the navigation instance without remounting editor content. |
| `ExpandNavigationGroups` | Defaults to true: groups begin expanded; a caller can opt out and users retain their collapse choices. |
| `NavigationLabel`, `NavigationTestId` | Localized navigation landmark label and optional stable test selector. |
| `Feedback` | Resource-wide notices below the header; fixed notifications do not reserve an empty body row. Existing action feedback. Action/transport failures remain visible; do not duplicate inline errors. |
| `LoadState`, `OnRetry`, `OnRefresh` | Typed resource presentation state, recoverable retry and optional shared header refresh command. Blocking states suppress commands, editors and navigation; refresh states retain mounted content and drafts. Family code owns requests and response classification. |
| `State` | Legacy optional custom state fragment for existing adopters. When supplied, replaces section content and suppresses draft actions. Prefer typed `LoadState` for loading/errors. |
| `OverviewMetrics`, `IdentityDetails`, `OverviewConfiguration`, `OverviewContent` | Optional cards, additional identity facts, required saved configuration content and optional family panels. Identity and Configuration panels and their order belong to the template. |
| `IdentityLabels` (shared template) | Saved labels rendered inside the Identity summary. The Console adapter supplies them directly from the saved resource metadata. |
| `DefinitionContent` | Required retained editor fragment, receiving the current/last Definition child path. Configuration is mandatory; specialized editors remain family-owned. |
| `AdditionalContent` | Required when additional primary sections exist; receives their path. Each visited section retains its own mounted content. |
| `AllowAccess`, `AccessVersion`, `EvaluateSavedAccess` (Console adapter) | Optional capability restriction, version metadata and existing saved-policy evaluation option. Ownership checks still apply. Access uses the exact saved UID and remains independently editable for authorized managed resources. |
| `Yaml`, `YamlChanged`, `YamlReadOnly`, `Busy`, `YamlError`, `YamlLabel`, `YamlFeedback` (Console adapter) | YAML editor value/callback, capability and feedback. Declare YAML commands through `SectionPresentations["yaml"].Actions`. Read-only is the default; the adapter never parses or saves YAML. |
| `YamlTitle`, `YamlDescription` | Optional localized YAML panel title/description. The shared template supplies the standard panel and final navigation entry. |
| `AccessContent`, `YamlContent` (shared template) | The Console adapter supplies these regions. Direct shared-template consumers remain responsible for their content and leave guards. |
| `SectionTestIdPrefix` | Optional stable prefix for overview, identity summary, configuration summary, YAML editor and content region selectors. |
| `Content`, `Sections`, `DefaultSection` | Compatibility contract for existing adopters only; mutually exclusive with standard composition. |
| `HasDraft`, `DraftActions` | Caller-computed dirty state and authorized save/discard controls. Template creates `UnsavedChangesBar` only when both are present. |
| `TestId` | Stable family-specific root selector. |

## Section composition and components

Paths in this table are relative to `src/Agentstration.Web.Components/` unless stated otherwise.

| Element | Required component/path and rule |
| --- | --- |
| Header | `ResourceHeader.razor`: composes `ResourceIcon.razor`, `ResourceScopeBadge.razor`, `CopyableValue.razor`, `ResourceActionBar.razor`; optional `ResourceActivationToggle.razor`. One activation control; no duplicate Deactivate button. |
| Overview identity | `ResourceIdentitySummary.razor`. Always include it after useful indicators. Use `AdditionalDetails` only for meaningful family identity facts. |
| Panels and grids | `FormSection.razor`, `FormGrid.razor`; shared semantic spacing/theme variables. Suitable two-column fields on desktop, one column on mobile. |
| Editable identity | Console `Components/ResourceIdentityEditor.razor`: one standard Identity panel using `ResourceIdentityFields.razor`, `FormGrid.razor`, `FormField.razor`, Console `Shared/ResourceScopeFormField.razor` and `LabelsEditor.razor`. Use `AdditionalPanels` for family panels after the common panel, within the same form. |
| Definition form | `ResourceDefinitionForm.razor`: one `EditForm`, `LocalizedDataAnnotationsValidator` and `FormDraftMonitor`, retained sections, disabled fieldsets and invalid-submit routing/focus. Families supply their existing EditContext, field labels, declarations and panel fragments. |
| Fields | `FormField.razor` plus `ClearableText.razor`, `ClearableNumber.razor`, `ClearableSelect.razor` or `FormCheckbox.razor`. Preserve actual bound `ValueExpression`, labels, help and inline `ValidationMessage`. The template owns the localized guidance row from section presentation declarations. Creation retains its form-start guidance. |
| Annotation validation | `LocalizedDataAnnotationsValidator.cs`, composed by `ResourceDefinitionForm.razor`. Supply localized `FieldLabels` to the common form; keep annotations in the model. Do not install a second validator in the screen. Agent is the pilot. |
| Saved configuration | `CollectionView.razor` and `CollectionValue.razor`, inside a `FormSection` with `Class="resource-configuration-summary"`. Two-column setting/value summary gives values 70% of table width. Use wrapping `.configuration-key`/`.configuration-value` content; do not truncate the only inspectable value. |
| Resource rows | `CollectionIdentity.razor` only when the row identifies a real resource. Plain setting names are not fictional resources. Links retain exact scope/context through existing Console URL helpers. |
| Indicators | `MetricCard.razor` in `OverviewMetrics`; `StatusBadge.razor` for supported state. Do not manufacture zero counts or new business status. |
| Specialized values | Console `Components/Values/ValueBindingRequirementEditor.razor`, shared `JsonSchema/JsonSchemaInputEditor.razor` and the existing schema/contract editor where appropriate. Retain source eligibility and secret-reference semantics. |
| Agent tools/contracts | Console `Components/Pages/AgentToolSetConfiguration.razor`, `Components/Pages/AgentToolConfiguration.razor`, `Components/ReusableSchemaBindingEditor.razor` and `Components/ContractDirectionTabs.razor`; preserve current family behavior. |
| Access | Console adapter supplies `Components/Pages/ResourceAccessPanel.razor`, with exact saved identity and existing policy permissions. |
| YAML | Shared template supplies `FormSection.razor`; Console adapter supplies `Components/ResourceYamlEditor.razor` → `FormField.razor` → multiline `ClearableText.razor`. Family supplies value, capability, feedback and commands. |
| Feedback/state | `ResourceLoadState` and template-owned `ResourceLoadFeedback.razor` for resource loading/errors; `ResourceReadOnlyNotice.razor` for read-only reasons; `PageNotification.razor` and existing alerts for command feedback. Collection empty states retain `EmptyState.razor`. Localize caller-supplied text. |
| Draft actions | Template's `UnsavedChangesBar.razor`; use `FormActions.razor` for section-specific commands. `UnsavedNavigationGuard.razor` and `FormDraftMonitor.razor` retain protection/invalid input tracking. |
| Section actions | Declare `ResourceSectionPresentation.Actions` for editable sections and explicit `ReadOnlyActions` for consultation/navigation in read-only sections. The template supplies `FormActions.razor`, placement, localized group label and busy state. Family callbacks and form submission semantics remain unchanged. |
| Dialogues | `ConfirmationDialog.razor`, `ModalFrame.razor`; reuse their focus behavior. |

For resources with activation, use the template header switch and keep its existing confirmation/write/save behavior in the adapter. Prefer switch presentation for binary options; retain checkboxes for multi-selection. Do not convert three-state execution policies to booleans.

The shared template lazy-mounts Access and retains its hidden pane. The Console adapter passes the saved `ResourceUid` and owns the combined leave guard. Screens using the adapter must not add a second guard for that persisted resource; creation/loading may retain their own guard before the adapter exists. The template draft bar remains for definition changes; policy Save/Discard stays in the Access panel. Managed definitions remain read-only while independently authorized access administration remains available. With an exact UID, generation-only refreshes retain pending policy edits and update their metadata; a mismatched inventory UID cannot select a different resource’s policy.

Overview order is **relevant indicators → Identity → Configuration → family-specific panels**. Present saved values, even when a form draft exists. Summarize all applicable saved configuration, including declared references and structured settings. Never resolve or display Secret contents. Avoid disabled forms as a read-only summary.

Definition navigation order is **Identity → Configuration → family-specific sections**. Identity has its own reserved `#definition/identity` destination before Configuration. Where both exist, value bindings precede type-specific options. Preserve specialized editors. Show field errors once, beside their field; retain genuine cross-field/transport failures in feedback. Avoid redundant information banners and technical contract panels without an actionable user need.

## Standard editable identity

Use Console's `ResourceIdentityEditor` in the retained Definition form for `definition/identity`. It owns one localized Identity panel containing display name, technical name, namespace, owner scope, description and labels. Labels belong inside this panel, never in a separate Labels panel. The technical identity, namespace and owner stay immutable for saved resources. Families supply value bindings and expressions for their actual edit-model properties; model rules, callbacks and permission checks remain family-owned. No universal edit model or second `EditForm` is introduced.

The optional `AdditionalPanels` fragment follows the common panel in the same section, using the shared section gap. It inherits the existing `EditContext`; use `FormSection` for each family panel and retain its own permission/validation rules. Add useful family identity information there without replacing or renaming standard fields. An omitted fragment reserves no space. Agent needs no additional identity panel today; the slot is available to other adopters.

Keep the identity component mounted after its first visit, including partial or duplicate label rows that are not yet publishable to the model. Refresh, section changes and validation must preserve that local state. Explicit discard or an accepted snapshot replacement resets it with the form. The Agent creation wizard also reuses the common panel in its Identity step and returns there for invalid labels.

## Shared definition form and validation navigation

Use `ResourceDefinitionForm` inside `DefinitionContent`; do not recreate the validator/monitor/form assembly in each resource screen. Supply `EditContext`, localized `FieldLabels`, a stable HTML `Id` for external submit buttons, command callbacks and the existing `InvalidInputChanged` callback. `Busy`, `ReadOnly` and `Disabled` lock its fieldset and suppress its valid-submit command. Model rules and API commands remain family-owned. The wrapper never saves automatically or computes a resource fingerprint.

For persisted resources, pass the same `SectionState` and section presentation dictionary as the layout, plus `SectionContent` and `SectionRequested`. The wrapper mounts visited Definition panels and retains each one while hidden. Families keep their actual `FormSection`/`FormGrid`/`FormField` composition. Root field names belong in `ResourceSectionPresentation.Fields`; each field must exist on the edit model and belong to one section. These declarations are presentation metadata, not validation rules. Special/nested editors retain their existing validation stores.

On invalid submission, the wrapper uses declaration order to request the section containing the first mapped error. After accepted section rendering, it scrolls to and focuses the first visible invalid control, including custom editor groups with inline messages. A context token prevents a superseded focus request from targeting a replacement form. Unknown/form-level failures fall back to the last Definition destination and the visible form; keep genuine form-level feedback in its existing surface. There is no duplicate validation summary.

Creation can supply `ChildContent` for its existing `CreationWizard`. Wizard prerequisite checks and accepted step transitions stay with the screen; `RequestValidationFocus` lets focus follow the returned step. Agent uses this same wrapper for creation and editing, keeps external Save buttons bound to `agent-resource-form`, and declares Identity, Configuration, Model/execution, Tools and Contracts fields once.

## Read-only definitions and sections

`ResourceReadOnlyNotice` renders localized generic, inherited, managed or insufficient-permission explanations. The Console adapter derives inherited/managed state from the saved owner/governance and accepts an explicit permission reason from the screen. Missing ownership receives a generic read-only state, never inferred Instance ownership. An inherited definition keeps the exact owner-context link. The notice is shared across sections without repeating a family-specific banner.

Whole-definition read-only state cascades to `ResourceDefinitionForm`, locks the YAML editor and hides definition draft mutation actions. Each declared read-only section also disables its own retained fieldset and hides normal section actions. Explicit `ReadOnlyActions` remain available for consultation/navigation; Agent's invalid-form YAML state uses this slot to return to the form. Access policy commands and resource execution remain independently authorized. UI locking complements existing application/API permission checks and must never replace them.

## Navigation integration boundary

[#1233](https://github.com/gbaudrit/agentstration/issues/1233) owns hierarchy, fragments/history, focus and mobile drawer behavior. Reuse that implementation; do not create a second engine in this template. Its contract must retain two levels, icons, directly selectable Tools, and an independent final primary YAML entry. There is no five-tab cap.

The Agent pilot consumes the component merged in PR #1237. The template places one `ResourceNavigation` instance in its sticky right rail, separated from the content by the existing `--space-6` gutter and its shared `ResourceNavigationTrigger` in the header. The external trigger reuses the same drawer, localized label, expansion state and focus-return behavior; no second navigation engine or modal is created.

Agent hierarchy: Overview; grouping-only Definition with Identity, Configuration, Model and execution, Tools, Contracts; Access when manageable; independent final YAML. Stable destinations are `#overview`, `#definition/identity`, `#definition/configuration`, `#definition/model-execution`, `#definition/tools`, `#definition/contracts`, `#access`, `#yaml`. Identity contains common fields and labels in one panel. Configuration retains Handler and Instructions; Model and execution retains the profile pickers. Each visited subview remains mounted in the same form. Invalid submission selects the section owning the first invalid field, including incomplete label rows in Identity.

The adopted selection model is fragments only, never obsolete `view`/`tab` selectors. Preserve canonical resource paths and the `namespace`, `scopeRef`, `context` coordinates. Unknown/unauthorized destinations must use #1233's authorized fallback. Keep existing leave-resource/context guards.

## Agent example and draft ownership

The real Agent pilot supplies the following composition (see the complete executable markup in `Components/Pages/AgentEditor.razor`):

```razor
@{
    var presentations = AgentSectionPresentations(yamlActions);
}
<ConsoleResourceDetailsTemplate Resource="@savedResource" Name="@(savedResource?.Definition.DisplayName ?? Name!)"
                         ResourceType="@Localizer["Agent"]" Domain="agent" Icon="◎"
                         Description="@savedResource?.Definition.Description"
                         Feedback="@feedback" HasDraft="@isDirty"
                         LoadState="loadState" OnRetry="RetryLoadAsync" OnRefresh="RefreshAsync"
                         DefinitionSections="AgentDefinitionSections"
                         SectionState="sectionState" @bind-Yaml="rawYaml" YamlReadOnly="@(!CanEditRaw)"
                         Busy="saving" YamlError="@rawError"
                         SectionPresentations="presentations"
                         ResourceIdentity="@($"{loadedKey}|{selectedScope}|{ContextScopeRef}")"
                         SelectionChanged="SectionSelected" SectionTestIdPrefix="agent">
    <OverviewMetrics><!-- existing MetricCard components --></OverviewMetrics>
    <OverviewConfiguration><!-- saved CollectionView content --></OverviewConfiguration>
    <OverviewContent><!-- optional family panels --></OverviewContent>
    <DefinitionContent Context="section">
        <ResourceDefinitionForm @key="model" EditContext="FormContext" FieldLabels="ValidationFieldLabels"
            Id="agent-resource-form" FormName="agent-editor"
            Sections="presentations" SectionState="sectionState" SectionContent="DefinitionSectionContent"
            SectionRequested="ShowSection" OnValidSubmit="() => SaveAsync(applyRuntimeOnSubmit)"
            InvalidInputChanged="value => invalidInput = value"
            Busy="saving" ReadOnly="@(!CanManage)" Disabled="HasRawDraft" />
    </DefinitionContent>
    <Metadata><span>@Localizer["Generation", generation]</span></Metadata>
    <PrimaryActions><!-- existing authorized Run link --></PrimaryActions>
    <Actions><!-- existing authorized reconcile and contextual return --></Actions>
    <DraftActions>
        <button type="button" @onclick="DiscardAsync" disabled="@(saving || refreshing)">@Localizer["Discard"]</button>
        <button type="submit" form="agent-resource-form" @onclick="() => applyRuntimeOnSubmit = false"
                disabled="@(saving || refreshing || HasRawDraft)">@Localizer["SaveDraft"]</button>
    </DraftActions>
</ConsoleResourceDetailsTemplate>
```

The template applies the shared section gap inside its immediate visible `div` section wrappers and nested `.resource-overview` composition. Keep these wrappers intact when retaining section content; native `hidden` still controls visibility.

Nested editor stacks must also use `.resource-editor-view`, including identity enrichments and fragments containing several panels. A plain block wrapper loses the shared grid gap between its children. Agent applies this to its identity, configuration, execution, tools and contract fragments while retaining the existing hidden-pane behavior.

Keep visited forms mounted with `hidden`; retain their `EditContext`, invalid parser input, labels and schema/editor state across section changes and responsive transitions. Only replace the model/context after a successful save, explicit discard or a refresh without a definition draft. Ignore superseded asynchronous load completions before applying snapshots or resetting section/draft state; Agent demonstrates a load-version check. Fragment-only transitions must not reload the saved resource. External submit buttons target the real `EditForm` by its stable HTML `id`; they must still run its validation. Do not call a save directly to bypass an invalid hidden form.

Agent's overview reads `savedResource`, not the draft. Its dirty action surface remains available in Overview after visiting Definition, and Discard restores the saved snapshot. YAML retains its apply command and invalid-form guard; saved-resource YAML Save/Discard uses the same draft-action region, with the family-owned YAML handlers. The template has no API client and never determines whether saving succeeded.

## Loading, refreshing and resource failures

Use `ResourceDetailsTemplate.LoadState` (`ResourceLoadState`) and the shared `ResourceLoadFeedback`; do not implement another family-specific loading/error panel. The template owns localized loading, refreshing, failed, not-found and forbidden presentation. Its `OnRetry` callback provides Retry for recoverable failures; `OnRefresh` opts into a shared header Refresh action. Family code retains API calls, cancellation, authorization, response classification and command disablement. Save/validation errors remain in their existing field/general feedback surfaces, separate from load failures. The legacy `State` fragment remains available for existing adopters.

Before a snapshot is available, render the template shell with a blocking Loading state, then Failed/NotFound/Forbidden as appropriate. The Console adapter accepts a null `Resource` only with a blocking state and can use `RequestedAddress` for route identity. Never infer owner, status or permissions from that address. Blocking states hide resource commands, editors, draft actions and local navigation. Only recoverable failures offer Retry. NotFound/Forbidden must clear cached resource content, drafts and permissions rather than displaying stale private data.

For an existing snapshot, Refreshing and Failed with `HasContent=true` keep the same content subtree mounted. Use a compact status/error above the content, preserve the active section and keep definition, YAML and policy editors and their error stores. Agent refreshes the exact loaded namespace/name/owner and rejects a changed UID/address/owner. A superseded response cannot affect a newly accepted resource/context.

Evaluate whether the definition is dirty when the refresh response arrives. A pristine form may adopt the new snapshot. A draft keeps its model, `EditContext`, parser state, baseline and original ETag; refresh is never an implicit save. If the server version changed, Agent retains the newer snapshot separately and displays a localized warning. Save still uses the draft's original ETag; explicit Discard adopts the newer snapshot. A failed refresh leaves every draft and its leave guard available for Retry. This guarantee concerns the in-app Refresh action; a full browser reload does not persist drafts.

## Verification and delivery

### Localized form validation

Agent creation and editing use `LocalizedDataAnnotationsValidator` for annotations on the root edit model. `Required`, length, range, format, email/URL/phone/card, enum and comparison defaults use `SharedFormStrings` keys under `Validation.*`, with complete English/French messages. Families map every annotated property (including non-input reference coordinates) to localized display labels. No annotation, persisted identifier or model rule is rewritten to translate presentation.

The validator owns its `ValidationMessageStore`, validates changed fields without showing untouched-field errors, and validates the model on submission. It preserves parsing/schema/label errors owned by other stores, custom explicit messages and `IValidatableObject` results, and detaches when the edit context changes or is disposed. Explicit/custom messages retain their existing localization responsibility. This component does not validate arbitrary nested object graphs or translate API/JSON-schema failures; those specialized paths remain separate migration work.

Render each field's `ValidationMessage` in `FormField.Validation`, using an expression for the actual edit-model property. `FormField` owns uniform error styling, its polite live region and the input's descriptive link. Agent identity fields supply explicit model expressions. Its Model Profile picker is wrapped in `FormField` with its duplicate internal heading suppressed and the error linked to the selection group. Field labels and rules remain in their respective catalogs/model; avoid duplicate field errors in a page summary. Keep genuine form-level/business failures in the existing general feedback surface.

Use focused component checks for template states/optional slots, retained form instances, localized rendering, authorized/read-only behavior and save/discard routing. Maintain existing browser regressions and smoke-test the executable at desktop/mobile widths, both appearances, long identities/values and keyboard/touch use. Check save actions against both navigation regions and safe areas.

The new reusable template journey is deferred to native child [#1236](https://github.com/gbaudrit/agentstration/issues/1236), after explicit maintainer UX validation and implementation merge. Navigation-specific follow-up remains #1234. Do not close #1235 before its children are complete or a maintainer records a waiver.

Initial loading is not a draft. Agent only enables dirty-navigation protection after its saved baseline is loaded. Preferred-culture redirects retain the section fragment, and default context URL normalization uses the public Blazor API so the server navigation state and browser coordinates stay synchronized.
