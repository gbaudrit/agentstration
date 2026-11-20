# Agent runner — direct execution and Runtime Run inspection

`/agents/{name}/run` and `/runs/{runId}` are operational surfaces, not an editable Agent definition or a control-plane Resource envelope. RuntimeRun owns its string ID, exact definition generation/UID/owner and execution scope, lifecycle/events and specialized cancellation/retry APIs. The route therefore retains the shared ResourceDetailsTemplate with its existing execution hierarchy, recent bounded history and final raw JSON inspector. It does not fabricate Definition, generic Access, namespace/labels, YAML or a creation wizard for an execution request. The real Agent identity is shown before creation; a recorded Run shows historical facts and its exact Agent reference separately. Saved Agent tags now stay in that identity panel. Real recorded status/duration metrics remain above Identity, followed by saved wrapping Configuration.

The request uses ResourceDefinitionForm, its localized annotations and existing AgentRunnerModel; execution/validation rules and exact ToRequest references are unchanged. Prompt, context and parameter drafts update locally before blur; invalid numbers and the same EditContext survive sections and refresh. Streaming and tool-argument retention remain actual three-state selects. Execution permission remains independent from definition write permission, management and inheritance. Reconcile, cancel, retry, model policy resolution, readiness, SSE resume, event filtering and bounded history retain their existing APIs.

Typed loading/refresh/error states and retry replace isolated loading/error branches. A refresh of the exact UID/namespace/owner keeps a pending request and its loaded generation. A changed generation is buffered until explicit discard; no silent request rebasing. Private read denial, not-found/replaced UID and lost event-read access clear the request, run/events/history and model/readiness data and stop observation. Recoverable errors retain pending input; stale/disposed completions remain guarded. The protected Agent resource screen, styles and translations are unchanged, and no shared component was extended.

**15 focused component/model cases pass**, including real request references, independent permissions for managed/inherited definitions, before-blur and invalid input retention, generation conflict/discard, recoverable/private refresh, initial retry, replaced UID, late replies and live event-read denial. TypeScript, symmetric duplicate-free EN/FR catalogs and all three Release hosts pass without warnings/errors. Native English/dark desktop and French/light true-touch smoke verifies incomplete request retention across sections/refresh, real successful execution, actual definition/execution scopes, response, historical Overview, raw JSON and history. The existing desktop/mobile execution-scope journeys also pass. These 14 PNGs are unmodified captures; exact tested source/base/host hashes are in verification.json.

A duplicate separate execution journey is **Not applicable** because the existing execution-scope journey remains maintained; transverse #1236 stays deferred until explicit UX validation and merge. No CI failures investigated or repaired, and full solution lanes omitted at the maintainer's request.

- [agent-runner-invalid-request-desktop-en.png](agent-runner-invalid-request-desktop-en.png)
- [agent-runner-overview-desktop-en.png](agent-runner-overview-desktop-en.png)
- [agent-runner-retained-refresh-desktop-en.png](agent-runner-retained-refresh-desktop-en.png)
- [agent-runner-response-desktop-en.png](agent-runner-response-desktop-en.png)
- [agent-runner-recorded-overview-desktop-en.png](agent-runner-recorded-overview-desktop-en.png)
- [agent-runner-raw-desktop-en.png](agent-runner-raw-desktop-en.png)
- [agent-runner-history-desktop-en.png](agent-runner-history-desktop-en.png)
- [agent-runner-invalid-request-mobile-fr.png](agent-runner-invalid-request-mobile-fr.png)
- [agent-runner-overview-mobile-fr.png](agent-runner-overview-mobile-fr.png)
- [agent-runner-retained-refresh-mobile-fr.png](agent-runner-retained-refresh-mobile-fr.png)
- [agent-runner-response-mobile-fr.png](agent-runner-response-mobile-fr.png)
- [agent-runner-recorded-overview-mobile-fr.png](agent-runner-recorded-overview-mobile-fr.png)
- [agent-runner-raw-mobile-fr.png](agent-runner-raw-mobile-fr.png)
- [agent-runner-history-mobile-fr.png](agent-runner-history-mobile-fr.png)
