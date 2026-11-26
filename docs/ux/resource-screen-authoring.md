# Authoring resource detail screens

This chapter implements the resource-screen composition part of [#1235](https://github.com/gbaudrit/agentstration/issues/1235). It applies to persisted resources. Collection pages and creation flows remain separate. Resource creation uses `CreationWizard`; the screen owns its stages, field fragments, validation and commands. Do not change the global Console/Workplace layout to implement a resource screen.

The first adopter is `src/Agentstration.Console.Components/Components/Pages/AgentEditor.razor`, route `/agents/{Name}` with optional `namespace`, exact owner `scopeRef` and administrative `context`. Creation at `/agents/new` uses the shared wizard with the existing standard identity fields, model/runtime pickers, Tools and contract editors. Pack-managed Agents retain their separate read-only route and capability rules.

## Required structure

Persisted Console screens use `src/Agentstration.Console.Components/Components/ConsoleResourceDetailsTemplate.razor`. It composes the shared `src/Agentstration.Web.Components/ResourceDetailsTemplate.razor`, which constructs `ResourceHeader`; do not pass an arbitrary replacement header or duplicate its page layout in the business screen.

1. Resource header: a first row with the existing smaller icon/type on the left and compact commands on the right, then the saved title and Identity button on the next row. Narrow layouts wrap commands within the top area. Follow with the single-line description/full tooltip and ONE wrapping badge row for scope, version/status and family metadata. Keep one divider before content.
2. Below the header: content column and resource-local navigation column on the right. Global navigation remains in the application layout.
3. Caller feedback spans the resource area below the header and above the two body columns, keeping the first panel aligned with its navigation. Blocking resource load/error states replace editor content and draft actions; refresh feedback retains them.
4. Draft actions follow content in that same column. Reuse the shared sticky surface, mobile navigation offset and safe-area rules. Never stretch it behind the local rail.

The template owns placement, spacing, the standard section tree and the mounting/visibility of its section regions. The business screen owns data loading, exact resource coordinates, permissions, confirmations, commands, ETags, validation, feedback and draft restoration. It has no generic resource client, repository, domain-aware base page, inferred save state or automatic save on navigation.

## Compact header and saved identity

`ResourceHeader` owns the composition: a first row with the existing smaller icon/type and compact commands, then the full-width title with its Identity button, description, and scope/version/family metadata/status in one wrapping badge row. Keep the existing icon artwork, border and colors. Narrow screens wrap commands within the top area; the title keeps its own full-width row. Do not add a divider under actions. The description tooltip exposes the full saved text on hover/focus. Resource details use a smaller top content inset (other screens retain theirs), with distinct spacing between actions, icon/type, title/description and readable badges.

Pass `CollectionHref` and a localized destination `CollectionLabel` through either details template or `PageHeader` for a creation screen. Both render the same `ResourceCollectionLink`. The header renders a compact list-icon link with that accessible name and tooltip, without a visible All… text link. The screen declares the canonical destination and localized destination label. The shared link retains explicit target coordinates and adds only the current logical Console context when absent; it never copies the inspected resource owner or namespace into list filters. Preserve the canonical destination/context, never infer browser history and do not repeat the destination in `Actions`. Put destructive commands in `MoreActions`. The overflow uses an icon-only trigger with a localized accessible label. Each family still owns authorization, confirmations, callbacks and ETags. An activation switch remains available in every section, displays the saved state and should not be duplicated by an identical status badge.

`ResourceScopeBadge.JoinedName` combines kind and authorized name into one header badge (Workspace: Project, Tenant: Development, or Instance). It never rewrites physical ownership. Unknown names are omitted; internal operational Workspaces stay hidden as described below.

`ResourceIdentityPopover` opens from the identity-card button beside the saved resource name in any section; it is not part of the command row. It reads the same saved snapshot as the header, never the draft. It supplies the complete `ResourceIdentitySummary`: name, full description, wrapping technical name/namespace, logical scope, version and labels, with copy commands. It anchors immediately below its own button, clamps to the viewport and scrolls long content. Escape, close and outside dismissal preserve the user's section and draft; restore focus when appropriate. Blocking loading/privacy states suppress it, and accepted resource identity changes close/remount it. The shared component makes no resource API calls.

`ResourceIdentitySummary` uses stacked full-width facts, wrapping long values with copy controls in reserved space. Use the same body font, size and weight for fact values (including technical names, namespaces, UUIDs and badge values); reserve the smaller muted typography for labels and copy feedback. Pass the saved `ResourceUid` to show a copyable UUID as the first fact in the list; omit unknown/empty UUIDs and do not repeat them in `AdditionalDetails`. The Console adapter forwards this value automatically; direct shared-template consumers supply it explicitly. Standard Overview sets `HeaderOwnsIdentity`: order UUID, technical name, namespace, complete description, then labels and additional facts, while the saved display name and scope are already in the header. Creation/review cards retain their full identity. `CopyFacts` enables copying all inspectable facts in the quick-view. `CopyableValue` reveals its reserved copy button on hover or keyboard focus on desktop, and keeps it visible on touch devices. Keep version counters in the header instead of repeating them as Overview metrics (Trigger and Flow pilots).

Supply actual saved resource labels. A known empty dictionary renders the localized No labels state; unavailable labels render a dash in standard Overview. Verify legacy projections before mapping a metadata dictionary: Flow publishes its draft `Tags` as `FlowResponse.Metadata` (`FlowDraftService`), so this saved dictionary supplies Flow identity labels, including the functional reserved key `flow.contract`. Do not substitute unrelated execution/configuration metadata for resource labels.

The shared layout change applies automatically to its adopters. Screens supplying their own buttons still require a bounded adaptation to supply their collection destination and organize family actions. Trigger and Flow demonstrate this wiring; Agent retains its existing refresh command inside overflow because its draft/conflict workflow uses it. Do not rewrite navigation or permissions to infer these commands centrally. The collection shortcut is the final command, after the optional overflow menu. It stays available in loading, failed, missing and forbidden states whenever the family already knows its destination; no private snapshot or write permission is required to navigate to a collection.

### Enriching the header badge row

Use the existing `Metadata` region of either details template. It renders inline after scope/version and before status; do not create a separate metadata row. `ResourceMetadataBadge` is the shared value-only or labelled badge. Localize product labels/values in the family; user-authored label keys and values stay unchanged.

```razor
<Metadata>
    <ResourceMetadataBadge Value="@FlowLabels[$"Kind.{flow.Definition.Kind}"]" />
    <ResourceMetadataBadge Label="@T("Environment")" Value="@environmentName" />
</Metadata>
```

`LabelsSummary` uses this same badge component and the `key : value` format. Each badge supplies its own `CopyableValue` action to copy that exact pair; reveal it on badge hover/focus and keep it visible on touch. Identity keeps extra spacing before labels and between their heading and badges. Storage and API metadata remain raw dictionaries; formatting never changes persisted keys or values. Required-fields legends belong with the definition form, not among identity badges.

## Compact, collapsible panels and extension provenance

Use the shared `FormSection` parameters `Collapsible` and `InitiallyCollapsed`; do not implement another toggle in a family. The accessible heading button exposes its expanded state and controlled body. A collapsed body stays mounted, preserving editor/component state and the user's chosen expanded state across parent renders. Family Overview panels come first (after metrics); the Flow preview is collapsible and initially open. The saved Identity summary follows them, then Overview Configuration; both shared panels are collapsed by default. Its compact fields retain wrapping and reserved copy controls without a separator between every fact. Creation/review identities and the header quick-view remain expanded.

The Console adapter automatically builds an embedded `ExtensionManagedResourcePanel` from the saved resource's extension governance, exact owner, namespace, kind, name and UUID, using the shared template's `IdentityContent` slot. Families using the Console adapter must not also declare that component in `OverviewContent`. This section sits within Identity without another panel frame; it shows the managing extension/version/state, with installation details available on demand. User- and Pack-managed resources receive no extension section. The existing owner/UID matching and inventory-unavailable fallback remain enforced. A logical `PresentationScopeRef` affects display only; the lookup uses the physical owner. The shared Web layout has no extension client and the header quick-view triggers no inventory fetch. Flow still uses the shared template's legacy content contract and explicitly embeds the same component inside its Identity card.

The quick-view shares the same Identity summary: its heading contains the title and Close, while the UUID is the first ordinary copyable fact. Do not add separate UUID positioning or duplicate identity markup for the popup.

`ResourceIdentitySummary.Collapsible` and `InitiallyCollapsed` can override disclosure behavior independently of `HeaderOwnsIdentity`, which controls which identity facts are repeated. When omitted, both retain the existing header-based defaults. Collection Preview explicitly enables disclosure and starts Identity expanded for both Agents and Flows; the header identity popover retains its existing expanded presentation.

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

The template builds Overview in this order: optional metrics grid → optional family panels → `ResourceIdentitySummary` using the saved header identity and labels → Configuration panel. Identity and Configuration are collapsed by default. The Console adapter supplies labels from `Resource.Metadata.Tags`; render them inside Identity using `LabelsSummary`, including the localized empty state, without a separate Labels panel. Draft labels never replace the saved summary before a successful save. Its metrics slot accepts existing `MetricCard` components and controls the adaptive grid and spacing. Legacy content adopters use the same `resource-overview-metrics` grid class, styled by the shared template; do not use the generic `metric-grid`, which can reserve empty columns. Agent supplies Desired generation, Active generation and Deployment cards instead of `StateComparison`; values come from the saved generation and runtime readiness, while pending changes remain visible in the detail/status.

The [Agents Summary POC](agents-summary-poc.md) supplies `OverviewSummary` instead of `OverviewContent`/`OverviewConfiguration`. This optional typed fragment receives the shared saved Identity and composes it with the family's Configuration, panels and independent quick actions. The shared metrics grid remains first. Never supply both composition contracts or duplicate Identity/Extension provenance. The Summary has no resource client and receives saved snapshots and callbacks from its owning screen. Collection actions remain independent and do not require mounting the Summary.

Definition, additional sections, Access and YAML mount on first visit and remain mounted while hidden. Definition uses one retained fragment for its child sections so a family can keep a single validated form and `EditContext`. The Console adapter supplies `ResourceAccessPanel` from the saved `Resource` identity and existing ownership permissions. It combines access drafts with definition drafts in one leave guard, preserves policy edits across generation refreshes and rejects callbacks from replaced resource identities. The shared Web component has no dependency on Console clients or permission models. The adapter also supplies `ResourceYamlEditor` inside the shared YAML panel; parsing, authorization and save semantics remain family-owned.

Pass one `ResourceSectionState` instance to the adapter and read its `CurrentPath`, `DefinitionPath` and visited destinations in the family screen. The shared template updates that same instance after accepted navigation. Derive display predicates from it; do not maintain mutable per-section mode flags or a second visited-section registry. Request persisted section changes through navigation and let the accepted destination update the state. Creation may use the state directly for its form/YAML switch and record visited wizard editors.

Declare section presentation in `SectionPresentations`, keyed by an exact navigable section path. Each `ResourceSectionPresentation` declares `HasRequiredFields`, `ReadOnly`, optional `ReadOnlyReason`/localized `ReadOnlyMessage`, its root model `Fields` and authorized `Actions`/`ReadOnlyActions`. Pass the same declaration to the retained `ResourceDefinitionForm`. The template adds one localized required-fields legend on a discreet guidance row above both body columns, outside header metadata, only for an editable section declaring required fields. Content and navigation keep the same starting position. State/loading views suppress the legend. Agent declares required fields for Identity, Configuration and Model/execution; Overview, Tools, Contracts, Access and YAML have no page-level legend. Creation keeps its existing form-start guidance.

Declared section actions render in the content column below the section and before the draft bar, through `FormActions` with a localized group label and the supplied busy state. An absent action fragment reserves no row. Resource-wide commands remain in the header; definition Save/Discard remains in the draft bar; Access retains its specialized policy commands. Agent declares Apply to form as its YAML section action. Read-only sections hide `Actions` and may explicitly supply inspection/navigation commands through `ReadOnlyActions`. Callers provide localized labels, authorization, disabled states, callbacks and any external form target. The template does not execute commands or bypass validation. Actual form controls and application commands still enforce permissions and validation.

Existing adopters using `Content` and `Sections` remain supported during migration. Do not combine that compatibility contract with standard region content. Agent demonstrates the standard contract; this increment does not migrate other resource families or creation flows.

## Extensions adoption

The Extension detail route `/extensions/{RegistrationName}` uses `ConsoleResourceDetailsTemplate`, `ResourceDefinitionForm` and `ResourceIdentityEditor`. Its persisted registration remains the authority for UID, namespace, scope, enablement, validation and ETag. The saved observed version belongs in the header, while availability, contributions and bundle counts occupy the metrics region. Creation uses the shared `CreationWizard` inside the registration dialog, with retained Identity, Connection, Authentication and Review stages. Authorization to create a Tenant registration remains independent of permission to read the aggregated inventory.

Preserve the unified Extensions collection with its row enrollment editor and policy switches. Contribution-count links use the exact extension coordinates plus `#contributions`; details use the shared fragment navigation. The enrollment-management link supplies the instance ID so the collection opens that request, and an unavailable requested ID must never expand a different request. Registration metadata and associated enrollment reads remain independently authorized. A failure reading enrollment must not clear an otherwise authorized registration or its pending form.

`ExtensionResourceSummary` owns the saved Identity/Configuration composition for both the overview and on-demand registration Preview. The detail supplies its existing operational connections panel and the template-owned identity region; forms, drafts, ETags and enrollment mutations remain in their existing screens. Preview uses the exact saved registration without fetching inventory pages or enrollment verifiers. Managed/inherited registrations expose inspection but no Edit shortcut. Row actions and Preview quick actions are supplied independently; an enrollment quick action returns to the existing inline editor rather than nesting an enrollment modal inside Preview.

## Inputs and named regions

| Input/region | Required use |
| --- | --- |
| `Resource`, `RequestedAddress` (Console adapter) | Saved base resource: exact UID, kind, technical name, namespace, owner, generation and governance. Adapter-owned Access and technical header identity use this snapshot. Null is allowed only during a blocking load state; optional `RequestedAddress` supplies route identity without implying ownership. |
| `Name`, `ResourceType` | Required, localized presentation; saved title on an existing resource. |
| `TechnicalName`, `Namespace`, `ScopeRef` (shared template) | The Console adapter derives these from `Resource`. Never infer Instance ownership from null. |
| `ResourceUid` (shared template) | Optional saved UUID, shown as the first fact in the Identity summary and quick-view. The Console adapter derives it from `Resource.Uid`. Never generate a replacement identifier or show an empty UUID. |
| `Description`, `StatusText`, `Status` | Useful saved presentation; omit unsupported state. |
| `Version`, `VersionDescription` | Optional saved version badge in the shared badge row, between scope and status. Pass the published version for Flow or the saved configuration counter for Trigger; localize its explanatory tooltip (published/configuration version). Omit unknown versions and do not repeat the value in header metadata. Presentation does not change generation fields, API contracts or publication semantics. |
| `Domain`, `Icon`, `IconName`, `ImageSource`, `DarkImageSource`, `IconContent` | Existing family icon contract. Trusted icon content only. |
| `Metadata` | Small relevant family metadata. Do not repeat version or technical identity, or expose UID/digest diagnostics by default. Technical coordinates remain inspectable in the Identity quick-view. Supply `ResourceMetadataBadge` components here; the slot renders alongside scope/version, not on a separate line. |
| `PrimaryActions`, `Actions`, `MoreActions` | Existing authorized primary, secondary and optional overflow commands. |
| `Enabled`, `EnabledChanged`, `EnabledDisabled`, `EnabledTestId` | Optional header switch. Null hides it; no callback is read-only. Family owns confirmation, writes, ETag and synchronization with other drafts. Agent currently has no enablement property: omit the switch. |
| `DefinitionSections`, `AdditionalSections` | Optional typed family navigation extensions; Definition children and independent primary sections respectively. Reserved destinations cannot be replaced. |
| `SelectionChanged` | Resolved section callback for family-specific editor modes and YAML initialization. Standard navigation is constructed by the template. |
| `SectionState` | One resource-local presentation state shared by the screen, Console adapter and Web template. Fragment changes update it; accepted identity changes reset it. |
| `SectionPresentations` | Optional path-to-`ResourceSectionPresentation` dictionary for required-field guidance, read-only presentation and authorized section actions. Unknown/group-only paths are rejected. |
| `DefinitionReadOnlyReason` (Console adapter) | Optional explicit reason, such as insufficient write permissions. Saved governance and inherited ownership also impose read-only presentation. The adapter preserves an owner-context link when the exact owner is known. |
| `DefinitionContextScope` (Console adapter) | Optional family-authenticated physical operational context for inherited-definition presentation. Trigger passes its operational Workspace while retaining its actual owner and logical Flow context. This technical input is never displayed as the resource's administrative scope. It does not authorize commands or change Access permissions; the default ownership rule is unchanged. |
| `PresentationScopeRef` (Console adapter) | Optional explicit logical scope for the header and saved Identity when a family is stored in an internal operational Workspace. Derive it from the authenticated administrative context that owns the loaded resource. It never replaces `Resource.ScopeRef`, access-policy coordinates, command authorization, ETags or serialized resource identity. Ordinary resources omit it. |
| `AccessContent` (Console adapter) | Optional caller-authorized target-access information for a family without the generic resource-policy contract. Trigger disables generic Access and supplies its existing Flow access panel. The caller owns permission checks and any additional draft guard. |
| `ResourceIdentity` | Required when sections exist: exact route/namespace/owner/context identity, stable across fragments. The template combines it with normalized URL coordinates, observes accepted location changes and keys the navigation instance without remounting editor content. |
| `ExpandNavigationGroups` | Defaults to true: groups begin expanded; a caller can opt out and users retain their collapse choices. |
| `NavigationLabel`, `NavigationTestId` | Localized navigation landmark label and optional stable test selector. |
| `Feedback` | Resource-wide notices below the header; fixed notifications do not reserve an empty body row. Existing action feedback. Action/transport failures remain visible; do not duplicate inline errors. |
| `LoadState`, `OnRetry`, `OnRefresh` | Typed resource presentation state, recoverable retry and optional family refresh command (explicit overflow opt-in). Blocking states suppress commands, editors and navigation; refresh states retain mounted content and drafts. Family code owns requests and response classification. |
| `State` | Legacy optional custom state fragment for existing adopters. When supplied, replaces section content and suppresses draft actions. Prefer typed `LoadState` for loading/errors. |
| `OverviewMetrics`, `IdentityDetails`, `IdentityContent`, `OverviewConfiguration`, `OverviewContent` | Optional cards, additional identity facts, optional content within Identity, required saved configuration content and optional family panels. Family panels come first after metrics, then Identity and Configuration, both collapsed by default. The template owns their placement. |
| `OverviewSummary` | Optional family composition receiving the shared Identity as its context. Owns family panels, saved Configuration and quick actions instead of `OverviewContent`/`OverviewConfiguration`; used by the Agents POC. |
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
| Header | `ResourceHeader.razor`: composes `ResourceIcon.razor`, `ResourceScopeBadge.razor`, `ResourceIdentityPopover.razor` and `ResourceActionBar.razor`; optional `ResourceActivationToggle.razor`. One activation control; no duplicate Deactivate button. |
| Overview identity | `ResourceIdentitySummary.razor`. Always include it after useful indicators. Standard Overview sets `HeaderOwnsIdentity` to avoid repeating name/scope; the full description remains in the card. Its Scope region presents `ResourceScopeBadge` as localized plain text (kind and authorized name), matching the other saved facts; header and collection badges remain unchanged. This also applies to wizard review cards. Use `AdditionalDetails` only for meaningful family identity facts. |
| Panels and grids | `FormSection.razor`, `FormGrid.razor`; shared semantic spacing/theme variables. Suitable two-column fields on desktop, one column on mobile. |
| Editable identity | Console `Components/ResourceIdentityEditor.razor`: one standard Identity panel using `ResourceIdentityFields.razor`, `FormGrid.razor`, `FormField.razor`, `ResourceScopeReadOnlyField.razor` for an existing/fixed scope, Console `Shared/ResourceScopeFormField.razor` for a selectable creation scope, and `LabelsEditor.razor`. Use `AdditionalPanels` for family panels after the common panel, within the same form. |
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

## Internal operational Workspaces

In the standard Identity editor, saved or fixed ownership uses `ResourceScopeReadOnlyField` (a `ClearableText` in read-only mode), aligned with the other inputs. Show the localized scope kind and an authorized Tenant/Workspace display name when known; omit unknown names instead of substituting the current context's name or exposing IDs. Saved Identity and review cards use plain text: `ResourceIdentitySummary` supplies the `ResourceIdentityScopeAsText` presentation context to its Scope region. Keep normal badges for headers and collection metadata. Selectable creation scopes retain `ResourceScopeFormField`. Families with fixed creation ownership set `ScopeReadOnly`; Trigger also supplies the logical `PresentationScopeRef` without changing its physical model scope. Do not recreate this presentation through a family-specific `ScopeContent` fragment.

Present the resource's logical administrative scope consistently in the header, Overview/Identity, definition identity, creation and review. For a Trigger, Instance mode shows Instance, Tenant mode shows the selected Tenant, and Workspace mode shows the actual user Workspace. An inherited Flow target may belong to another scope: its owner must not be substituted for the Trigger's scope.

Internal Instance/Tenant operational Workspaces are execution and storage implementation details. Do not display their names, IDs or Workspace badges in the normal resource header, identity fields, summaries, pickers or collection identities. Do not add a second scope row in header metadata. In Workspace mode, the actual selected Workspace's authorized display name remains useful and may be shown by `ResourceScopeBadge`.

Keep the physical owner and logical execution context distinct internally, as required by ADR-0186. `PresentationScopeRef` changes presentation only; `DefinitionContextScope` and all saved identity, permission, link and command checks retain their actual coordinates. Technical YAML/export must remain faithful to the stored resource rather than rewriting its owner to match the visible badge. This presentation rule replaces the older Console convention of showing the operational Workspace separately; it does not change the accepted storage or execution model.

Verify Instance, Tenant and Workspace, including a Tenant/Workspace Trigger targeting an Instance Flow, disabled resources without an execution identity, creation/review and persisted Identity. Assert that operational names are absent from normal UI while commands, access and stored/exported ownership retain their real coordinates.

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

## Resource manifests in YAML

The YAML section presents the saved resource manifest with `ResourceYamlEditor`, not an isolated graph or a second custom textarea. Use the localized shared `ResourceManifest` title/label. Retain exact saved name, namespace and owner, rather than the current navigation context or a pending form draft. Preserve the family's canonical definition envelope and permission/command rules.

Flow uses the existing declarative Flow envelope: `apiVersion`, `kind`, `metadata.name/namespace`, `scopeRef` and `definition` containing saved display name, description, version, enablement, `spec`, functional `metadata` and optional `graph`. The graph is already embedded; only use a separate YAML panel when a family's resource manifest does not contain that content. Flow's legacy dictionary remains under `definition.metadata`, as required by the Bootstrap contract; do not silently relocate it to generic `metadata.tags`. The read-only Flow manifest uses the existing Flow editor for edits. Its Overview Configuration keeps the useful saved-settings table and omits the duplicate raw definition/metadata JSON blocks.

The Agents/Flows baseline extracts saved presentation into `AgentResourceSummary` and `FlowResourceSummary`. The Flow summary retains the existing topology viewer, Identity and saved Configuration, while its detail adapter still owns loading, commands, history and forms. Both full details supply the identity fragment composed by `ConsoleResourceDetailsTemplate`; Preview supplies its complete standalone identity. Both summaries expose `ShowConfigurationViewSwitch`; Preview sets it to false, preserving automatic mobile cards without rendering a selector or empty toolbar. `ResourceQuickActions` supplies shared compact action styling, while the family and caller choose the commands. The collection's `ResourceCollectionPreview` handles the exact-address URL fragment and modal lifecycle; family Preview adapters own their one scoped snapshot read. Flow Preview omits the publication date because that snapshot does not contain one; the full detail keeps the real publication-history date.

## Loading, refreshing and resource failures

Use `ResourceDetailsTemplate.LoadState` (`ResourceLoadState`) and the shared `ResourceLoadFeedback`; do not implement another family-specific loading/error panel. The template owns localized loading, refreshing, failed, not-found and forbidden presentation. Its `OnRetry` callback provides Retry for recoverable failures; `OnRefresh` retains the draft-preserving family command. There is no Refresh button by default; explicit `ShowRefreshAction` places an existing useful command in the overflow menu (Agent), without occupying the main row. Trigger and Flow omit it. Family code retains API calls, cancellation, authorization, response classification and command disablement. Save/validation errors remain in their existing field/general feedback surfaces, separate from load failures. The legacy `State` fragment remains available for existing adopters.

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

## Saved Summary adoption

The [#1257 adoption matrix](collection-previews-1257.md) records all 42 adopted lists, five non-resource decisions and their validation. Family Summaries receive saved snapshots and the optional template-composed Identity; Preview renders a complete standalone Identity with actual labels, logical scope, known versions and applicable Extension provenance. Native envelopes such as Entries, Workers, Plans and Artifacts retain their real identifiers without invented Core metadata. Keep history, pickers, usage reads, mutations, dirty forms and ETags in the owner screen. Inherited and managed resources retain their native read-only adapters, including namespaced Pack Agents.

Wrap new family Summaries in `ResourceSummary` to share section spacing, bounded configuration columns and long-value wrapping across full details and the Preview panel. This presentation wrapper owns no data, commands or disclosure state.

Configure Identity and Configuration disclosure independently. Preview starts both open, keeps children mounted when folded and hides the configuration selector without suppressing automatic mobile cards. The initial authentication/loading phase preserves an exact incoming fragment; enabled context replacement closes its old Preview. Embedded hosts mount outside lazy section content. The shared navigation bridge routes native Preview pushes through guarded Blazor navigation and observes accepted entries; never fill a Preview by scanning collection pages or copying a draft into URL state.
