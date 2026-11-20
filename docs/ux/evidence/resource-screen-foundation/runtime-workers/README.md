# Runtime Worker: operational inspection reconciliation

`RuntimeWorkerDetailsResponse` combines an enrolled Worker summary with recent durable assignment placements. Neither is a persisted `Resource` definition: there is no resource UID/namespace/metadata, generic policy, definition-save contract or YAML document. Presence is an observed state and not activation. Preserve the existing shared operational ResourceDetailsTemplate with Overview and Assignments, metric cards before Identity, saved Configuration and physical-attempt disclosure. Do not invent a Console resource envelope, owner badge, Definition/YAML/Access editor or creation wizard for this process projection.

The screen now supplies the shared typed loading/refresh/error/retry states and one ResourceSectionState. A recoverable refresh retains the loaded inspection and selected section. 401/403/404 and mismatched Worker identity clear cached content; superseded/disposed reads cannot replace the current Worker. Initial failures expose explicit retry. Assignment links keep the exact execution scope and preserve the independent administrative navigation context. Keyed attempt disclosure stays associated with its assignment during refresh.

Existing capability/protocol/software version, presence/enrollment distinctions, capacity, heartbeat, lease/fencing history and physical-attempt data remain intact. The family query still requests the existing bounded 100 most recent assignments; there is no extra loader, unbounded history read or invented global total. No mutation, permission or backend contract changes.

Validation:

- All 11 focused Runtime observability component cases pass, including the existing inventory/placement checks and new refresh classification, privacy clearing, initial retry, superseded read and exact execution-scope/navigation-context assertions. Actual component/test and Web/Workplace/GovernedInventoryHost Release builds have zero warnings/errors.
- Both English/dark desktop and French/light genuine-touch probes pass actual enrolled-Worker inspection, observed-state semantics, Overview/Assignments navigation, refresh retaining selection, real missing-Worker feedback, strict document-width checks and touch Escape/focus return. No Worker or assignment record is fabricated in production.
- Both maintained Runtime Worker browser cases pass, including real Runtime/Flow assignment correlation, Worker loss/reconnection and history, authorization and mobile inspection. [Exact tested source and actual-host hashes; all four browser results](source-proof.json).
- Final synchronization preserves the parallel Source Registry creation correction and stricter shared browser-navigation helper. TypeScript, the 11 focused cases and actual host builds pass again; both maintained Runtime Worker cases pass again with that helper. Own Worker/test bytes match the initial desktop/touch captures. [Post-synchronization scope and exact host hashes](final-synchronization.json).
- Protected Agent source/styles/translations are unchanged. No shared product component evolves, no CI failure is investigated, and no full solution suite is claimed. Existing reusable Worker coverage is retained; transverse resource composition remains tracked by open/blocked #1236 pending UX validation/merge of #1238.

Real application captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Observed overview | [Overview](worker-overview-desktop-en.png) | [Overview](worker-overview-mobile-fr.png) |
| Assignment inspection | [Assignments](worker-assignments-desktop-en.png) | [Assignments](worker-assignments-mobile-fr.png) |
| Missing Worker | [Shared error state](worker-not-found-desktop-en.png) | [Shared error state](worker-not-found-mobile-fr.png) |
