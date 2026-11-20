# Tool Provider: validated Console resource foundation

The persisted screen uses ConsoleResourceDetailsTemplate and one retained ResourceDefinitionForm with the same state and section presentations. Standard Identity precedes connection Configuration; tools, independently authorized Access and saved read-only YAML follow. Actual tool-count and last-discovery metrics precede saved Identity/labels. Provider type is an Identity AdditionalPanels enrichment. Creation retains its wizard; its API has no namespace, description or metadata-write capability, so unsupported fields remain read-only. No deletion or YAML-write command is invented.

The family model owns existing identity, connection and environment-reference parsing rules, properties projection and exact/relative AEP extension coordinates. Commands and ETags stay in the screen. The header switch remains a definition draft until Save and mounts the retained form even when changed from Overview. Tool enablement and provider Test/Discovery commands retain their restrictions. Links preserve actual owner, namespace and administrative context.

Refresh preserves invalid values, messages, the same model/EditContext and original ETag, including changes made while the request is in flight. A newer saved definition is buffered until explicit discard; replaced identity or lost access clears private rendered content and combined drafts. A missing persisted owner yields localized failed-load feedback instead of an assumed Instance.

Provider update/test/discovery previously used an unqualified name after a qualified read. Existing family client commands and endpoints now accept the same exact owner/namespace, using existing service operations and retaining authorization, best-effort post-save discovery and concurrency semantics. A real API regression verifies namespace homonyms, exact target UID and untouched default-namespace ETag; stale writes retain this family's 409 Resource version conflict contract.

Verification:

- 14 focused component cases pass, plus one client coordinate/ETag case and one API homonym/concurrency case. Release component, test, Web, Workplace and dedicated GovernedInventoryHost builds complete without warnings/errors. TypeScript passes.
- Both final English/dark desktop and French/light genuine-touch probes pass real MCP-backed creation, saved metrics/Identity, draft activation with explicit Save, identity/connection error routing and focus, retained refresh errors/ETag, explicit discard, actual governed tools, independent Access, saved read-only YAML, exact UID/owner and strict document-width assertions. Touch navigation also verifies Escape/focus return. The existing provider-family boolean translations are reused, including Oui/Non. [Final source and actual host hashes](source-proof.json).
- Both maintained governed-inventory desktop/mobile cases pass, including managed Tool Provider consultation. Their old Flow-tab selectors were updated to current section navigation, preserving mutation assertions; the provider form is visited before asserting its lock. [Their exact source/host proof](governed-inventory-source-proof.json) precedes only the final boolean-localization follow-up; final probes and component cases above validate that follow-up.
- Initial transient MCP fixture attempts lacked chunked HTTP input and a valid unsupported-method response to the SDK's discovery probe. The corrected fixture was checked with the same MCP SDK before the successful browser campaign. The first dedicated-inventory attempt was interrupted when a stale host copy was detected; its host was rebuilt and assembly hashes checked. No timeout or product assertion was weakened.
- Protected Agent screen/model/styles/translations remain byte-identical. No shared presentation component changes in this screen. No CI failure is inspected or remediated; no full solution suite is claimed.

Real captures:

| State | Desktop, English/dark | Touch, French/light |
| --- | --- | --- |
| Creation | [Review](provider-creation-review-desktop-en.png) | [Review](provider-creation-review-mobile-fr.png) |
| Overview | [Saved Identity and metrics](provider-overview-desktop-en.png) | [Saved Identity and metrics](provider-overview-mobile-fr.png) |
| Invalid identity | [Error routing](provider-invalid-identity-desktop-en.png) | [Error routing](provider-invalid-identity-mobile-fr.png) |
| Refresh with pending changes | [Retained draft](provider-refresh-retained-draft-desktop-en.png) | [Retained draft](provider-refresh-retained-draft-mobile-fr.png) |
| Tools | [Governed catalog](provider-governed-tools-desktop-en.png) | [Governed catalog](provider-governed-tools-mobile-fr.png) |
| Explicit activation Save | [Saved disabled state](provider-saved-activation-desktop-en.png) | [Saved disabled state](provider-saved-activation-mobile-fr.png) |

Reusable transverse UX composition remains tracked by open/blocked #1236 and the adoption matrix, pending maintainer UX validation and merge of #1238. These are local regression probes, not a new prematurely implemented scenario.
