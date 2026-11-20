# Model Provider: retained resource definition and creation wizard

`/modelproviders/{Name}` now uses ConsoleResourceDetailsTemplate, one shared ResourceSectionState/presentation dictionary and ResourceDefinitionForm with actual root Fields. Overview places observed status, discovered-model and authorized-usage metrics before saved Identity/labels and Configuration. Definition starts with Identity then Configuration; Models and Usages retain their specialized collections, independently authorized Access follows, and saved read-only YAML stays last.

`/modelproviders/new` uses the same standard Identity/configuration fields in CreationWizard. Stages remain freely navigable when incomplete; final validation selects the first invalid stage/control. Review stays incomplete until the required identity and bindings are complete. Provider type enriches Identity through AdditionalPanels. The existing contract permits Instance ownership only. Description/labels remain read-only because the properties-only family API cannot write them; there is no invented activation, publication or metadata-write capability.

Parameter/Secret/Literal bindings retain their exact scope/namespace/name, specialized source/type/schema/security validation and contextual creators. The value-binding component's optional root ValidationField lets the common form route binding errors to Configuration; optional CreationChanged joins the existing combined leave guard. Authorized refresh keeps the creator, EditContext, invalid inputs, drafts and original ETag. A changed snapshot requires explicit Discard; unchanged snapshots do not raise a false conflict. Main-resource 401/403/404, replaced UID/address/owner and late replies clear or cannot replace private content. Auxiliary catalog denial removes its private choices/dialogs. Recoverable observation failures retain known observations and never claim an unavailable catalog has zero models.

Family commands, discovery, confirmations, reference/specification overrides and API concurrency checks remain with their existing owners. The editor preserves an unchanged original extension reference, including relative namespace and explicit owner. Provider usages now carry the actual already-authorized profile namespace/owner through the existing service/response projection; this adds no query and enables exact links without guessing coordinates. Testing/discovery and policy authorization remain independent of definition immutability.

Validation:

- **126 focused component cases pass**: ModelProviderNavigationTests (including existing value-binding/contextual-creation and profile regressions), ParameterEditorModelTests and AgentResourceTemplateTests. Coverage includes shared form/state/presentations, incomplete wizard navigation, root binding-error routing, invalid draft/original ETag retention, unchanged refresh, open creator retention, retry and private-state clearing.
- **One focused API case passes**, verifying actual usage namespace/owner against the referenced profile and retaining deletion protection. EN/FR catalogs are symmetric (80 keys); TypeScript passes. Affected projects and all three real browser hosts build in Release without warnings/errors. Full solution lanes are intentionally not run under the requested focused-validation scope.
- Final real **desktop EN/dark and touch FR/light probes pass**, including invalid-submit focus, wizard stages, saved identity, concurrent API update plus retained invalid draft/old ETag, YAML navigation, explicit discard, corrected save, discovery, actual UID-based Access, strict document bounds and touch Escape/focus return.
- The maintained **Foundry binding journey passes** after adapting its wizard/section navigation and obsolete Secret-value-panel selector. It creates exact Parameter/Secret references, preserves write-only secret handling, deletes the secret value and verifies the unavailable saved selection.
- The maintained model-administration lifecycle, both provider-access authoring/execution cases and both Agent creation/deployment cases pass in the preceding campaign. The subsequent product follow-up changes only this family's wizard completeness indicators; the shared binding editor and protected Agent sources remain unchanged. Final family probes and Foundry run again afterwards. The initial temporary probes failed on an undefined probe-only locator; initial Foundry failed on its old Secret-panel selector. No assertions/timeouts or permissions were weakened.
- [Exact source/host hashes and validation scope](source-proof.json). Parallel Model Profile/Trigger collection commits were fast-forwarded after these campaigns; their list changes are preserved without claiming they were part of this screen's tested binaries. No CI failure is inspected/remediated. New reusable transverse coverage remains deferred under open/blocked #1236.

Real application captures (not generated images):

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Invalid Identity/focus | [Identity error](model-provider-invalid-identity-desktop-en.png) | [Identity error](model-provider-invalid-identity-mobile-fr.png) |
| Wizard Identity | [Identity](model-provider-wizard-identity-desktop-en.png) | [Identity](model-provider-wizard-identity-mobile-fr.png) |
| Wizard Configuration | [Configuration](model-provider-wizard-configuration-desktop-en.png) | [Configuration](model-provider-wizard-configuration-mobile-fr.png) |
| Wizard Review | [Review](model-provider-wizard-review-desktop-en.png) | [Review](model-provider-wizard-review-mobile-fr.png) |
| Saved overview | [Overview](model-provider-overview-desktop-en.png) | [Overview](model-provider-overview-mobile-fr.png) |
| Invalid draft after newer snapshot | [Retained draft](model-provider-retained-invalid-desktop-en.png) | [Retained draft](model-provider-retained-invalid-mobile-fr.png) |
| Discovered models | [Models](model-provider-models-desktop-en.png) | [Models](model-provider-models-mobile-fr.png) |
| Independent Access | [Access](model-provider-access-desktop-en.png) | [Access](model-provider-access-mobile-fr.png) |
