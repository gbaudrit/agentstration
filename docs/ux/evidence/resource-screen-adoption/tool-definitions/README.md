# Saved ToolDefinition application evidence

These captures come from the actual Console compiled at `04b0659b3759d7ceb92fecfccc49fbd92fb03f3c`. They are not generated mockups. The two temporary verification probes passed on desktop English/dark (1440×1000) and touch-enabled mobile French/light (390×844), using actual `tap()` interactions in the mobile variant. The maintained schema-contract journey also passed.

Each probe creates a real published Flow-backed ToolDefinition and a draft relay targeting its exact published Tool UID, generation, namespace and owner. It checks the saved Flow overview, retained definition edits and explicit discard, immutable version history, relay configuration/bindings/contracts navigation, contextual creator placement outside forms, Escape and focus restoration, draft activation before and after save, publication review dismissal, and horizontal bounds. Publication is deliberately cancelled; this browser evidence does not claim a new relay publication. The 18 API cases cover publication and exact-owner homonyms separately.

The first probe attempt selected the hidden antiforgery input as its first form input. Both variants were rerun successfully after selecting the visible text input. No product change, assertion removal or timeout increase was needed for that probe failure.

| State | Desktop English/dark | Mobile French/light |
| --- | --- | --- |
| Saved Flow-backed overview | [Capture](flow-tool-overview-desktop-en.png) | [Capture](flow-tool-overview-mobile-fr.png) |
| Saved relay overview | [Capture](relay-overview-desktop-en.png) | [Capture](relay-overview-mobile-fr.png) |
| Explicit publication review | [Capture](relay-publication-desktop-en.png) | [Capture](relay-publication-mobile-fr.png) |
| Relay contracts section | [Capture](relay-contracts-desktop-en.png) | [Capture](relay-contracts-mobile-fr.png) |

The full-page captures include the application's fixed navigation at its viewport position. They do not prove every permission combination, every immutable historical contract, a physical touch device or the final full functional suites. Image digests and exact capture dimensions are in [provenance.json](provenance.json).
