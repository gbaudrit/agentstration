# Data Source and Knowledge reconciliation — 2026-10-08

This scoped reconciliation compares the implementation delivered through the #624 integration branch with the shared DAT, LikeC4 model, operator guidance, and accepted ADRs. The code baseline is merge commit `177a35ac`, which includes the Data Source, Artifact capture, Flow-contract, Knowledge projection, and final composition changes from #733 through #740. This page records evidence and remaining drift; it does not introduce a new architecture decision.

## Evidence inspected

- Data Source contracts, profile publication, scope resolution, acquisition service, REST module, Bootstrap and Pack handlers;
- Knowledge Source bindings, projection, immutable Snapshot publication, retrieval, source-specific Tool exposure, REST module, and Console composition;
- Artifact staging, Storage Flow receipts, Flow `artifactOutput`, built-in Workspace reconciliation, and local filesystem stores;
- Crawl4AI AEP contributions and its `crawl4ai-web` Data Source Profile bundle;
- the `agentstration-documentation` Bootstrap profile;
- the shared LikeC4 model, DAT Markdown and Mermaid dynamic views;
- ADR-0148 through ADR-0166, with particular attention to the explicit supersession statements in ADR-0165 and ADR-0166.

## Reconciled model

| Concern | Implemented authority | Documentation correction |
| --- | --- | --- |
| Distribution Source | Versioned resource and Pack content distribution. | Kept distinct from origins acquired for Knowledge. |
| Data Source | Origin identity, configuration, enabled state, and profile reference at instance, tenant, or Workspace scope. | Documented as the only governed acquisition boundary. |
| Data Source Profile | Editable acquisition policy with immutable published revisions, active revision, schema, Flow, Tool/provider bindings, limits, and policies. | Replaced obsolete Knowledge Source Profile reference guidance. |
| Acquisition | Workspace execution of `datasource.acquisition/v1`, with exact source, profile, Flow, Tool, provider, policy, and Artifact evidence. | Separated acquisition from Knowledge projection and retrieval. |
| Knowledge Source | Workspace projection over one to thirty-two visible Data Sources. | Documented optional per-binding `artifact.transform/v1`, one projection Flow, and one retrieval Flow. |
| Projection and Snapshot | One `knowledge.projection/v1` Run over all prepared inputs; every success publishes and activates a distinct immutable Snapshot. | Clarified identity aggregation, heterogeneous Snapshots, failure behavior, and exact provenance. |
| Retrieval | Search, query, and bounded read through `knowledge.retrieval/v1`, restricted to the selected Snapshot. | Clarified local deterministic behavior and source-specific Tool exposure through the existing Tool pipeline and internal MCP provider. |
| Flow classification | One Flow-owned `flow.contract` key; families own their contract values. | Removed the obsolete family-specific metadata-key model from current guidance. |
| Extension bundles | AEP contributes declarative ToolSet, Flow, and Data Source Profile documents; the host installs them through ordinary handlers. | Documented explicit installation and kept automatic enrollment installation in independent #696. |

## Decision-history interpretation

[ADR-0165](../../decisions/0165-flow-contracts-use-one-flow-owned-metadata-key.md) explicitly replaces the earlier family-specific metadata keys and the pre-release `data.source.acquisition/v1` spelling. [ADR-0166](../../decisions/0166-knowledge-sources-project-data-source-artifacts.md) explicitly supersedes Knowledge-owned acquisition, Knowledge Source Profiles, `knowledge.ingestion/v1`, and ADR-0163's temporary compatibility path. The repository had not shipped those resources, so the current model has no migration or historical-record rewrite requirement.

The earlier ADR files remain unchanged as historical evidence. Current guidance follows ADR-0165 and ADR-0166 rather than presenting the superseded paths as supported behavior.

## Remaining discrepancies and independent work

- ADR-0155 still describes a built-in `knowledge.ingestion/v1` Flow. ADR-0166 supersedes that behavior but does not name ADR-0155 in its supersession list. The implementation provisions `datasource.acquisition/v1` Flows and `knowledge-projection-builtin`; a future decision-history cleanup should make this supersession explicit without rewriting ADR-0155.
- The dormant pre-release `KnowledgeSourceProfile`, Knowledge acquisition, and ingestion-oriented Snapshot remnants have been removed from contracts, services, adapters, Console surfaces, and composition. The supported model now starts with Data Source acquisition and continues with Knowledge projection, immutable snapshots, retrieval, and governed Tool exposure.
- Automatic installation of an extension-contributed Data Source Profile bundle during AEP enrollment is not implemented. [#696](https://github.com/gbaudrit/agentstration/issues/696) owns that independent capability and requires installation at the extension's scope.
- MCP-aligned normalization of Tool output `content` and `structuredContent` remains independent in [#737](https://github.com/gbaudrit/agentstration/issues/737).
- Reusable browser UX scenarios are intentionally independent of #624 so they can be authored after the planned graphical evolution. This does not defer existing regression tests or offline functional validation.

## Validation record

- LikeC4 validation passed for all three shared model files.
- The complete Docusaurus production build succeeded, including ADR validation, LikeC4 Web Component generation, internal-link checks, and Mermaid compilation.
- `Agentstration.ArchitectureTests` passed: 86 total, 86 succeeded, 0 failed, 0 skipped.
- Optional live Crawl4AI, AEP enrollment, remote-model, external-provider, and browser checks were not run because this change updates documentation only and those integrations remain opt-in.
