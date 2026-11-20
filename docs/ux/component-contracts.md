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

`ResourceIdentitySummary` presents the common read-only identity, explicit empty description, optional owner scope/namespace and short badges. Family-specific configuration stays in adjacent `FormSection` panels. Model Profile demonstrates this summary. The Agent resource-template pilot also includes the common Identity panel before its saved Configuration collection. `CopyableValue` copies only its supplied value through the clipboard module and reports success/failure accessibly.

`ResourceHeader` composes `ResourceIcon` and `ResourceActionBar`. Icons accept an image URL (SVG/PNG/WebP through an image element), an optional dark-theme variant, a catalog name or trusted caller content. Images keep their proportions and fall back to the family icon on failure; no arbitrary SVG markup is injected. This is the rendering contract only: persisted icon metadata, Bootstrap asset lifecycle and automatic type inheritance remain owned by [#1110](https://github.com/gbaudrit/agentstration/issues/1110). Header actions support primary, secondary and optional overflow content; each screen retains permissions and commands.

The resource icon aligns with the top of the title rather than the center of the title/description/metadata block. An optional `Namespace` qualifies `TechnicalName` into one final, folder-marked identity chip. It displays and copies `namespace/name`, with no spaces around the slash; omitted namespaces preserve the existing unqualified copy behavior. The full value remains available through the chip and copy-button tooltips when the path is truncated. See the [qualified header identity](entete-identite-qualifiee/README.md).

`LabelsEditor` edits the existing newline-separated key=value binding as explicit rows. It keeps invalid drafts, reports missing/invalid/duplicate keys in the EditContext, and never replaces duplicate values silently. `LabelsSummary` renders saved maps as wrapping badges. Agent is the first adopter; the transport schema is unchanged.

The Agent Definition section has independent Identity and Configuration destinations, followed by Model/execution, Tools and Contracts. Labels are part of its standard Identity panel. Overview presents saved configuration and three `MetricCard` components for desired generation, active generation and deployment, above the shared Identity summary. Discard restores the last saved resource snapshot, replaces the EditContext and child input state, clears validation/YAML drafts and retains the selected section. `UnsavedChangesBar` is a neutral sticky action surface that clears the mobile navigation height through `--mobile-navigation-height`; it does not own the restoration operation. `PageNotification` presents dismissible, eight-second action feedback, separate from persisted state; the notification center retains session history.

The existing `ResourceAccessPanel` puts permissions ahead of collapsible identity/governance details. It searches the authorized target inventory, renders at most 50 matching options with an honest count, and keeps exact scope coordinates. This bounds the selector rendering, not the existing inventory API. Discover/use/propagation remain independent rights with existing domain validation. Cancel restores the last loaded/saved policy without calling the API; saves preserve the ETag contract and read-only permissions.

A selected target is a pending grant and enables Save without a separate add step. Saving includes the validated pending grant together with listed changes. “Add to list” optionally prepares a batch; “Cancel addition” clears the pending target and rights without discarding listed changes. Invalid pending targets block the entire save. See the [access save correction](acces-enregistrement/README.md).

## Incomplete label drafts and YAML

`LabelsEditor` ignores entirely empty placeholder rows. Partial/duplicate entries remain local and mark the EditContext invalid without publishing an unserializable map into the resource binding. Publishing resumes only for a valid set of non-empty rows. Keep the inline row feedback; do not add a second field-level ValidationMessage beneath this editor.

The Agent YAML view uses the saved definition while the form has invalid values and remains read-only until the form is corrected. The incomplete form stays mounted, and Apply/Save handlers also enforce that guard. The shared header places type/name/state on one line and compact metadata below it. The default section gap is 12 px; theme overrides remain supported. See the [graphical correction report](corrections-affichage/README.md).

## Header references and direct navigation

`ResourceHeader.TechnicalName` is rendered after the caller's metadata; the full value remains copyable while the visible code may ellipsize. Description is a single line with a full native tooltip. Header flex items are width-constrained so long identifiers/descriptions cannot widen the mobile document.

The Agent resource-template pilot includes the shared Identity summary and saved Configuration table; execution editing stays in Definition. `StateComparison.RuntimeUrl` can link the runtime name when the active revision provides an exact name/namespace; absent an authoritative destination, keep plain text. The declared/resolved Model Profile cards and selected-profile summary link resource names directly instead of adding separate Open/View actions. Definition retains execution configuration. See [review captures](references-directes/README.md).


## Additional editor field adoption

[#1124](https://github.com/gbaudrit/agentstration/issues/1124) reuses the field components in nine additional Console editors. Secret/Vault forms subscribe to their current EditContext field changes, so in-control clearing activates the existing dirty feedback; replacing the saved/restored model also recreates its form inputs and validation state. Secret replacement input clearing has no persistence or delete side effect. Other family commands, specialized editors and non-nullable enum semantics are preserved. See the [bounded adoption and verification](formulaires-complementaires/README.md). Collection adoption remains owned by #1121.


## Adoption transverse des champs (#1127)

`FormField` expose `LabelContent` et `HelpContent` pour les libellés et aides riches, ainsi que les attributs de contexte. Un `FormCheckbox` réutilise `InputCheckbox` avec les associations du champ et les notifications du `EditContext` lorsqu’il existe. `FormGrid`, `FormSection` et `FormActions` acceptent une classe complémentaire et les attributs non reconnus.

`ClearableText.UpdateOnInput=false` conserve une saisie locale jusqu’au changement, sans modifier la fréquence des mutations métier. Un effacement du brouillon conserve le focus et n’effectue pas une première validation par perte de focus. `AllowClear=false` permet de conserver les choix obligatoires fixes (contexte courant, langue). Ces options n’ajoutent pas de validation métier.

Les pages Razor utilisent `ClearableControlTagHelper` (`clear-label`, `validation-id`), `native-form-controls.js` et `account-form-theme.css`. L’adaptateur conserve leurs champs et POST natifs, ajoute un bouton non soumettant masqué sans JavaScript et partage les variables sémantiques. Une option vide explicite préserve la validation serveur d’une sélection effacée. Les pages anonymes suivent le système et les pages de compte peuvent lire la préférence enregistrée.

Voir l’[inventaire des formulaires](formulaires-tous-ecrans/README.md), qui sépare les routes, composants imbriqués et filtres de collections.


Les créateurs contextuels Paramètre/Secret utilisent également `ModalFrame`. Son ouverture reste unique pendant l’import et l’enregistrement asynchrones ; un rendu supplémentaire du contenu ne doit pas perdre la poignée qui restitue le focus au déclencheur. Les dialogues fermés/détruits pendant cette opération libèrent les références créées tardivement.

## Resource headers and Connected Service configuration

Page headers omit the default product eyebrow. `PageHeader.ScopeRef` and `ResourceHeader.ScopeRef` render the shared compact owner badge next to the title, using the exact loaded resource scope; missing ownership does not imply Instance ownership. Existing explicitly supplied family eyebrows remain available.

`PageHeader.Enabled` and `ResourceHeader.Enabled` expose an optional activation switch; null hides it, and missing callbacks or an explicit disabled state make it read-only. Connected Service creation keeps draft activation until Save. Existing Connected Services expose activation on every tab: deactivation retains its confirmation, activation writes only the saved enablement state, and the visited definition editor retains other drafts and receives the new ETag. Options and value bindings share one Configuration panel; type selection still pins the exact UID, version and schema digest without exposing those diagnostics as form panels. The type selector sits inside Identity, with compact display name/version and exact owner details below it; homonymous choices remain qualified. Definition places value bindings before type-specific options and uses inline validation rather than a duplicate summary. The overview reuses ResourceIdentitySummary and displays all bindings and options in a single Configuration table with full, wrapping values. YAML reuses FormSection, FormField and the shared read-only multiline control. Exports remain omitted and sections follow the shared resource spacing. Technical identity and pinned contract data remain available in YAML.

The shared empty state uses a framed panel and family icon. Connected Service catalogs omit filters when no records exist and offer creation. Filtered empty results retain their filters and offer a reset. Required-field guidance and validation feedback are localized in English and French.

## Resource details template (#1235)

`ResourceLoadState` is the shared presentation contract for Loading, Refreshing, Ready, Failed, NotFound and Forbidden. `ResourceLoadFeedback` composes existing `LoadingState`/`ErrorState` with localized defaults and Retry. `ResourceDetailsTemplate` retains the resource header during blocking states and suppresses commands/navigation/editors. Its optional `OnRefresh` adds the common header action. Refreshing and failed refresh with content use compact feedback while keeping the editor subtree mounted. Console's adapter accepts a pending null snapshot only in a blocking state, without inferred owner/permissions. API loading and classification remain with the family. Agent preserves definition/YAML/policy drafts and the original ETag during refresh; newer server snapshots require explicit discard to adopt when a definition draft exists. Loss of access, deletion or replacement clears cached content. Full browser reload persistence is outside this contract.

Console adopters use `ConsoleResourceDetailsTemplate` to supply Access from the saved `Resource` UID/owner, combine policy and definition leave protection, and render `ResourceYamlEditor`. Family code provides YAML values, feedback and commands. Agent shares one `ResourceSectionState` with the adapter and Web template; section predicates are derived from its accepted path and visited destinations.

`ResourceSectionPresentation` declares required fields, read-only presentation and authorized actions for a navigable path. The template adds the required-fields guidance above both body columns, outside header metadata, only for editable sections requiring it, and renders declared section commands below content before the draft bar. Agent declares Identity, Configuration and Model/execution guidance and the YAML Apply to form command. Form rules, permissions, command callbacks and button disabled states stay with their existing owners.

Agent uses `LocalizedDataAnnotationsValidator` with localized field labels and the common `SharedFormStrings` validation messages. Model annotations remain unchanged. `FormField.Validation` contains inline errors and supplies their shared styling and accessible description. Other message stores and explicit/custom validation results retain their meaning; API/schema and nested-graph validation are outside this pilot.

`ResourceDetailsTemplate` fixes the shared header, feedback/state, content, right navigation and content-local draft-action placement. Its standard composition imposes Overview, Definition/Identity, Definition/Configuration, conditional Access and final YAML, with optional Definition children and family primary sections. Overview owns the optional metrics grid, saved Identity summary and Configuration panel order. Visited definition/access/YAML regions remain mounted; family adapters own API calls, authorization, commands and draft transitions. The legacy `Content`/`Sections` contract remains for existing adopters during migration. Agent uses the standard composition; creation remains outside this template. It composes the hierarchical `ResourceNavigation` from PR #1237, keyed to exact resource/context identity, with the shared Sections trigger in the header and one rail/drawer instance. Agent uses fragment-only selection; model/execution, Tools and Contracts are Definition children. See [resource-screen authoring](resource-screen-authoring.md) for the required ordering, exact component mapping, slots, pilot and integration limits.


`ResourceIdentityEditor` is the standard Console edit panel for Identity. It composes the common identity fields, immutable saved namespace/owner, description and `LabelsEditor` in one `FormSection`. Bindings and expressions point to the family model and use its existing EditContext. The optional `AdditionalPanels` slot appends family panels in the same Identity section and form with the shared gap. It does not replace the common panel or create a nested form. The template reserves `definition/identity` before `definition/configuration`; Agent keeps both visited subviews mounted independently and returns to Identity for invalid identity or label fields, including creation-wizard labels.

The overview's `ResourceIdentitySummary.Labels` uses `LabelsSummary` inside the Identity panel. The Console template forwards the saved resource's `Metadata.Tags`, so empty labels remain explicit and pending edits do not appear in the saved overview. Agent omits its former separate Labels panel. Direct legacy summary consumers can omit the optional Labels parameter.

The shared invalid-control CSS takes precedence over legacy form-grid input styles, including focused and hovered controls. Text inputs, textareas and selects retain the same semantic error border/ring; a valid corrected field returns to its normal state.

`ResourceDefinitionForm` owns the shared definition form assembly: retained `EditForm`, localized annotation validator, draft validation monitor, locked fieldsets, invalid-submit section routing and focus. The family supplies its EditContext, localized labels, section declarations and field/panel fragments, and retains model rules and save commands. `ResourceSectionPresentation.Fields` maps root properties to sections; duplicate/unknown property mappings are rejected. `SectionContent` keeps visited sections mounted using the layout's existing `ResourceSectionState`. Creation may instead supply its wizard as `ChildContent` and request focus after its accepted step change. The focus module ignores replaced form contexts and targets the first visible inline error/control after section rendering.

Read-only sections now enforce presentation through the form's disabled fieldsets and guarded submit callback. The layout shows one `ResourceReadOnlyNotice` with a localized reason, hides normal `Actions`, and supports explicit `ReadOnlyActions` for inspection/navigation. The Console adapter derives whole-definition read-only state from saved governance or inherited ownership; screens can explicitly declare insufficient write permissions. This state locks the common form and YAML, suppresses draft mutation actions and preserves the owner-context link where applicable. Access policy and execution commands retain their independent authorization. Agent uses the shared contract and removes its duplicate inherited-resource banner and manual field-to-section validation logic.

## Family identity capabilities

Console `ResourceIdentityEditor.NamePolicy` forwards an existing family naming policy to the common identity fields; its default remains Generic. `LabelsReadOnly` lets a properties-only family show its actual saved metadata without offering an unsupported label write. Whole-identity `ReadOnly` still locks all controls. Parameter is the first such adopter: its tags stay inside Identity, its original Parameter naming policy is retained, and its create/update payload is unchanged. Agent keeps the default behavior and remains the protected reference.

`ResourceIdentityEditor.DescriptionReadOnly` defaults to false and prevents editing an unsupported family description. `ScopeTargets` optionally forwards an already authorized family inventory to `ResourceScopeFormField.Targets`; this preserves family eligibility and avoids a second inventory request. Omitting these capabilities retains the existing Agent behavior. Vault uses both: its API has no description write, and its own scope targets remain authoritative. Provider configuration follows the shared Identity panel through `AdditionalPanels`.

`ConsoleResourceDetailsTemplate.HasAdditionalDraft` extends its existing combined leave guard for family drafts with independent commands. It defaults to false and does not display the definition save bar. Secret uses it for a pending write-only replacement: section navigation and refresh retain that input, definition discard/save do not write or cancel it, and only the separate value commands clear or persist it. Access keeps its own existing draft callback.

`ResourceIdentityEditor.NamespaceEditable` is an explicit creation-only capability, defaulting to false. When enabled, `NamespaceChanged` preserves a family creation API that already supports choosing a namespace; saved namespaces and read-only identities stay locked. Runtime Profile uses it instead of losing its existing creation field during migration. Agent keeps its existing default.

`ResourceIdentityEditor.NamespaceUpdateOnInput` defaults to true and forwards the existing text-control commit policy for editable creation namespaces. Source Provider explicitly retains its previous false value; its qualified extension binding still uses the original root model expression and exact scope/namespace/name key.

`ResourceIdentityEditor.DescriptionMaxLength` preserves an existing family text limit without adding a new domain rule; omitted values leave Agent unchanged. `NamespaceTestId` retains established namespace-control selectors. Its optional `ScopeValidation` fragment flows through the existing `ResourceScopeFormField`/`ResourceScopePicker` into `FormField.Validation`, including loading/unavailable targets, so a family root-model scope error remains beside its actual control. Category uses these capabilities for its existing 2,000-character description, namespace selector and scope validation.

The resource template ignores an empty feedback wrapper when laying out its grid, preserving the declared section gap without a phantom feedback row. Visible feedback retains its full-width region. This handles an empty family fragment without adding layout logic to each adapter; shared-style changes retain Agent regression verification.

Console's `ResourceYamlEditor.InputTestId` and `ConsoleResourceDetailsTemplate.YamlInputTestId` optionally retain a family's existing multiline-input selector when moving to the common YAML region. Omitted values add no attribute; binding, read-only/busy guards and family parsing/commands are unchanged. Data Source Profile retains `profile-yaml`; Agent uses the unchanged default and is verified independently.


`ValueBindingRequirementEditor.ValidationField` optionally maps its existing specialized message store to an owning form's real root field, so `ResourceDefinitionForm` can select Configuration and focus the invalid binding. Its default remains the existing local Selection field. The opt-in presentation exposes the invalid control group and retains inline specialized feedback, allowed-source/type/schema validation and exact scoped addresses. `CreationChanged` optionally reports contextual-dialog opening/closing to the owning screen's combined leave guard; it does not own a second guard or persist a value. Model Provider uses both and keeps the creator mounted during an authorized refresh.


Model inspection keeps its persisted Model definition read-only. Its separate Provider override command also uses ResourceDefinitionForm with the layout's section state/presentations and real fields from its concrete family edit model, but retains an independent EditContext and Provider UID/ETag/permissions. Only that command's scope clears the observed-definition read-only cascade; the observed Model form remains locked. HasAdditionalDraft joins the existing policy/leave guard without presenting a Model save. This composes existing components and does not add a generic cross-resource command framework.


The common definition form also recognizes the JSON Schema editor's existing field-validation-error messages and their schema-field containers when locating the first visible error. JsonSchemaInputEditor exposes raw-input invalidity on its FormField wrapper, so InputBase's attribute updates cannot remove the error target. Validation focus prioritizes editable inputs over adjacent help buttons, retaining ordinary keyboard access to that help. Parser/schema rules and retained values remain unchanged. Connected Service creation exercises both generated-field and raw-JSON focus; protected Agent source/styles/translations remain unchanged and its maintained desktop/mobile journeys are verified when this shared integration changes.

ConnectedServiceEditor accepts its persisted host's existing ResourceSectionState and SectionPresentations and forwards those same instances into ResourceDefinitionForm. Its family declarations map real model fields, including scoped identity, pinned type, bindings, schema options and descendant-use policy. Creation keeps its wizard. Authorized snapshot adoption/discard, activation ETag reconciliation and contextual-creator draft callbacks remain explicit family operations, without a shared loader or additional business schema.

PageHeader forwards the caller’s EnabledTestId value to ResourceActivationToggle rather than the literal property name. Optional enablement, mutation callbacks and disabled behavior remain unchanged. Connected Service creation and the maintained protected Agent journeys verify this shared header integration.

The common definition form uses `novalidate` to let Blazor receive invalid submissions from any retained section, including Overview. Native constraint validation cannot focus an invalid control in a hidden section and otherwise prevents the form handler entirely. Existing annotation, parsing/schema and specialized validation stores remain authoritative; the shared form then reveals the owning section and focuses its first inline error. Connected Service desktop/mobile probes exercise a real invalid numeric schema field hidden behind Overview before submission.


Console Task inspection opts into PendingActionPanel.DraftChanged to join its existing common leave guard and retain uncommitted text before blur. The optional callback leaves Workplace's existing commit-on-change default intact. SubmitDisabled separately prevents a response command during an authorized refresh while keeping the text editable; Busy still locks the whole interaction for an actual command. The family owns response acceptance, exact task/action/workspace checks, buffered changed views and explicit discard. No generic draft store or loader is added. Visited response panels remain keyed to the actual action ID until acceptance/discard, including refresh failures. Protected Agent source/styles/translations remain unchanged and its maintained desktop/mobile creation journeys are verified with this shared integration.
