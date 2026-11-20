# Secret: validated Console resource foundation

Secret uses the standard Console resource template, saved identity with labels, actual value-status metric above Identity, retained common definition form and one shared section state/presentation dictionary. Definition has Identity, Configuration (exact qualified Vault reference/key) and family descendant-use policy. Authorized resource Access remains independent; final YAML exports only saved metadata. Creation keeps its existing wizard, automatic name/key derivation and family-authorized scope/Vault choices.

The secret value remains a separate write-only command. A minimal optional `HasAdditionalDraft` extends the Console adapter's existing leave guard without enabling the definition save bar. Switching sections, refreshing, saving or discarding metadata retains a pending value. Its own Cancel/Save controls clear or persist it. No stored value is retrieved; neither pending nor stored values enter saved Overview or YAML. Replacement identity or loss of access clears private pending content. Only public disposable fixture strings were used in local checks; captures mask their inputs.

Definition refresh retains the same EditContext, invalid values/messages and original ETag. A newer server snapshot is buffered until explicit discard. Family rules, metadata/value commands, usage restrictions, confirmations and exact UID/owner/reference/ETag behavior remain separate. Namespace stays immutable; metadata labels remain inside Identity but read-only because the existing properties-only API cannot write them. No activation capability is invented.

Verification:

- Affected Release component/tests and actual Web/Workplace hosts build without warnings/errors; all host builds finish before the browser campaign.
- Targeted component/model checks cover name/key derivation, grants, managed read-only and independent Access, metadata/value draft separation, invalid draft refresh, both original/newer ETag paths and replacement-resource clearing. All 60 targeted cases pass.
- TypeScript passes. Both desktop English/dark and French/light genuine-touch probes pass creation, hidden-error routing/focus, invalid key retention, concurrent and value-only refresh, explicit metadata discard/save with pending value intact, exact UID/owner writes, separate value persistence and read-only YAML without value contents. Every capture follows a strict width check; tactile navigation also verifies Escape and focus return.
- The maintained descendant Vault/Secret grants journey and both maintained Agent creation/deployment desktop/touch cases pass. Agent's protected product files remain byte-identical.
- [Browser source and actual-host hashes](source-proof.json). A final test-only data row additionally checks saving with the original ETag before explicit discard; it does not change product/browser sources. No CI failures are investigated. New reusable scenarios retain the #1234/#1236 UX-validation/merge gate; these are transient local probes and maintained regressions.

| Surface | Desktop English/dark | French/light touch |
| --- | --- | --- |
| Saved Overview | [Capture](secret-overview-desktop-en.png) | [Capture](secret-overview-mobile-fr.png) |
| Invalid Vault key | [Capture](secret-invalid-key-desktop-en.png) | [Capture](secret-invalid-key-mobile-fr.png) |
| Retained concurrent refresh | [Capture](secret-retained-refresh-desktop-en.png) | [Capture](secret-retained-refresh-mobile-fr.png) |
| Independent write-only draft | [Capture](secret-write-only-draft-desktop-en.png) | [Capture](secret-write-only-draft-mobile-fr.png) |
| Saved YAML | [Capture](secret-yaml-desktop-en.png) | [Capture](secret-yaml-mobile-fr.png) |

## Confirmed-deletion follow-up

Secret invalidates the deleted snapshot and drafts before its post-render return to the exact collection path. [Focused regression and actual desktop/touch evidence](../deletion-followup/README.md).
