# ADR-0251 Bootstrap versions, scoped history and application provenance

Status: Proposed — 2026-11-22

Bootstrap fingerprints profile files and validates the expected digest during application. Durable manual progress from ADR-0246 and exact targets from ADR-0248 provide the execution identity and target inputs for versioned history.

A validated application registers an immutable Bootstrap profile version before applying resources. Manifests declare an `X.Y.Z` version. A legacy descriptor without a version has effective version `1.0.0`. A registered version can be reused when its content digest matches. Changed content requires a new version. Local versions retain an archive of the files protected by the instance Data Protection keys. Source-backed versions retain the pinned Source and Channel Snapshot. Pack installation reads the registered local archive or the materialized pinned Source content. Archive bodies are private storage and excluded from Console and history responses.

Application history is stored at the exact Instance, Tenant, or Workspace target. Manual and startup executions share the history shape. Records from a multi-target execution have a shared correlation identifier. The current Phase/Application progress surfaces still manage their respective lifecycle and cancellation.

ADR-0181 defines `Origin` and `ManagedBy`. Bootstrap application attribution lives in `AppliedBy` with the profile version, scoped application and manifest location. Bootstrap assigns it through a trusted context at creation, including during a Pack installation. Updates and skipped resources retain the original application. The Console reuses the resource Identity slot and keeps history access under the Platform administrator policy.
