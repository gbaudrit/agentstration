# Flow Run resource layout: real application evidence

Product source: `98575a8d11fb062b20ac292458d6dcb27138f54c`. Both the Console component output and its copy in the running default `Agentstration.Web` host had SHA-256 `30080d1c891566775491e9b3ad640beac52080d6e6aa9b515968da8eed21346e` before the campaign. Assemblies remained unchanged during execution.

The transient desktop English/dark and French/light touch probes both passed. They create a deterministic successful Flow Run through the real application, inspect saved Overview/Identity/configuration and causal output, retain expanded causal output across sections, then inspect actual Worker placement. The mobile probe uses `tap()` and verifies Escape and focus return; desktop also checks a 1050px viewport. Each capture follows a full-document horizontal-bound assertion. Component regressions separately cover response drafts, execution-scope rows, superseded event replay, hidden placement reads and denied/mismatched placement results.

| Surface | Desktop English/dark | French/light touch |
| --- | --- | --- |
| Overview | [Capture](flow-run-overview-desktop-en.png) | [Capture](flow-run-overview-mobile-fr.png) |
| Causality | [Capture](flow-run-causality-desktop-en.png) | [Capture](flow-run-causality-mobile-fr.png) |
| Worker | [Capture](flow-run-worker-desktop-en.png) | [Capture](flow-run-worker-mobile-fr.png) |
| 1050px layout | [Capture](flow-run-mid-width-desktop-en.png) | Not applicable |

These results cover the Flow Run probes only. The same campaign's maintained Worker regression passed on desktop but failed its separate mobile inventory horizontal-bound assertion (1038px document for a 390px viewport), before opening Worker details. That failure does not invalidate these successful Flow Run captures and is not claimed fixed. Earlier attempts using stale copies in the default Web host are not current-source evidence. The new reusable UX journey remains deferred under #1234/#1236.
