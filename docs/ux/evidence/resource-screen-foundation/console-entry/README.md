# Console Entry interaction — operational Work projection

EntryResponse is a published Work projection with real Workspace/namespace/name/revision and resolved target, without a Core Resource envelope or writable metadata. The shared operational ResourceDetailsTemplate retains Overview and Interaction, real entry revision/loaded-message metrics before saved Identity and wrapping Configuration. The actual execution Workspace is explicitly labelled as business context; no fictitious owner, tags, generic policy, lifecycle, Definition, YAML or creation wizard is introduced. PrimaryEntryContainer, native Prompt/Form Entry renderers and specialized conversation/task/activity/result/artifact rendering remain intact.

Typed loading/refresh/retry distinguish recoverable errors from missing/forbidden/replaced/foreign identities. Exact workspace, entry, interaction, task and associated event coordinates are checked before adoption; stale/disposed reads and acknowledgements are ignored. Initial query fallback remains exact and executes at most once, while refresh never retries a command. Capability loss disables invocation without removing independently authorized inspection/cancellation. Entry-definition changes and replaced pending questions are buffered under a draft until explicit discard.

Optional EntryRenderer/PromptEntry/FormEntry draft callbacks preserve normal Workplace submission/validation contracts. The opt-in ConversationComposer acknowledgement retains a rejected message and clears only an accepted send; default OnSubmit behavior stays compatible. InteractionView tracks pending response and message drafts separately, resets only the acknowledged actual pending action, retains its composer across refresh and local navigation, and localizes disabled-state explanations. Late acknowledgements cannot clear/focus a disposed composer. No protected Agent source/style/translation was modified.

**33 targeted Console cases and 34 affected Workplace component cases pass**, including initial-query fallback, invalid/rejected request retention, independently typed conversation/pending response, changed definition/discard, input during a read, recoverable failures, invocation loss, private-read loss/foreign context/replaced identity, stale reads and disposal. TypeScript/catalog checks and three Release hosts pass without warnings/errors. Native English/dark desktop and French/light true-touch smoke verify real saved identity, actual target version, responsive rail/drawer, no overflow, fragment navigation, refresh retention, a successful deterministic submission, independent follow-up draft and Escape cancellation of leaving. The maintained Console fallback/conversation journey and protected Agent desktop/mobile journeys pass. Long-label intrinsic grid sizing is constrained locally in ConsoleEntryInteraction, preserving the specialized renderer without hiding overflowing content. All 14 PNGs are unmodified application captures; verification.json records exact tested base/source/assembly hashes.

A duplicate Console Entry scenario is **Not applicable** because the existing journey is maintained; transverse #1236 remains deferred until UX validation and merge. Pending-question transitions/rejections are verified in focused components; browser smoke uses an ordinary deterministic published Prompt Entry. Existing circuit-scoped realtime registration still joins its original workspace set: route/context safety is enforced by exact guarded reads/manual refresh, without a broader client/subscription redesign. No CI errors investigated and no full solution suites at the maintainer's request.

- [console-entry-request-desktop-en.png](console-entry-request-desktop-en.png)
- [console-entry-overview-desktop-en.png](console-entry-overview-desktop-en.png)
- [console-entry-refreshed-request-desktop-en.png](console-entry-refreshed-request-desktop-en.png)
- [console-entry-response-desktop-en.png](console-entry-response-desktop-en.png)
- [console-entry-recorded-overview-desktop-en.png](console-entry-recorded-overview-desktop-en.png)
- [console-entry-retained-follow-up-desktop-en.png](console-entry-retained-follow-up-desktop-en.png)
- [console-entry-cancel-leave-desktop-en.png](console-entry-cancel-leave-desktop-en.png)
- [console-entry-request-mobile-fr.png](console-entry-request-mobile-fr.png)
- [console-entry-overview-mobile-fr.png](console-entry-overview-mobile-fr.png)
- [console-entry-refreshed-request-mobile-fr.png](console-entry-refreshed-request-mobile-fr.png)
- [console-entry-response-mobile-fr.png](console-entry-response-mobile-fr.png)
- [console-entry-recorded-overview-mobile-fr.png](console-entry-recorded-overview-mobile-fr.png)
- [console-entry-retained-follow-up-mobile-fr.png](console-entry-retained-follow-up-mobile-fr.png)
- [console-entry-cancel-leave-mobile-fr.png](console-entry-cancel-leave-mobile-fr.png)
