# Model: immutable inspection and independent Provider overrides

`/models/{Name}` now uses ConsoleResourceDetailsTemplate, shared ResourceSectionState/presentations and retained ResourceDefinitionForm. Overview places four real observation/effective-specification metrics above saved Identity/labels and Configuration. Definition contains locked Identity then Configuration; Provider identity enriches Identity through AdditionalPanels. Provider Overrides and Observation history remain specialized primary sections, independent Access follows, and the final read-only YAML remains the canonical observed Model, without applied override data.

The Model has no definition-write or creation API: discovery creates it. No Model Save, activation, editable description/labels or wizard is invented. Product help remains help in Configuration, rather than appearing as a saved description. Actual publisher/model/version and generation keep their business meaning; unknown support/modalities remain neutral.

Overrides are a **separate command on the Model Provider**. Their retained common form shares the layout's state/presentation declarations but has its own EditContext and independently authorized write command. The concrete family editor extends the existing override model with read-only inspection fields so declarations reference real root properties. The observed Model form stays locked. Only the Provider command form clears the inherited presentation cascade, then enforces Provider write rights, ownership, governance, exact namespace/name/UID and its original ETag. The command updates only that exact external-model entry and preserves the other Provider properties/overrides. Parent/detail interaction rules stay in the existing editor model; localized presentation and API commands stay in the screen.

The Console adapter's HasAdditionalDraft combines this independent command draft with policy leave protection. Section changes/refresh retain invalid numeric input, parser messages, both form contexts and the old Provider ETag. Changed Model/Provider snapshots are buffered until explicit Cancel; unchanged refresh does not invent a conflict. Main-resource denial/deletion/replacement clears private state. A replaced/denied Provider removes its override form while retaining authorized observed Model data. A successful Provider write remains successful when subsequent observation fails; feedback reports the actual write without falsely claiming diagnostics were reloaded.

The specialized controls keep their schema/feature/collection/limit semantics. Family container queries adapt operation grids to their actual content-column width, and wrapping keeps French actions/badges within mobile panels. The shared/protected Agent implementation and styles are unchanged; no shared presentation component is extended.

Validation:

- **23 focused cases pass** (ModelDetailsTests and ParameterEditorModelTests): existing canonical-YAML/copy, exact-model add/remove, preservation of other Provider state, parent/detail support behavior and concurrency feedback; common read-only/independent forms, shared state/presentations, honest identity description, invalid parser/context/old-ETag retention, unchanged refresh, retry/privacy clearing, Provider UID/permission loss, a resources reader and successful-write/failed-observation handling.
- EN/FR catalogs remain symmetric (144 keys), TypeScript passes, and affected projects/three actual browser hosts build in Release without warnings/errors. Full solution suites are not run under the requested focused-validation scope.
- Final **desktop EN/dark and real-touch FR/light probes pass**: saved/locked identity, exact Provider link and UID, retained override through concurrent Provider update/Refresh/YAML, an actual stale-ETag rejection without overwriting the Provider, explicit Cancel/new snapshot adoption, successful retry preserving concurrent metadata, unchanged observed Model specification, observation history and actual Model-UID Access. Strict page bounds and internal operation-card bounds, Escape and focus return remain asserted.
- The maintained model-administration lifecycle passes in the initial campaign. Subsequent follow-ups affect only this family's responsive layout, unknown presentation and honest description/success feedback; final family probes run afterwards. Initial probe 01 used an incorrect AdditionalPanels locator. Probe 02 and visual review exposed real mobile/desktop overflow, repaired in the family stylesheet. No timeout/assertion is weakened, no CI failure is handled and no shared Agent change is made.
- [Exact final source/host hashes and validation scope](source-proof.json). Parallel list changes are preserved. Reusable transverse UX coverage remains deferred under open/blocked #1236.

Real application captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Saved overview | [Overview](model-details-overview-desktop-en.png) | [Overview](model-details-overview-mobile-fr.png) |
| Locked Identity and Provider facts | [Identity](model-details-identity-desktop-en.png) | [Identity](model-details-identity-mobile-fr.png) |
| Independent Provider command | [Override](model-details-override-desktop-en.png) | [Override](model-details-override-mobile-fr.png) |
| Retained draft after Refresh | [Retained](model-details-retained-override-desktop-en.png) | [Retained](model-details-retained-override-mobile-fr.png) |
| Canonical observed YAML | [YAML](model-details-yaml-desktop-en.png) | [YAML](model-details-yaml-mobile-fr.png) |
| Real stale-ETag refusal | [Conflict](model-details-conflict-desktop-en.png) | [Conflict](model-details-conflict-mobile-fr.png) |
| Saved Provider override, observed Model retained | [Saved](model-details-saved-overview-desktop-en.png) | [Saved](model-details-saved-overview-mobile-fr.png) |
| Observation history | [History](model-details-observation-desktop-en.png) | [History](model-details-observation-mobile-fr.png) |
| Independent Model Access | [Access](model-details-access-desktop-en.png) | [Access](model-details-access-mobile-fr.png) |
