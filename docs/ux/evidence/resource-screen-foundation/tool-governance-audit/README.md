# Tool governance audit: operational collection review

This route presents `ToolGovernanceAuditRecord` events for a Runtime Run or Flow Run. Its record/page contracts expose sequence, invocation identifiers, optional retained arguments and hook evaluations; they are not persisted `Resource` definitions. The execution owner is business context, not an I/T/W resource proprietor. The specialized audit collection and its existing shared operational shell are retained. Adding a fabricated definition, labels, YAML, creation wizard or resource policy would misrepresent this contract. Exact execution/scope links, deep filters, the bounded 50-record request and real continuation sequence remain intact.

A discovered privacy regression is corrected: 401/403/404 during continuation clear retained records, invocation arguments and continuation. Recoverable continuation failure retains the rows and retries the same cursor. Disposed/superseded reads cannot replace the current audit.

Validation: all ten targeted component cases pass, including three previously failing access-loss cases, recoverable retry, deep filters, exact sequence and superseded-read checks. Release components/tests and actual Web/Workplace hosts build with zero warnings/errors. English/dark desktop and French/light native-touch smoke pass operational navigation, invalid-generation filters retained across fragments, real missing-run feedback, strict document-width checks and touch Escape/focus return. These two smoke probes use a genuinely missing local execution; populated-record privacy and retry are verified by component tests, not claimed as browser-tested. No audit events or resource identities are fabricated in production. [Exact source and host hashes](source-proof.json).

- [Desktop application capture](audit-desktop-en.png)
- [Touch application capture](audit-mobile-fr.png)

Final synchronization preserves the separate Tool Provider list migration and two list-only translation keys. Audit source bytes match the browser evidence; no shared product component changed in that delta. [Synchronization scope](final-synchronization.json).

No shared component or protected Agent file changes. No CI failure was inspected. New reusable scenario coverage remains tracked by open/blocked #1236 pending UX validation and merge of #1238.
