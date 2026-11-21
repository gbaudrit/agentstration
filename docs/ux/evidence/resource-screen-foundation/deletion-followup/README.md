# Confirmed-deletion lifecycle follow-up

A successful resource deletion must invalidate the saved resource and its definition, YAML, policy and independent value drafts before returning to the collection. The six migrated family adapters now defer navigation until the invalidated snapshot has rendered. No timer, domain command or authorization change is involved. Failed deletion retains existing drafts and feedback.

- 74 focused component cases pass across Parameter, Vault/Secret, Runtime Profile, Source Provider and Tool Category, including a pending-policy deletion regression for each applicable adapter and Secret’s independent pending replacement.
- Real application: English dark desktop and French light touch both pass. Each journey deletes Parameter, Secret, Vault, Runtime Profile and Source Provider with an unsaved access-policy draft; Secret also has a pending write-only replacement. Each asserts the exact collection URL, no lingering guard/dialog and API 404. Category’s separate final desktop/touch probes already exercise its deletion; its component regression now asserts the exact collection path.
- [Source/host hashes](source-proof.json) identify the six-adapter working tree tested together before separate per-screen follow-up commits. [Browser result](browser.log).
- Initial fixture-only attempt failed before deletion because the transient probe supplied a string for a numeric enum. Corrected to the actual JSON contract; production validation and assertions were retained.
- Release component, test and executable-host builds completed without warnings or errors. Protected Agent files remain unchanged. No CI failure was inspected.

These transient probes maintain regression evidence; deferred reusable UX composition stays tracked by #1236, open and blocked pending UX validation and merge.
