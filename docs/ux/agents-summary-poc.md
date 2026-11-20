# Agents and Flows Summary and independent actions POC

This proof of concept covers the Console Agents and Flows collections and their persisted overviews. Other resource lists, creation forms, query contracts and pagination remain unchanged.

The extracted Summary is used by `AgentEditor`, including its inherited read-only state. The separate Pack/namespaced Agent detail adapter retains its existing read-only overview composition; its list navigation and restricted commands are preserved. Migrating that adapter is a concrete follow-up to assess during generalization.

## Collection

The name link and Open explicitly select `#overview`. Edit definition selects `#definition/configuration`. Existing namespace, exact owner and administrative context are preserved. The menu order is Open, Edit definition, Run, Preview, separator and Delete, omitting unauthorized or inapplicable commands. Delete retains confirmation, a fresh ETag and ownership/management checks.

The menu uses neutral link/visited colors, decorative icons, aligned touch targets, a subtle separator and a danger treatment reserved for destructive commands. `CollectionActions.Destructive` is an optional screen-supplied region; `ShowDestructiveActions` can hide it, including its separator. Agents exposes that presentation choice as `ShowDeleteAction` (default `true`). Existing authorization and managed/inherited restrictions remain mandatory even when display is enabled. This is a choice made by the screen composition, not a stored user preference.

Preview uses the shared `ResourcePreviewPanel` and existing `ModalFrame` keyboard/focus handling. After the initial empty-container checkpoint, the maintainer requested populated Preview as the next POC. Agents now mounts `AgentResourcePreview` only when opened. This family adapter performs one exact owner/namespace/name read and supplies its saved snapshot to the same `AgentResourceSummary` used by the overview. Ordinary rerenders do not refetch it; close, replacement selection and context changes cancel or reject late reads. Shared loading/error/not-found/forbidden feedback and a bounded retry remain in the panel. The list query and pagination are unchanged.

Preview reuses `ResourceIdentitySummary` for UUID, display/technical names, namespace, description, owner, generation, labels and copy controls, including the existing Extension provenance component when applicable. Configuration is initially expanded and uses the Summary's existing saved-value renderer. Preview supplies Open and authorized Edit links to the Summary's optional quick-action region; the Summary retains its existing authorized Run link. It offers no reconciliation or deletion command. It does not load model resolution, readiness, revisions or editor pickers. Those remain on the full resource page. Desktop uses a wider side panel; mobile fills the viewport, with a fixed close header and an independently scrolling body.

Extension-managed resources retain the provenance component's existing authorized inventory lookup. Ordinary Agent Preview needs only its one definition read; no per-row Preview component or request is created before opening.

## Family Summary

`AgentResourceSummary` composes the saved Identity supplied by the Console details adapter, saved Configuration, the existing declared/resolved model and pinned ToolSet information, and its own Quick actions. It performs no data loading. The editor still owns loading, saved snapshots, draft forms, ETags, command execution and feedback.

The optional `OverviewSummary` region receives the already-composed Identity fragment, including Extension provenance. It replaces the default family content/Identity/Configuration composition for this adopter, avoiding duplicate panels. Metrics retain their existing shared region. Other adopters keep their previous composition.

Quick actions expose the existing Run and Reconcile deployment commands under their permissions on the full overview. Reconcile remains unavailable while busy or while a draft exists. List actions and Summary actions are independent: Reconcile is present only on the full overview, and no Summary is mounted for unopened collection rows. Preview reuses the Summary with its own additional navigation actions and saved configuration, without loading operational diagnostics. All commands continue to use existing application services.

## Generalization findings

- A family can extract overview presentation without moving its editor, loading or mutations.
- Shared Identity should remain supplied by the Console adapter to retain governance and exact ownership consistently.
- Each family must explicitly choose its overview/edit destinations and the actions useful in each surface; action sets need not match.
- Existing collection renderers embedded in the overview move with the Summary; their inventory ownership must follow that extraction.
- Quick actions should disappear entirely when none are authorized. They are ordinary existing commands presented prominently, not a new command execution system.
- Summary content remains based on the saved resource while definition edits are pending.

This POC awaits maintainer UX validation. It does not implement the deferred reusable journey #1242 or generalize other families beyond Agents and Flows.

## Flow and browser-history extension

`FlowResourceSummary` extracts the existing topology, Identity and Configuration from the full Flow overview. The detail page retains its metrics, forms, commands, loading and real publication history. After synchronization with the shared Flow detail adapter, it supplies the identity fragment through `OverviewSummary` just like Agent details. `FlowResourcePreview` reads one exact snapshot only when opened. Its list actions are Open, authorized Edit and Preview; its independent quick actions add the existing designer for editable non-orchestration definitions. No Delete or Run command is invented. Inherited and Extension-managed resources retain their inspection and editing restrictions. Preview omits the unavailable last-publication date instead of substituting an update timestamp.

Both summaries expose `ShowConfigurationViewSwitch` (default `true`). Preview disables it: the table/card selector and empty toolbar disappear, while the renderer still adapts automatically on mobile. `ResourceQuickActions` shares compact spacing, icons and responsive action alignment; each family still supplies its own applicable commands.

`ResourceCollectionPreview` keeps the modal lifecycle and URL state shared, without any resource client. Opening pushes `#preview/{kind}/{encoded owner}/{namespace}/{name}` while preserving the list query. Normal navigation to a resource retains that history entry, so browser Back reopens the exact Preview. A direct fragment works independently of the currently loaded result window and does not fetch other list pages. Closing replaces the fragment with the collection URL; context changes close the panel and cancel pending reads. Invalid or other-family fragments are ignored. No resource content is stored in the URL.

## Validation (2026-11-20)

- Restore completed. The standalone Web Release build passed with zero warnings.
- Agents collection/permissions, Summary and persisted-editor regressions: 43 passed. The final targeted run used the built MSTest assembly directly after the `dotnet test` launcher encountered an IPC handshake failure.
- Shared components: 193 passed, including bilingual empty Preview and existing details-template behavior. The three changed English/French catalogs have symmetric, duplicate-free keys.
- Real Edge desktop (1440 × 1000) and mobile (390 × 844) smoke checks passed: menu order, keyboard navigation/Escape/focus restoration, viewport bounds, empty Preview, no browser API request on Preview, Open/Edit destinations, independent quick actions and saved Summary while a draft disables reconciliation. The collection component regression also verifies zero detail reads on Preview. No model invocation or resource deletion was performed by the browser smoke.
- The initial Fast lane reported 2,307 passed / 21 failed out of 2,328. After repairing the old inspector assertion for Preview, the complete Console Components assembly reported 1,372 passed / 20 failed; the remaining failures concern other families (refresh, deletion, Flow orchestration and form assertions). Global validation is not green.
- The complete Release solution build stopped on the unchanged `Agentstration.Console.Web/Features/Flows/FlowDesignerBackend.cs:27`: `FlowGraphDefinition` is passed to `FlowYamlPresentation.ToYaml(FlowResponse)`. The POC does not change that backend. Consequently the Integration lane could not launch the missing Console Web test binary; the run was interrupted without a successful consolidated result. Optional PostgreSQL/live-provider tests were skipped by their existing conditions.
- Existing collection-catalog assertions: two passed; the exhaustive inventory comparison failed on four unrelated missing configuration frames in Organization Member, Storage, Storage Type and Trigger details. The two extracted Agent frames and their new Summary source are mapped explicitly; the POC does not reconcile those other families.

The local review instance uses isolated Development data and the deterministic AI configuration. The Ollama Extension is local; a live Ollama model is not required for the presentation checks. Its unresolved model availability is retained as an actual resource status rather than fabricated readiness.

### Menu appearance and optional Delete follow-up

The menu refinement passed 44 Agents cases and all 195 shared-component cases, including toggling destructive-only menus without leaving an empty disclosure, omitting the separator, and retaining authorization when display is enabled. The Web Release build passed with zero warnings. Real Edge smoke checks passed in light/dark themes, desktop/mobile and keyboard, including neutral link colors, a distinct danger color and the existing Preview/navigation/draft assertions. A new attempt at the full Fast lane stopped on the .NET test launcher's IPC handshake mismatch; it does not provide a new consolidated suite result.

The populated Preview follow-up removes the diffuse menu/panel shadow. The Actions trigger retains a fine inset keyboard focus indication instead of an outer halo. The earlier empty-Preview/no-fetch checks above describe the previous checkpoint; the populated POC explicitly allows one targeted definition read on open.

Populated Preview validation: 55 focused Agents tests passed, including exact-scope reads, owner mismatch, denied/missing resources, missing owner, retry, replacement-response rejection and cancellation when unmounted; all 195 shared-component tests passed. The standalone Web Release build passed with zero warnings. Real Edge light/dark, desktop/mobile and keyboard smoke passed, including shared Summary content, no model/editor form, scrolling without horizontal overflow, an always-visible close control and focus restoration. The prior global-build/runner limitations remain unresolved; no new consolidated functional-suite success is claimed.

### Flow, quick actions and history follow-up

- Standalone Web Release build: passed, zero warnings.
- Focused Agents/Flows components and persisted-detail regressions: 126 passed. Shared component suite: 206 passed, including asynchronous server navigation before browser acknowledgment, exact-address restoration, invalid fragments and hidden view selectors with automatic narrow-screen cards.
- Real Edge smoke passed at desktop/mobile sizes, with light/dark Agent menus: independent quick actions, populated summaries, no configuration selector, bounded scrolling, close/focus, Open/Edit, browser Back from both destinations, Back/Forward within the collection and reload of an incoming Preview URL. The Flow configuration and diagram use the extracted summary.
- Existing `flow-collection.spec.ts` English/dark desktop journey: passed, including paging, copy, publication display and inherited resources. Existing row-action assertions were updated for the additional column.
- Complete Console component suite: 1,395 passed / 20 failed out of 1,415. The same unrelated failures remain; the focused Flow regressions pass. The collection catalog still reports two passed and one failed on the four previously unmapped frames outside this POC; the extracted Flow and Agent frames are mapped. No new full-solution or consolidated functional-lane success is claimed.

The browser checks use only local deterministic presentation data. The new reusable UX journey remains deferred pending maintainer validation.

Identity disclosure correction: Preview initially inherited `ResourceIdentitySummary`'s non-collapsible standalone mode. `Collapsible` and `InitiallyCollapsed` now override disclosure independently of `HeaderOwnsIdentity`, so complete Agent and Flow identities start expanded and can be folded without losing their facts. Existing defaults for full details and the identity popover are retained. Validation: Web Release build passed with zero warnings; 126 focused Console and 208 shared component tests passed. Edge desktop/mobile smoke passed, including folding and reopening Identity in both family previews.

## Generalization tracking

The POC branch is synchronized with `origin/codex/ux-corrections` at `40643ab6`, retaining the new Flow draft/refresh lifecycle and shared return-to-list navigation. The optional family Summary region preserves the current template's behavior when no owner scope is supplied. The synchronization does not broaden the implementation beyond Agents/Flows.

Generalization is tracked by [FR #1257](https://github.com/gbaudrit/agentstration/issues/1257) in milestone **Unify UX**. Its native Sub-issue [#1258](https://github.com/gbaudrit/agentstration/issues/1258) owns the deferred reusable Actions/Preview/history scenario and remains gated on explicit validation and merge of the future rollout. The FR requires an adoption/exception matrix, independent action sets, exact scoped lazy reads, saved Summaries, configurable disclosures/view selection and browser-history restoration for all applicable resource lists.

### Validation after synchronization

- Solution restore completed. Standalone Web Release build passed with zero warnings.
- Focused Agents/Flows, persisted-detail and localization regressions: 143 passed. Complete shared-component suite: 221 passed, including the synchronized optional-owner identity contract.
- Edge desktop/mobile smoke passed on the synchronized executable: independent actions, saved summaries, Identity folding/reopening, hidden configuration selector, focus/scroll bounds and exact Preview restoration through Back/Forward/direct links. The existing Flow list journey had already passed before synchronization.
- The full Console component assembly reported 1,548 passed / 30 failed out of 1,578; failures are in other families, while the targeted Agents/Flows cases pass. No all-Console success is claimed.
- Full solution build remains blocked by the unchanged `Console.Web/Features/Flows/FlowDesignerBackend.cs:27` type mismatch. The attempt also encountered local Extension file locks; that POC process was stopped before targeted builds and restarted for smoke testing.
- The first Fast attempt reported 2,522 passed / 32 failed out of 2,554 before the final shared-component refresh. The current shared suite then passed independently; the repeated Fast launch hit the SDK's IPC handshake mismatch, so it supplies no successful final consolidated result.
- Integration could not launch the missing Console Web test executable after that build failure. Other modules started (Bootstrap passed), but the run was interrupted without a consolidated result because the lane was already incomplete. Optional live-provider/PostgreSQL workloads were not enabled.
- Collection catalog assertions: two passed; exhaustive source reconciliation still fails on uninventoried configuration frames outside the extracted Agent/Flow renderers. The moved POC frames and source-audit entries are updated; broader reconciliation belongs to the generalization inventory.
