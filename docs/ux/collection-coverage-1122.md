# Reusable collection coverage (#1122)

This coverage change is a separate draft stacked on the implementation of #1121. The maintainer explicitly requested that stack before merge. This is permission to prepare and test the draft; implemented UX acceptance and both merges remain pending. Both issues stay open.

The [adoption inventory](all-list-adoption-1121.md) is the closure scope, including its embedded and specialized renderers. Route rendering and populated interactions are separate evidence:

| Layer | Cases and ownership | Assertions |
| --- | --- | --- |
| Collection primitives | `CollectionContractPage`, reused by `inspect-collections` | Applicable column order, first identity, icon/text state with neutral Unknown, authorized scope type/full accessible owner text, complete copy, final action menu, Arrow/Home/End/Escape/focus, no accidental navigation and document overflow. |
| Route families | `collection-contract.spec.ts` | Independent cases for every top-level catalog, operation/admin/account list and separate Extension tabs. Empty/loading/error rendering does not certify populated row behavior. Workspace-only routes select the prepared authorized Workspace. |
| Populated embedded collections | `collection-workflows.spec.ts` | Existing deterministic domain journeys retain their commands, prerequisite APIs and failure assertions; the common contract is additionally checked at their stable checkpoints. Cases remain independent across sources/knowledge, ToolSets, executions, Triggers, account/member/PAT, Schema/model capabilities, Pack selection/build and Workplace. |
| Long identities and owners | `collection-long-identities.spec.ts` | Homonymous Model Profiles with exact namespaces and owner/context, complete clipboard values, bounded long tenant name, visible type, hover/focus/touch disclosure, Escape, full accessible text and no owner-resolution requests caused by row interactions. Desktop/mobile and both themes with English/French. |
| Domain-specific behavior | Existing resource access, inherited-read-only, scoped execution, artifact, worker, organization, discovery and lifecycle regressions | Preserve permission/managed restrictions, confirmations, model semantics, native timelines/trees/messages, bounded data/paging and real commands. The shared presentation assertions do not replace these family assertions. |
| Inventory reconciliation | `collection-surface-catalog.spec.ts` | Every implementation manifest entry and route alias has an explicit common/native assertion mapping and domain ownership; repeated-item sources are classified. Source mapping never automatically claims a populated browser case succeeded. |

Page objects own selectors. New checkpoint `collections-contract-verified` is registered; the reusable journey is available through the normal capture registry. Independent English/French expectations are maintained in the test locale catalog rather than read from product RESX.

Final local/CI results and observed per-surface evidence are recorded after execution. This draft alone is neither UX acceptance nor fulfillment of the parent's merge gate.
