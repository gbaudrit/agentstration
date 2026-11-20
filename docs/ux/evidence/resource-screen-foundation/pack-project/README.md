# Pack Project details — saved resource, authoring sources and immutable builds

The owning `/pack-projects/{uid}` screen uses the common Console template. Overview shows the real project revision, Resource generation and known build/source counts before saved Identity/tags and a full wrapping Configuration summary. The server-generated Resource technical name is distinct from the immutable publisher/Pack coordinate. Definition shares one section state and the exact presentation dictionary with ResourceDefinitionForm and PackProjectEditorModel. Identity metadata labels remain read-only because this properties-only update API cannot write them; manifest tags/categories retain their independent business meaning. Source provenance, immutable build history, installation, independently authorized Access and final read-only public YAML retain their family contracts. YAML omits the internal artifact storage locator and explicitly describes that public projection.

Per-path source editors retain visited raw and specialized Agent form drafts independently. Switching views or closing an editor does not discard them; transfer/replacement is explicit, conflicting view drafts require confirmation, and rejected writes retain both. The Pack-specific Agent form preserves unchanged scoped and extended reference objects rather than reconstructing them from display text. It preserves binding expressions and unknown source fields. Its opt-in input tracking leaves existing consumers' default commit policy intact. This is a family helper, not the protected Agent resource screen.

Main refresh retains the accepted model/EditContext, invalid values, source buffers and original ETags. Newer saved content is buffered until explicit discard. A source acknowledgement advances the accepted Project envelope without resetting pending metadata or another source; metadata acknowledgement likewise preserves source drafts. Main read denial/not-found/replaced identity clears private retained content. Context/request guards reject stale completions. Failed auxiliary history has an unknown count, never a manufactured zero.

Installation has an independent leave guard and explicit discard, remembers choices for each immutable build, uses exact available scope/namespace/name references, retains unavailable saved references without silently selecting a homonym and never fetches Secret contents. Its permission is independent of definition management/inheritance. The existing explicit replacement intent uses the standard visual switch. A failed preview cannot enable installation. Build/preview/download and source editing keep existing authorized commands and boundaries; resource loading stays in this screen.

**38 focused component cases pass**, including EN/FR rendering and field/state composition, original ETags, invalid metadata, separate source/metadata acknowledgement, raw/form drafts and confirmation, recoverable and denied refresh, replaced identity, unknown history and managed-definition versus installation permissions. TypeScript, symmetric duplicate-free EN/FR Pack catalogs and all three Release hosts pass. Native English/dark desktop and French/light true-touch smoke passes, including first-error focus, incomplete metadata across sections/refresh, retained invalid source, explicit discard, real source and metadata saves verified through the API, safe read-only YAML, a real immutable build and its action menu with Escape/focus return. Existing composition/build smoke passes, as do both protected Agent creation/deployment desktop/mobile cases. These 16 PNGs are unmodified application captures; exact tested base/source/host hashes and regression report hashes are recorded in verification.json.

Existing scope-policy/ADR drift recorded under PackComposer remains unchanged: this adapter displays actual authenticated Resource ownership and request scope, without a new ownership-selector API or backend capability claim. The creation wizard remains the separately reconciled PackComposer. A new duplicate Pack creation/build journey is **Not applicable**: the existing composition/build journey remains maintained; transverse #1236 stays deferred until UX validation and merge. No CI failures investigated or repaired; full solution lanes omitted at the maintainer's request.


- [pack-project-overview-desktop-en.png](pack-project-overview-desktop-en.png)
- [pack-project-identity-desktop-en.png](pack-project-identity-desktop-en.png)
- [pack-project-invalid-version-desktop-en.png](pack-project-invalid-version-desktop-en.png)
- [pack-project-source-draft-desktop-en.png](pack-project-source-draft-desktop-en.png)
- [pack-project-source-saved-desktop-en.png](pack-project-source-saved-desktop-en.png)
- [pack-project-yaml-desktop-en.png](pack-project-yaml-desktop-en.png)
- [pack-project-installation-desktop-en.png](pack-project-installation-desktop-en.png)
- [pack-project-build-history-desktop-en.png](pack-project-build-history-desktop-en.png)
- [pack-project-overview-mobile-fr.png](pack-project-overview-mobile-fr.png)
- [pack-project-identity-mobile-fr.png](pack-project-identity-mobile-fr.png)
- [pack-project-invalid-version-mobile-fr.png](pack-project-invalid-version-mobile-fr.png)
- [pack-project-source-draft-mobile-fr.png](pack-project-source-draft-mobile-fr.png)
- [pack-project-source-saved-mobile-fr.png](pack-project-source-saved-mobile-fr.png)
- [pack-project-yaml-mobile-fr.png](pack-project-yaml-mobile-fr.png)
- [pack-project-installation-mobile-fr.png](pack-project-installation-mobile-fr.png)
- [pack-project-build-history-mobile-fr.png](pack-project-build-history-mobile-fr.png)
