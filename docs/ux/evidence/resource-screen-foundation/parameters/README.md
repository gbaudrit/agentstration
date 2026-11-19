# Parameter: validated Console resource foundation

The working tree was based on `7e4a53f2`, containing the validated `eb0632a5` foundation. [Exact source and host hashes](source-proof.json) identify the code built for the final campaign. The Console assembly and its actual default Web-host copy match; all builds completed before the browser campaign and no assemblies were rebuilt during it.

Parameter now uses `ConsoleResourceDetailsTemplate`, its standard saved Overview/Identity/Configuration, independent authorized Access and final YAML. One `ResourceSectionState` and section declaration dictionary are shared with the retained `ResourceDefinitionForm`; Identity precedes Configuration, and mapped field errors route/focus correctly. Creation remains a `CreationWizard` using the same common form and identity controls.

The descendant-use policy remains a family Definition section: it is a Parameter use rule, distinct from resource access administration. Label values are displayed inside Identity but remain read-only because the Parameter create/update API accepts properties and has no metadata-label mutation contract. The existing naming policy is forwarded to the common identity editor. YAML exports the actual saved resource read-only; it does not introduce a new mutation command. No activation state is fabricated.

Refresh retains a dirty model, its EditContext, invalid input and the original ETag. A newer server snapshot is adopted only on explicit discard or when no draft exists. A replaced UID/address/owner clears the old private resource and draft. Commands preserve exact scope, existing confirmation and family validation.

Verification:

- Release build of affected components and actual Web/Workplace hosts: zero warnings/errors.
- 47 targeted component/model cases pass, including Parameter draft/refresh/ETag/error routing/read-only/replaced-resource cases, common Identity capability behavior and Agent resource-template regression cases.
- TypeScript passes. Both maintained naming desktop/mobile cases pass after updating Parameter edit navigation to the reserved Identity section.
- Final Parameter desktop English/dark and French/light genuine-touch probes both pass creation, typed-value validation, saved Overview, concurrent refresh retention, explicit discard, first-error focus, exact UID/scope/typed writes and YAML. Each capture follows a document-width check; the touch probe also verifies Escape and focus return.
- Both maintained Agent desktop English/dark and French/light touch cases pass after the minimal shared Identity extension. Agent's source, styles, translations and model hashes are unchanged.

| Surface | Desktop English/dark | French/light touch |
| --- | --- | --- |
| Saved Overview | [Capture](parameter-overview-desktop-en.png) | [Capture](parameter-overview-mobile-fr.png) |
| Typed-value error | [Capture](parameter-invalid-value-desktop-en.png) | [Capture](parameter-invalid-value-mobile-fr.png) |
| Retained concurrent refresh | [Capture](parameter-retained-refresh-desktop-en.png) | [Capture](parameter-retained-refresh-mobile-fr.png) |
| Saved YAML export | [Capture](parameter-yaml-desktop-en.png) | [Capture](parameter-yaml-mobile-fr.png) |

Initial transient attempts failed in the temporary ES-module configuration and in an uppercase fixture name rejected by the unchanged Parameter API. The final probes use explicit ES-module configuration and valid lowercase prerequisites. Assertions and timeouts were retained. New reusable UX scenarios remain deferred under #1234/#1236; this change maintains existing journeys and uses transient local probes. CI failures are left to the maintainer's parallel PR #1238 work.
