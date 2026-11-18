# Authoring resource detail screens

This chapter implements the resource-screen composition part of [#1235](https://github.com/gbaudrit/agentstration/issues/1235). It applies to persisted resources. Collection pages and creation flows remain separate; retain `CreationWizard` where already adopted. Do not change the global Console/Workplace layout to implement a resource screen.

The first adopter is `src/Agentstration.Console.Components/Components/Pages/AgentEditor.razor`, route `/agents/{Name}` with optional `namespace`, exact owner `scopeRef` and administrative `context`. Creation at `/agents/new` keeps its existing composition. Pack-managed Agents retain their separate read-only route and capability rules.

## Required structure

Use `src/Agentstration.Web.Components/ResourceDetailsTemplate.razor`. It constructs `ResourceHeader`; do not pass an arbitrary replacement header or duplicate its page layout in the business screen.

1. Full-width resource header: icon/type, saved display name, useful description, exact owner scope, qualified copyable technical name, relevant status and commands. Omit the redundant Agentstration eyebrow.
2. Below the header: content column and resource-local navigation column on the right. Global navigation remains in the application layout.
3. Caller feedback spans the resource area below the header and above the two body columns, keeping the first panel aligned with its navigation. Resource load/error/empty state replaces editor content and draft actions.
4. Draft actions follow content in that same column. Reuse the shared sticky surface, mobile navigation offset and safe-area rules. Never stretch it behind the local rail.

The template owns placement, spacing and shared presentation. The business screen owns data loading, exact resource coordinates, section visibility, permissions, confirmations, commands, ETags, validation, feedback and draft restoration. It has no generic resource client, repository, domain-aware base page, inferred save state or automatic save on navigation.

## Inputs and named regions

| Input/region | Required use |
| --- | --- |
| `Name`, `ResourceType` | Required, localized presentation; saved title on an existing resource. |
| `TechnicalName`, `Namespace`, `ScopeRef` | Exact loaded resource identity/owner. Never infer Instance ownership from null. |
| `Description`, `StatusText`, `Status` | Useful saved presentation; omit unsupported state. |
| `Domain`, `Icon`, `IconName`, `ImageSource`, `DarkImageSource`, `IconContent` | Existing family icon contract. Trusted icon content only. |
| `Metadata` | Small relevant metadata such as generation. Do not repeat technical identity or expose UID/digest diagnostics by default. |
| `PrimaryActions`, `Actions`, `MoreActions` | Existing authorized primary, secondary and optional overflow commands. |
| `Enabled`, `EnabledChanged`, `EnabledDisabled`, `EnabledTestId` | Optional header switch. Null hides it; no callback is read-only. Family owns confirmation, writes, ETag and synchronization with other drafts. Agent currently has no enablement property: omit the switch. |
| `Sections`, `DefaultSection`, `SelectionChanged` | Typed `ResourceNavigationItem` configuration and resolved section callback. The template constructs the shared navigation and its mobile trigger. |
| `ResourceIdentity` | Required when sections exist: exact route/namespace/owner/context identity, stable across fragments. The template combines it with normalized URL coordinates, observes accepted location changes and keys the navigation instance without remounting editor content. |
| `ExpandNavigationGroups` | Defaults to true: groups begin expanded; a caller can opt out and users retain their collapse choices. |
| `NavigationLabel`, `NavigationTestId` | Localized navigation landmark label and optional stable test selector. |
| `Feedback` | Resource-wide notices below the header; fixed notifications do not reserve an empty body row. Existing action feedback. Action/transport failures remain visible; do not duplicate inline errors. |
| `State` | Optional `LoadingState`, `ErrorState` or `EmptyState`. When supplied, replaces `Content` and suppresses draft actions. |
| `Content` | Required section content and retained editors. The template does not select, recreate or reset them. |
| `HasDraft`, `DraftActions` | Caller-computed dirty state and authorized save/discard controls. Template creates `UnsavedChangesBar` only when both are present. |
| `TestId` | Stable family-specific root selector. |

## Section composition and components

Paths in this table are relative to `src/Agentstration.Web.Components/` unless stated otherwise.

| Element | Required component/path and rule |
| --- | --- |
| Header | `ResourceHeader.razor`: composes `ResourceIcon.razor`, `ResourceScopeBadge.razor`, `CopyableValue.razor`, `ResourceActionBar.razor`; optional `ResourceActivationToggle.razor`. One activation control; no duplicate Deactivate button. |
| Overview identity | `ResourceIdentitySummary.razor`. Always include it after useful indicators. Use `AdditionalDetails` only for meaningful family identity facts. |
| Panels and grids | `FormSection.razor`, `FormGrid.razor`; shared semantic spacing/theme variables. Suitable two-column fields on desktop, one column on mobile. |
| Editable identity | `ResourceIdentityFields.razor`, with `Shared/ResourceScopeFormField.razor` from Console `Components/`. Optional family type selector belongs in Identity using `FormField.razor` and `ClearableSelect.razor`. |
| Fields | `FormField.razor` plus `ClearableText.razor`, `ClearableNumber.razor`, `ClearableSelect.razor` or `FormCheckbox.razor`. Preserve actual bound `ValueExpression`, labels, help and inline `ValidationMessage`. One localized required-fields legend in header metadata for detail subviews with required fields; omit it on Tools/Overview/Access/YAML. Creation retains its form-start guidance. |
| Saved configuration | `CollectionView.razor` and `CollectionValue.razor`, inside a `FormSection` with `Class="resource-configuration-summary"`. Two-column setting/value summary gives values 70% of table width. Use wrapping `.configuration-key`/`.configuration-value` content; do not truncate the only inspectable value. |
| Resource rows | `CollectionIdentity.razor` only when the row identifies a real resource. Plain setting names are not fictional resources. Links retain exact scope/context through existing Console URL helpers. |
| Indicators | `MetricCard.razor`, `StatusBadge.razor`, `StateComparison.razor` for real supported state. Do not manufacture zero counts or new business status. |
| Specialized values | Console `Components/Values/ValueBindingRequirementEditor.razor`, shared `JsonSchema/JsonSchemaInputEditor.razor` and the existing schema/contract editor where appropriate. Retain source eligibility and secret-reference semantics. |
| Agent tools/contracts | Console `Components/Pages/AgentToolSetConfiguration.razor`, `Components/Pages/AgentToolConfiguration.razor`, `Components/ReusableSchemaBindingEditor.razor` and `Components/ContractDirectionTabs.razor`; preserve current family behavior. |
| Access | Console `Components/Pages/ResourceAccessPanel.razor`, with exact owner and existing policy permissions. |
| YAML | `FormSection.razor` → `FormField.razor` → multiline `ClearableText.razor`; retain current editable/read-only capability, parse feedback and save semantics. |
| Feedback/state | `LoadingState.razor`, `ErrorState.razor`, `EmptyState.razor`, `PageNotification.razor` and existing form alerts. Localize caller-supplied text. |
| Draft actions | Template's `UnsavedChangesBar.razor`; use `FormActions.razor` for section-specific commands. `UnsavedNavigationGuard.razor` and `FormDraftMonitor.razor` retain protection/invalid input tracking. |
| Dialogues | `ConfirmationDialog.razor`, `ModalFrame.razor`; reuse their focus behavior. |

Overview order is **relevant indicators → Identity → Configuration → family-specific panels**. Present saved values, even when a form draft exists. Summarize all applicable saved configuration, including declared references and structured settings. Never resolve or display Secret contents. Avoid disabled forms as a read-only summary.

Definition order is **Identity → Configuration → family-specific panels**. Where both exist, value bindings precede type-specific options. Preserve specialized editors. Show field errors once, beside their field; retain genuine cross-field/transport failures in feedback. Avoid redundant information banners and technical contract panels without an actionable user need.

## Navigation integration boundary

[#1233](https://github.com/gbaudrit/agentstration/issues/1233) owns hierarchy, fragments/history, focus and mobile drawer behavior. Reuse that implementation; do not create a second engine in this template. Its contract must retain two levels, icons, directly selectable Tools, and an independent final primary YAML entry. There is no five-tab cap.

The Agent pilot consumes the component merged in PR #1237. The template places one `ResourceNavigation` instance in its sticky right rail, separated from the content by the existing `--space-6` gutter and its shared `ResourceNavigationTrigger` in the header. The external trigger reuses the same drawer, localized label, expansion state and focus-return behavior; no second navigation engine or modal is created.

Agent hierarchy: Overview; grouping-only Definition with Configuration, Model and execution, Tools, Contracts; Access when manageable; independent final YAML. Stable destinations are `#overview`, `#definition/configuration`, `#definition/model-execution`, `#definition/tools`, `#definition/contracts`, `#access`, `#yaml`. Configuration retains Identity, behavior and labels. Model and execution retains the profile pickers. Visited subviews remain mounted.

The adopted selection model is fragments only, never obsolete `view`/`tab` selectors. Preserve canonical resource paths and the `namespace`, `scopeRef`, `context` coordinates. Unknown/unauthorized destinations must use #1233's authorized fallback. Keep existing leave-resource/context guards.

## Agent example and draft ownership

The real Agent pilot supplies the following composition (see the complete executable markup in `Components/Pages/AgentEditor.razor`):

```razor
<ResourceDetailsTemplate Name="@savedResource.Definition.DisplayName"
                         ResourceType="@Localizer["Agent"]" Domain="agent" Icon="◎"
                         TechnicalName="@savedResource.Name" Namespace="@savedResource.Namespace.Value"
                         ScopeRef="savedResource.ScopeRef" Description="@savedResource.Definition.Description"
                         Content="@content" Feedback="@feedback" HasDraft="@isDirty"
                         Sections="PageSections" ResourceIdentity="@($"{loadedKey}|{selectedScope}")"
                         DefaultSection="overview" SelectionChanged="SelectSection">
    <Metadata><span>@Localizer["Generation", generation]</span></Metadata>
    <PrimaryActions><!-- existing authorized Run link --></PrimaryActions>
    <Actions><!-- existing authorized reconcile and contextual return --></Actions>
    <DraftActions>
        <button type="button" @onclick="DiscardAsync" disabled="@saving">@Localizer["Discard"]</button>
        <button type="submit" form="agent-resource-form" @onclick="() => applyRuntimeOnSubmit = false"
                disabled="@(saving || HasRawDraft)">@Localizer["SaveDraft"]</button>
    </DraftActions>
</ResourceDetailsTemplate>
```

Keep visited forms mounted with `hidden`; retain their `EditContext`, invalid parser input, labels and schema/editor state across section changes and responsive transitions. Only replace the model/context after a successful save, explicit discard or resource reload. Ignore superseded asynchronous load completions before applying snapshots or resetting section/draft state; Agent demonstrates a load-version check. Fragment-only transitions must not reload the saved resource. External submit buttons target the real `EditForm` by its stable HTML `id`; they must still run its validation. Do not call a save directly to bypass an invalid hidden form.

Agent's overview reads `savedResource`, not the draft. Its dirty action surface remains available in Overview after visiting Definition, and Discard restores the saved snapshot. YAML retains its apply command and invalid-form guard; saved-resource YAML Save/Discard uses the same draft-action region, with the family-owned YAML handlers. The template has no API client and never determines whether saving succeeded.

## Verification and delivery

Use focused component checks for template states/optional slots, retained form instances, localized rendering, authorized/read-only behavior and save/discard routing. Maintain existing browser regressions and smoke-test the executable at desktop/mobile widths, both appearances, long identities/values and keyboard/touch use. Check save actions against both navigation regions and safe areas.

The new reusable template journey is deferred to native child [#1236](https://github.com/gbaudrit/agentstration/issues/1236), after explicit maintainer UX validation and implementation merge. Navigation-specific follow-up remains #1234. Do not close #1235 before its children are complete or a maintainer records a waiver.

Initial loading is not a draft. Agent only enables dirty-navigation protection after its saved baseline is loaded. Preferred-culture redirects retain the section fragment, and default context URL normalization uses the public Blazor API so the server navigation state and browser coordinates stay synchronized.
