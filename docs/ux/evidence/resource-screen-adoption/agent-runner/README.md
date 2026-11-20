# Agent Runner: real application evidence

Product source: `3163e81f4b2a1e1255f8791fb2026cb7cf437375`, including implementation `079dd755` and indicator sizing `6529b30e`. The Console component output and the copy in the actual default `Agentstration.Web` host both had SHA-256 `6f554b1d5b23eb01c023b0f86f1e68205c9239254af0855a77c13a7b879b0122`. All builds completed before the campaign; running assemblies and CSS outputs stayed unchanged.

Both transient probes passed: English/dark desktop (1440px), French/light with genuine touch interactions (390px), and an additional desktop 1050px check. They create and inspect real deterministic Runtime Runs through the application; retain the request and a rejected timeout across sections; validate the corrected request and await successful execution; inspect recorded execution facts, actual Worker placement and bounded history. The touch probe waits for the open navigation dialog, presses Escape and verifies closure plus focus return. Desktop also verifies Back navigation. Each screenshot follows a full-document horizontal-bound assertion, and Overview additionally checks that metric values fit their cards.

| Surface | Desktop English/dark | French/light touch |
| --- | --- | --- |
| Saved Overview, Identity and configuration | [Capture](agent-runner-overview-desktop-en.png) | [Capture](agent-runner-overview-mobile-fr.png) |
| Retained invalid request | [Capture](agent-runner-invalid-request-desktop-en.png) | [Capture](agent-runner-invalid-request-mobile-fr.png) |
| Successful response | [Capture](agent-runner-response-desktop-en.png) | [Capture](agent-runner-response-mobile-fr.png) |
| Recorded execution facts | [Capture](agent-runner-execution-desktop-en.png) | [Capture](agent-runner-execution-mobile-fr.png) |
| Actual Worker placement | [Capture](agent-runner-worker-desktop-en.png) | [Capture](agent-runner-worker-mobile-fr.png) |
| Bounded recent history | [Capture](agent-runner-history-desktop-en.png) | [Capture](agent-runner-history-mobile-fr.png) |
| 1050px layout | [Capture](agent-runner-mid-width-desktop-en.png) | Not applicable |

Six component cases cover invalid-input/EditContext retention, late default-context normalization preserving pending input and an already created run, exact Agent namespace/owner/UID/generation with API-owned execution-scope inference, historical generation/model facts, permissions and superseded replies. The Fast lane passed all 1,741 cases, including 859 Console cases, after the context correction. The final subsequent product change sizes the two Overview indicators and wraps their values; the fresh probes above verify it. TypeScript passes after the maintained model-profile-denial journey was updated to open the existing Agent wizard's Model and execution step.

The original touch attempt exposed late default-context normalization resetting the request; implementation `079dd755` fixes it. A later touch attempt reached all surfaces but sent Escape before waiting for the open dialog; the probe preparation was repaired without changing its assertions or timeout. Only screenshots from the final passing source above are included. Earlier captures with clipped metric values were replaced.

The screen distinguishes definition ownership from execution context and recorded facts from current Agent configuration. Agent generation remains a generation. A direct execution request keeps its single request form and three-state execution policies; it has no fabricated resource-creation wizard, namespace, activation or publication lifecycle. History retains the existing bounded API and technical-name match and exposes recorded qualified Agent references and execution scopes.

The required Integration repeat passed 1,540 tests with 28 optional external integrations skipped (1,568 total, zero failures). Fourteen unique maintained browser cases and all three collection inventory checks pass across the sources detailed in the [adoption matrix](../../../resource-screen-adoption.md). The new reusable UX journey remains deferred under #1234/#1236. No pull request, approval, merge or issue closure is part of this pause point.
