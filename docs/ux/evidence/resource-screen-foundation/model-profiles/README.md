# Model Profile: retained definition and wizard creation

`/modelprofiles/{Name}` adopts ConsoleResourceDetailsTemplate and ResourceDefinitionForm with the same section state and presentation dictionary. Overview places actual resolution and authorized Agent-usage metrics above saved Identity/labels and the common Configuration collection. Definition contains Identity, Configuration (Provider/model), Generation and Provider options; Usages and effective Resolution remain family sections, followed by independently authorized Access and final saved read-only YAML. No activation property is invented.

Creation keeps its own page and uses a five-stage CreationWizard. Clicking stages or Continue permits incomplete input; stage completion uses existing annotations/ranges, JSON parsing and guided-option validation. Only final submission validates and directs focus to the first visible error. Identity reuses standard fields and authorized scope targets. Model Profile supports Instance/Tenant/Workspace; Provider dependencies load from its owner's visibility while profile/usage/resolution requests retain the administrative viewing context.

Names, namespaces and owners remain exact. An unchanged Provider reference retains its entire original value, including relative namespace/scope semantics; explicitly choosing another Provider creates the same Instance-qualified reference as before. Saved namespace/owner remain immutable. The family API writes properties only, so labels stay read-only within Identity; description remains editable. Provider/model choices retain unavailable saved identities rather than inventing replacements.

Authorized refresh retains visited invalid JSON/numeric controls, their EditContext, guided/raw mode, original ETag and a newer server snapshot for explicit Discard. An unchanged snapshot does not create a warning. Main-resource denial, deletion or changed UID/address/owner clears private state; late/disposed responses are ignored/cancelled. Recoverable catalog/resolution errors retain known observations with feedback; denied auxiliary data is removed without resurrecting it from the discard baseline. Policy drafts retain the Console adapter's separate guard and permissions. Diagnosis remains a read command independent of definition writing.

Guided Provider options retain their actual published version, schema digest, values and unrelated envelope data. Invalid structured input stays local through section changes/refresh. Switching to raw JSON first serializes valid guided edits; failed conversion retains them. Existing parser rules stay in the model. ArgumentException.ParamName identifies an invalid option for localized, field-specific UI feedback and common first-error focus, without replacing the existing errors or changing parsing. Unknown/unrepresentable contracts require explicit raw editing. Enum and validation presentation is complete in English/French. The switch uses existing standard controls/theme styles, with a local alignment rule for the legacy form grid.

Usage search and cursor pagination keep the existing bounded client contract. Refresh retains requested filters/cursors; failure keeps accepted rows/page facts and offers retry, while permission loss clears them. Known counts describe authorized references only, unknown stays unknown. A fresh known zero clears a previous usage deletion restriction; otherwise the original command/backend restrictions and confirmations remain authoritative. Save/Delete preserve exact coordinates, original ETags and read-only/write permissions; successful deletion clears the resource before leaving.

Validation:

- **137 focused component cases pass**, including the maintained Model Provider/Profile navigation cases, guided-option parser tests and Agent resource-template regressions. Profile-specific checks cover shared state/presentation instances, incomplete wizard navigation, actual annotation ranges, invalid schema/context/original-ETag retention, explicit Discard, relative Provider-reference preservation, denied/deleted/replaced-resource clearing, refreshed zero usage count, and invalid guided input/pinned contract retention.
- TypeScript passes. The affected component/test projects and all three real browser hosts build in Release with zero warnings/errors. Full solution suites are omitted under the maintainer's focused-validation instruction.
- **Five final browser cases pass**: desktop EN/dark and actual-touch FR/light family probes, the maintained model-administration lifecycle, and both maintained inherited-resource desktop/mobile cases. They assert first-error focus, all five creation stages, long identities, shared metrics/labels, actual concurrent API update/stale-ETag refusal without overwrite, explicit Discard/new version adoption, successful save, exact-UID Access, saved read-only YAML, usages/resolution, document bounds, switch alignment, Escape and focus return. The inherited journey verifies owner draft commands and actually opens authorized Access through the mobile navigation.
- **30 real application captures** are linked below. Initial family probes expected the technical-name field even though the display-name field is first; that locator was corrected. The initial inherited mobile check expected a hidden rail entry to be visible; it now opens the Access section and verifies its actual panel. Visual review corrected this family's switch alignment. No timeout/assertion was weakened and no CI failure was handled.
- [Exact tested base, source/host hashes and validation scope](source-proof.json). No CI failure is inspected or repaired. The six protected Agent source/style/catalog files are unchanged. The existing model-administration and inherited-resource journeys are maintained; new reusable transverse coverage remains deferred under open/blocked #1236.

Real application captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Incomplete creation and error focus | [Desktop](model-profile-wizard-invalid-desktop-en.png) | [Touch](model-profile-wizard-invalid-mobile-fr.png) |
| Creation Identity | [Desktop](model-profile-wizard-identity-desktop-en.png) | [Touch](model-profile-wizard-identity-mobile-fr.png) |
| Creation Provider/model | [Desktop](model-profile-wizard-model-desktop-en.png) | [Touch](model-profile-wizard-model-mobile-fr.png) |
| Creation Generation/reasoning | [Desktop](model-profile-wizard-generation-desktop-en.png) | [Touch](model-profile-wizard-generation-mobile-fr.png) |
| Creation Provider options | [Desktop](model-profile-wizard-options-desktop-en.png) | [Touch](model-profile-wizard-options-mobile-fr.png) |
| Creation Review | [Desktop](model-profile-wizard-review-desktop-en.png) | [Touch](model-profile-wizard-review-mobile-fr.png) |
| Saved Overview | [Desktop](model-profile-overview-desktop-en.png) | [Touch](model-profile-overview-mobile-fr.png) |
| Invalid schema | [Desktop](model-profile-invalid-schema-desktop-en.png) | [Touch](model-profile-invalid-schema-mobile-fr.png) |
| Retained draft after Refresh | [Desktop](model-profile-retained-refresh-desktop-en.png) | [Touch](model-profile-retained-refresh-mobile-fr.png) |
| Saved YAML | [Desktop](model-profile-yaml-desktop-en.png) | [Touch](model-profile-yaml-mobile-fr.png) |
| Real stale-ETag rejection | [Desktop](model-profile-etag-conflict-desktop-en.png) | [Touch](model-profile-etag-conflict-mobile-fr.png) |
| Saved Identity | [Desktop](model-profile-saved-identity-desktop-en.png) | [Touch](model-profile-saved-identity-mobile-fr.png) |
| Authorized usages | [Desktop](model-profile-usages-desktop-en.png) | [Touch](model-profile-usages-mobile-fr.png) |
| Resolution diagnosis | [Desktop](model-profile-resolution-desktop-en.png) | [Touch](model-profile-resolution-mobile-fr.png) |
| Independent Access | [Desktop](model-profile-access-desktop-en.png) | [Touch](model-profile-access-mobile-fr.png) |
