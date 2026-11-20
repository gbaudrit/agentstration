# Extensions — inline registration creation wizard

The existing Extensions collection keeps its ResourceCollectionTemplate, tabs, table/card preference, bounded inventory and directly available enrollment commands. Its separate inline manual-registration creation boundary now uses CreationWizard Identity/Connection/Authentication/Verification with free incomplete navigation and top/bottom controls. ResourceDefinitionForm retains the real EditContext, localized annotations, conditional credential validation, declared root fields and first-error navigation/focus.

Standard ResourceIdentityFields and ResourceScopeFormField sit inside Identity. Only authorized Tenant ownership is selectable, namespace remains editable and exact Secret credential namespace/owner references remain intact. The POST does not write description/tags, so unsupported metadata fields are not invented. Tenant/Workspace contexts may create a registration under their independently authorized read/write capabilities even when the inventory aggregate is forbidden. The common collection contract still hides its header actions for Forbidden; this one independent creation command uses the existing family Feedback/FormActions slot. Instance does not offer an unusable Tenant-only creation wizard. No Secret value is read. Existing discovery enablement and two enrollment-policy binary options use common switches; configuration locks, independent policy saving and direct commands remain intact.

The wizard stays mounted across collection tabs, preserving values, invalid state and step position during catalog refresh. Reopening Register cannot replace an existing draft. Same-context reads retain drafts; denied/foreign/disposed contexts clear or reject private completion. Failed creation retains all inputs for explicit retry. Inventory denial clears all catalog metadata while preserving only the authorized creator's own input. Lost write capability keeps that same form read-only, including when the inventory still returns 403; genuine read/context loss clears private input. Returned name/namespace/owner are verified before acknowledgement.

All 55 targeted Extensions cases pass, including nine focused wizard cases for incomplete navigation/error routing, retained state, exact credential payload and failure/retry. Native English/dark desktop and French/light true-touch creation validates missing fields and malformed references, keeps tabs/refresh input, creates the actual Tenant registration and checks the persisted owner/namespace/credential reference, strict bounds and real captures. TypeScript/catalog checks, warning-free Release hosts and three maintained Extensions journeys pass.

These PNGs are unmodified captures of the running application. Exact source, host, report and screenshot hashes are recorded in verification.json. New reusable #1236 coverage remains deferred until explicit UX validation and merge; maintained journeys continue to be checked. No CI investigation or full solution lanes were performed, as requested.

Observed limits:

- The inherited catalogue description still uses older Workspace wording although the selected Console context and actual Tenant creation owner remain explicit. This migration corrects the creator ownership hint and records that separate pre-existing catalogue copy limit.
- This previously omitted creation boundary adds one component to the historical 51-component inventory; the reconciled current inventory is 52, not 52 distinct routes.
- The existing Tenant-context inventory endpoint refuses its Instance enrollment aggregate. This remains visible as catalog feedback. The independently authorized creator now works in the actual Tenant context despite that aggregate denial; the native smoke verifies the error, retained draft and exact persisted target/credential. No backend permission change or invented Tenant option in Instance was used.
- ResourceIdentityFields plus the real Scope form field avoid a duplicate Scope label in the existing IdentityEditor slot; no shared framework or metadata-writing API is added.

- [extension-creation-incomplete-identity-desktop-en.png](extension-creation-incomplete-identity-desktop-en.png)
- [extension-creation-connection-switch-desktop-en.png](extension-creation-connection-switch-desktop-en.png)
- [extension-creation-invalid-scoped-reference-desktop-en.png](extension-creation-invalid-scoped-reference-desktop-en.png)
- [extension-creation-retained-tabs-refresh-desktop-en.png](extension-creation-retained-tabs-refresh-desktop-en.png)
- [extension-creation-verification-desktop-en.png](extension-creation-verification-desktop-en.png)
- [extension-creation-saved-catalog-desktop-en.png](extension-creation-saved-catalog-desktop-en.png)
- [extension-creation-incomplete-identity-mobile-fr.png](extension-creation-incomplete-identity-mobile-fr.png)
- [extension-creation-connection-switch-mobile-fr.png](extension-creation-connection-switch-mobile-fr.png)
- [extension-creation-invalid-scoped-reference-mobile-fr.png](extension-creation-invalid-scoped-reference-mobile-fr.png)
- [extension-creation-retained-tabs-refresh-mobile-fr.png](extension-creation-retained-tabs-refresh-mobile-fr.png)
- [extension-creation-verification-mobile-fr.png](extension-creation-verification-mobile-fr.png)
- [extension-creation-saved-catalog-mobile-fr.png](extension-creation-saved-catalog-mobile-fr.png)
