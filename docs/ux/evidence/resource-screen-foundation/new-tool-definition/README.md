# Tool Definition creation: wizard guard review

Creation keeps its dedicated CreationWizard and standard ResourceIdentityFields. The Flow and Relay editors, freely reachable incomplete steps, retained inputs/dialogs, namespace/name/selected owner, exact published-Flow contracts and pinned Tool targets remain family-owned. No persisted-resource template, metadata write or automatic publication is invented.

The native fieldset, wizard and both final command paths now require interactive rendering and resources/write. Known denied users receive the existing common permission notice; missing scope retains the existing scope feedback. The creation path cannot load configuration sources or write before interactivity. Identity-validation feedback derives from the model’s current completeness after a final validation request, so correcting fields removes obsolete identity errors without clearing separate source/command failures. Context/ownership changes still clear the previous source/contracts and preserve the existing guarded navigation. The test administrator fixture now explicitly declares read/write permissions; the reader fixture has read only.

Validation:

- **33 focused NewToolDefinitionTests and ToolDefinitionConsoleTests pass**, including all three owner kinds for both implementations, context changes/loading, exact namespaces/source references, published schema inspection, retained relay bindings/dialogs and no-publication semantics, plus new prerender/reader guards. Both EN/FR identity-repair assertions failed before the feedback correction and now pass; final browser probes also assert that no stale alert remains on Review. The asynchronous reader test waits for the actual rendered form rather than assuming context is synchronous; no timeout is increased or assertion removed.
- Component/test and actual Web, Workplace and GovernedInventoryHost Release builds have zero warnings/errors. No shared product component or protected Agent source/style/translation changes; the preceding Agent regression evidence remains unchanged.
- Both actual application probes pass: EN/dark desktop and FR/light native touch. Each creates a Flow-backed definition from the existing fixture's exact published namespace/owner/version and a Relay using the fixture Tool's exact UID/generation/namespace/owner. They retain fields across wizard navigation, return incomplete final submission to Identity, preserve the selected context in the destination and verify empty publication history through the API. Each capture asserts document width. [Exact tested source/actual-host hashes and results](source-proof.json). The [initial adoption proof](initial-adoption-proof.json) is retained; displayed captures are from the final feedback-corrected campaign.

The probes use the existing deterministic GovernedInventoryHost, ordinary Console/API commands and isolated fixture data. No external Tool execution, live service, fabricated production resource or new reusable journey is introduced. The deferred transverse scenario remains under open/blocked #1236. CI failures are not handled. Persisted ToolDefinitionEditor/ToolRelayEditor still require their own shared-template/form migration; this creation review does not count them as migrated.

Real captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Flow configuration | [Configuration](new-tool-flow-configuration-desktop-en.png) | [Configuration](new-tool-flow-configuration-mobile-fr.png) |
| Flow review | [Review](new-tool-flow-review-desktop-en.png) | [Review](new-tool-flow-review-mobile-fr.png) |
| Created Flow-backed definition | [Created](new-tool-flow-created-desktop-en.png) | [Created](new-tool-flow-created-mobile-fr.png) |
| Relay configuration | [Configuration](new-tool-relay-configuration-desktop-en.png) | [Configuration](new-tool-relay-configuration-mobile-fr.png) |
| Relay review | [Review](new-tool-relay-review-desktop-en.png) | [Review](new-tool-relay-review-mobile-fr.png) |
| Created Relay | [Created](new-tool-relay-created-desktop-en.png) | [Created](new-tool-relay-created-mobile-fr.png) |
