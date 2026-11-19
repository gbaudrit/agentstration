# Tool Set: validated Console resource foundation

The persisted Tool Set inspection screen now uses ConsoleResourceDetailsTemplate, actual saved version/member/publication metrics before Identity, saved labels inside Identity, standard Definition/Identity then Configuration, immutable publications, independently authorized Access and final saved read-only YAML. It retains a read-only ResourceDefinitionForm with the same section state/presentations and a family identity projection. The configuration remains a full saved-value collection, including qualified member/category references and annotations; labels are no longer duplicated there.

Capability limits are explicit: this screen previously exposed inspection and authorized deletion, with a read-only activation indicator. It still has no definition Save, namespace mutation, metadata write, YAML write or creation route. The existing Tool Set collection and API/Knowledge Source creation workflows retain their templates; no fictitious wizard or write capability is introduced. Managed/inherited notices preserve their meaning and do not disable independently authorized policy administration.

Refresh uses existing bounded resource/history endpoints, retains mounted sections and policy drafts across saved-generation changes, and keeps saved content on recoverable failure. Identity/access loss clears private content. The published definition hash now uses CopyableValue, preserving its full value and preventing a real mobile overflow. Member links retain exact namespace/owner/navigation context. Deletion retains confirmation, ETag and domain guards; a successful command invalidates the snapshot/drafts and leaves after that render, while rejection preserves the policy draft.

Validation:

- 19 targeted component cases pass: existing extension/provenance checks plus standard locked forms, common state/presentation identity, saved labels, immutable consultation/YAML, independent policy editing, refresh/retry retention, replacement/403 clearing, scoped deletion ETags, successful deletion/guard release and rejected deletion/draft retention. Release component, tests, Web, Workplace and dedicated GovernedInventoryHost builds have zero warnings/errors.
- Both final English/dark desktop and French/light genuine-touch probes pass real managed inspection, standard sections, full published provenance/links, read-only YAML, independently editable Access, pending-policy refresh retention and rejection without draft loss. Every capture checks document width; touch also verifies Escape/focus return. [Final source/actual-host hashes and result](source-proof.json).
- Both maintained governed-inventory desktop/mobile cases pass on the initial migrated layout, before the final published-hash wrapping/rejection-probe follow-up. [Unfiltered initial campaign proof](initial-campaign-source-proof.json) also records its failed transient Tool Set probe; it is not an all-green campaign. The final probes above verify the corrected rendering. No TypeScript source changes here; the prior TypeScript result remains applicable.
- Parallel collection-template commits were synchronized before commit. All 33 Tool Set/Tool Provider component cases and TypeScript pass after the shared update; both maintained Agent desktop/touch creation/deployment cases pass with the final Tool Set probes. A final independent Data Source Profiles list commit and its test-context adjustment were then synchronized; the 33 component cases pass again. Tool Set product sources and shared presentation sources are unchanged by that final delta. [Exact synchronization scope](final-synchronization.json).
- Agent product files remain byte-identical. This screen changes no shared presentation component. No CI failure was inspected or remediated; no full solution suite is claimed.

Observed backend limit, preserved and not presented as fixed:

ToolSetService accepts Instance, Tenant and Workspace owners, but the existing ToolSetDeletionGuard in src/Agentstration.Infrastructure/Flows/LocalFlowExecution.cs rejects non-Workspace deletion with tool_set_scope_invalid. The initial real Instance deletion expectation therefore failed. The final real probe asserts the existing 422 Workspace-ownership response, retained resource and pending policy instead. Successful deletion with a pending-policy callback is verified at the adapter component contract using a Workspace resource; this does not pretend that a Workspace has reachable descendant grant targets. A successful Workspace deletion was not exercised in the browser in this increment. The backend scope discrepancy remains a separate, observed limitation.

The first mobile probe exposed the unwrapped publication hash; the common copyable value fixes that overflow without truncating the only inspectable value. A later transient probe was interrupted because it awaited a browser HTTP response for an API call made by the Blazor server. The final probe waits for actual UI feedback and separately verifies the HTTP response via its request client. No timeout, width bound, production rule or maintained mutation assertion was weakened.

Real captures:

| State | Desktop, English/dark | Touch, French/light |
| --- | --- | --- |
| Managed overview | [Identity and metrics](toolset-managed-overview-desktop-en.png) | [Identity and metrics](toolset-managed-overview-mobile-fr.png) |
| Read-only configuration | [Saved values](toolset-readonly-configuration-desktop-en.png) | [Saved values](toolset-readonly-configuration-mobile-fr.png) |
| Immutable provenance | [Full hash and member links](toolset-published-provenance-desktop-en.png) | [Wrapped full hash and member links](toolset-published-provenance-mobile-fr.png) |
| Saved YAML | [Read-only export](toolset-yaml-desktop-en.png) | [Read-only export](toolset-yaml-mobile-fr.png) |
| Pending policy and refresh | [Retained draft](toolset-retained-policy-refresh-desktop-en.png) | [Retained draft](toolset-retained-policy-refresh-mobile-fr.png) |
| Existing deletion guard | [Retained resource/policy](toolset-rejected-deletion-retains-policy-desktop-en.png) | [Retained resource/policy](toolset-rejected-deletion-retains-policy-mobile-fr.png) |

Deferred reusable transverse composition remains tracked by open/blocked #1236 and the adoption matrix, pending UX validation and merge of #1238.
