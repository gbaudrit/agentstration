# Task: scoped operational inspection

The public WorkTaskOperationsDetailResponse is a Work Plane execution projection, with a Task ID/workspace, interaction, action requests, execution links, results, artifacts and activities. It is not a mutable Resource definition envelope. The existing family commands expose no metadata save, generic resource policy, Resource UID/namespace/tags or YAML. The common operational ResourceDetailsTemplate is therefore composed with Overview / Execution / Results / Artifacts / Pending actions / Interaction / Activity, rather than fabricating Console Resource metadata or a ResourceDefinitionForm. Actual status, duration and pending-action metrics precede saved Identity and Configuration. Sequence numbers in result/artifact histories retain their existing business meaning.

Accepted navigation shares one ResourceSectionState. The real Task ID is fully copyable, workspace display names come from the already authorized shared context and execution links retain the selected context. The response panels remain keyed to their actual action ID through fragments, same-context refreshes and recoverable failures. Uncommitted text is reported before blur. Fields remain editable during a read while submission waits; a newer snapshot is buffered until explicit discard when a response is pending. Input arriving during the read is retained. An unchanged read does not announce a false conflict. Clearing the last pending response adopts a buffered view on the next input-event turn, including when the server has resolved that action, without leaving a notice whose discard action is inaccessible.

The family validates the Task ID, authorized workspace and related interaction/action/message/activity identities before exposing their data. Failed reads use common Retry feedback while keeping the current response; read denials, missing tasks or identity replacement clear private content and actions. Late/disposed completions are ignored. Time-dependent duration uses the injected TimeProvider. Realtime messages for other tasks do not reload the view, and matching/resynchronization messages during a read or command are queued for the next refresh. The existing API response remains the loading boundary; no full inventory request or extra loader is introduced.

Pause, resume, confirmed cancellation and action responses keep their exact Task/action targets and independent runs/execute permission. A denied execution does not imply revoked read access. Recoverable/unconfirmed writes retain the response and show localized feedback without silently retrying a write. Confirmed operational commands reconcile their observed status while keeping unrelated response fields. The public command clients have no conditional ETag parameter; this migration does not invent one or alter their backend concurrency contract. Historical result content, artifact download destinations, pending/resolved action meaning, read-only messages and activity/FlowRun data remain intact.

PendingActionPanel has two optional presentation capabilities: DraftChanged reports input/selection intent to the owning leave guard, and SubmitDisabled blocks submission while keeping text editable during a read. Without those options its original Workplace commit-on-change and Busy behavior remain unchanged. The family still owns all loading, response acceptance, authorization, changed-snapshot adoption and explicit discard. No parallel form or collection framework is introduced. The existing observability page object now selects Execution before following its exact Task FlowRun link.

Seventeen focused Task/component/localization cases and nine PendingActionPanel cases pass, with TypeScript and all three Release hosts built without warnings/errors. Native English/dark desktop and French/light true-touch smoke inspect actual identity, complete copy without navigation, seven operational sections, exact context-bearing FlowRun links, section-preserving refresh, mobile Escape/focus restoration, NotFound/privacy clearing and document bounds. The two affected maintained Flow observability cases and both protected Agent desktop/mobile creation/deployment cases pass. The 18 PNGs are unmodified captures of the running application; verification.json records exact tested base/source/host hashes and phase results. Initial local validation caught invalid navigation icon aliases and a literal text binding; native smoke then exposed a missing initial navigation identity while Console context was pending. The adapter now supplies its loading identity and a deferred-context regression case verifies the fix. Catalog icons and actual value binding were corrected without weakening assertions or extending timeouts. Real capture review also corrected the family metric grid width, compact duration presentation and reuse of the existing identity-label catalog; native assertions verify complete metric text. The copy probe explicitly grants browser clipboard-read/write permissions and retains its complete-value/non-navigation assertion.

The browser smoke uses real API-created deterministic Flow/Entry/Task data and actual application hosts. It does not fabricate a production resource. Pending-response failure, buffering and command assertions are covered by focused component tests; the native Task smoke inspects a completed deterministic task, rather than claiming an end-to-end external human-input producer. New reusable #1236 coverage remains deferred until explicit UX validation and merge. No protected Agent source/style/translation change. Full solution lanes are omitted at maintainer request. No CI error was inspected or handled.


- [task-overview-desktop-en.png](task-overview-desktop-en.png)
- [task-execution-desktop-en.png](task-execution-desktop-en.png)
- [task-results-desktop-en.png](task-results-desktop-en.png)
- [task-artifacts-desktop-en.png](task-artifacts-desktop-en.png)
- [task-pending-actions-desktop-en.png](task-pending-actions-desktop-en.png)
- [task-interaction-desktop-en.png](task-interaction-desktop-en.png)
- [task-activity-desktop-en.png](task-activity-desktop-en.png)
- [task-refreshed-desktop-en.png](task-refreshed-desktop-en.png)
- [task-not-found-desktop-en.png](task-not-found-desktop-en.png)
- [task-overview-mobile-fr.png](task-overview-mobile-fr.png)
- [task-execution-mobile-fr.png](task-execution-mobile-fr.png)
- [task-results-mobile-fr.png](task-results-mobile-fr.png)
- [task-artifacts-mobile-fr.png](task-artifacts-mobile-fr.png)
- [task-pending-actions-mobile-fr.png](task-pending-actions-mobile-fr.png)
- [task-interaction-mobile-fr.png](task-interaction-mobile-fr.png)
- [task-activity-mobile-fr.png](task-activity-mobile-fr.png)
- [task-refreshed-mobile-fr.png](task-refreshed-mobile-fr.png)
- [task-not-found-mobile-fr.png](task-not-found-mobile-fr.png)
