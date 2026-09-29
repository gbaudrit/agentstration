# Architecture reconciliation baseline — 2026-09-28

This page records the first implementation-backed reconciliation of the canonical Technical Architecture Document (DAT). It is a comparison baseline, not a new architecture decision. The code reference was `main` at `894230a6`; the documentation changes were prepared on the #602 integration branch.

## Evidence inspected

- the executable and project inventory, including `Agentstration.Web`, the independent Console and Workplace hosts, `Agentstration.AppHost`, AEP extensions, and the Source Registry tool;
- `StandaloneHostComposition` and infrastructure registration for storage providers, workers, Runtime, Work, Flow, resource families, Bootstrap, identity, scheduling, Packs, Sources, and artifacts;
- family-owned HTTP endpoints, MCP authorization, SignalR hubs, the global fallback policy, and canonical request scope;
- SQLite and optional PostgreSQL storage composition;
- architecture tests and accepted ADRs relevant to the documented boundaries.

## Reconciled findings

| DAT area | Implementation-backed baseline | Correction made |
| --- | --- | --- |
| C4 L1 | Agentstration is one system used by end users, operators, and API consumers, with optional AEP, provider, and identity systems. | The shared LikeC4 model remains canonical. |
| C4 L2 | One authoritative server, independently hostable Console and Workplace shells, distinct persistent responsibilities, and autonomous AEP extensions are implemented. | Control-plane data now explicitly includes family resources, resource planning, and scheduling responsibility. |
| C4 L3 | Transport, identity, resource families, distribution, Work, Flow, Runtime, Tools, and scheduling are stable server responsibilities. | Project names remain implementation mappings rather than component identities; a focused drill-down records API host, aggregator, and family-module composition. |
| Dynamic behavior | Work submission, Flow execution, Agent execution, Tool invocation, AEP calls, MCP calls, and reconciliation correspond to implemented services and durable stores. | Dynamic views remain Mermaid scenarios linked to the shared structural model. |
| Persistence | SQLite uses separate control-plane, resource-planning, identity, scheduler, Work, Flow, and Runtime files plus bounded file-backed state. PostgreSQL is optional and preserves module ownership. | Removed the obsolete local-JSON content-store claim and documented the actual stores. |
| Scope and authorization | Canonical instance, Tenant, and Workspace scopes apply across resource, Flow, Runtime, Work, Workplace, MCP, and realtime surfaces. | Removed the obsolete claim that those verticals were outside the authorization boundary. |
| Deployment | Direct startup is the all-in-one default; Bootstrap is profile-controlled; independent shells, Aspire, PostgreSQL, providers, and AEP extensions are optional. | Removed the claim that startup always seeds Management resources. |
| Implementation map | Current projects use plural `Agentstration.Flows.*` names and family-owned Identity, Extensions, Packs, Sources, Parameters, and Resource Planning modules. | Corrected obsolete project names and transitional ownership text. |

## Decision-history interpretation

[ADR-0002](../../decisions/0002-storage-profiles.md) records the early local-JSON default and PostgreSQL target. It remains historical decision evidence; the current storage baseline is established by the implemented SQLite boundaries and later [ADR-0007](../../decisions/0007-sqlite-control-plane.md) and [ADR-0078](../../decisions/0078-postgresql-is-an-optional-server-storage-profile.md). Reconciliation does not rewrite an accepted ADR to conceal that evolution.

## Resolved drift

- [#612](https://github.com/gbaudrit/agentstration/issues/612) resolved the duplicate ADR-0117, ADR-0118, ADR-0119, and ADR-0122 identifiers by retaining the earlier decision numbers and renumbering the later Resource Planning and Assistant decisions as ADR-0144 through ADR-0147. The ADR catalog and inbound references are now unambiguous. The current documentation toolchain has no redirect facility, so the old colliding file paths cannot be retained as aliases; the issue records the explicit old-to-new mapping.
- No other unresolved contradiction was found in the canonical L1, L2, L3, dynamic, persistence, deployment, or authorization pages during this reconciliation.

## Validation baseline

- LikeC4 validation passed for all three shared model files.
- The complete Docusaurus production build succeeded. Its non-blocking cache snapshot and local update-check warnings do not affect the generated site.
- `Agentstration.ArchitectureTests` passed: 84 total, 84 succeeded, 0 failed, 0 skipped.
- Browser smoke testing confirmed interactive L1 → L2 → L3 → API-composition navigation, including the reconciled Control-plane data element and all 17 family API modules, and rendered this baseline page from the generated site.

Future reconciliations should compare their evidence and corrections with this page, update the date through a new baseline when the architecture materially changes, and keep unresolved items linked to their governing issue or ADR.
