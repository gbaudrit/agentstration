# Connected services Actions and Preview smoke

The source hashes in [observations.json](observations.json) identify the tested product files. Four variants passed: desktop 1440 × 1000 / mobile 390 × 844, English/light and French/dark on desktop, English/dark and French/light on mobile. Data is synthetic and disposable. No model invocation, reconciliation or resource deletion was performed.

Checks: exact selected row, keyboard menu navigation and Escape/focus, mobile touch, saved Summary, independent disclosure, hidden configuration selector, document/panel width bounds and reachable close, Open/Edit followed by Back, reload/direct links, Escape/explicit close and native fragment reopening. No unhandled browser errors occurred. Component tests separately cover scoped query boundaries, cancellation, mismatch rejection and independent quick actions. Full-detail diagnostics remain family-owned.

New reusable scenarios remain deferred in #1258 pending maintainer UX validation and merge; these smoke observations do not complete that child.

- [connected-service-en-US-light-1440](connected-service-en-US-light-1440.png)
- [connected-service-fr-FR-dark-1440](connected-service-fr-FR-dark-1440.png)
- [connected-service-en-US-dark-390](connected-service-en-US-dark-390.png)
- [connected-service-fr-FR-light-390](connected-service-fr-FR-light-390.png)
