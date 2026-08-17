# Architecture Decision Records

- [ADR-0031: Agentstration-native declarative resource envelope](0031-agentstration-native-resource-envelope.md)

ADRs record durable architectural choices and their consequences. Existing decisions are numbered in chronological order and remain in Git when superseded.

## Format

```markdown
# ADR-XXXX: Title

## Status

Proposed | Accepted | Deprecated | Superseded

## Context

...

## Decision

...

## Consequences

...
```

Use **Proposed** when implementation or repository evidence does not establish a settled choice. Never rewrite an accepted ADR to conceal a later decision; add a new ADR and mark the old one superseded.

## Catalog

1. [ADR-0001 — Start with a modular monolith](0001-modular-monolith.md)
2. [ADR-0002 — Local JSON default, PostgreSQL target](0002-storage-profiles.md)
3. [ADR-0003 — Microsoft.Extensions.AI boundary](0003-ai-boundaries.md)
4. [ADR-0004 — Standalone scheduler before Quartz](0004-standalone-scheduler.md)
5. [ADR-0005 — REST, UI, and MCP share application services](0005-shared-application-services.md)
6. [ADR-0006 — Agentstration is the independent Management Plane](0006-independent-management-plane.md)
7. [ADR-0007 — SQLite control-plane storage for standalone mode](0007-sqlite-control-plane.md)
8. [ADR-0008 — Reconstructible Microsoft Agent Framework runtime](0008-reconstructible-maf-runtime.md)
9. [ADR-0009 — Independent Work Plane with local Runtime dispatch](0009-independent-work-plane.md)
10. [ADR-0010 — Independent Flow definition module](0010-independent-flow-module.md)
11. [ADR-0011 — Dedicated Management module](0011-dedicated-management-module.md)
12. [ADR-0012 — Runtime Run resource and observable execution](0012-runtime-run-resource.md)
13. [ADR-0013 — Model-provider boundary and local Ollama adapter](0013-model-provider-boundary-and-ollama.md)
14. [ADR-0014 — Configuration-backed model resolution into MAF](0014-configuration-backed-model-resolution.md)
15. [ADR-0015 — Persisted model profiles and provider APIs](0015-model-management-apis.md)
16. [ADR-0016 — Real model invocation from Agent Runner](0016-real-agent-runner-model-invocation.md)
17. [ADR-0017 — Canonical runtime/model options and capabilities](0017-canonical-runtime-model-options-and-capabilities.md)
18. [ADR-0018 — Persisted model-provider declarations and dynamic clients](0018-persisted-model-provider-crud.md)
19. [ADR-0019 — Flow-owned Run resource and execution Console](0019-flow-run-resource-and-console.md)
20. [ADR-0020 — Workplace Entry, Interaction, and Task vertical](0020-workplace-entry-interaction-task-vertical.md)
21. [ADR-0021 — Standalone Workplace and Work API hosts](0021-standalone-workplace-and-work-api.md)
22. [ADR-0022 — Interaction as durable conversation and FlowRun continuation](0022-interaction-durable-conversation-flow-continuation.md)
23. [ADR-0023 — Console supervision of WorkTasks through Work API](0023-console-supervises-worktasks-through-work-api.md)
24. [ADR-0024 — Entries always target executable Flows](0024-entries-always-target-executable-flows.md)
25. [ADR-0025 — Tenant, workspace, and identity foundation](0025-tenant-workspace-identity-foundation.md)
26. [ADR-0026 — Out-of-process model-provider extensions through AEP](0026-out-of-process-aep-model-provider-extensions.md)
27. [ADR-0027 — AEP tool contributions resolve to MCP](0027-aep-tool-contributions-resolve-to-mcp.md)
28. [ADR-0028 — Tool Providers materialize a governed catalog](0028-tool-providers-governed-catalog.md)
29. [ADR-0029 — Aspire consumes an existing local Ollama installation](0029-aspire-consumes-local-ollama.md)
30. [ADR-0030 — AEP is an autonomous SDK and Inspector repository](0030-autonomous-aep-repository.md)
31. [ADR-0031 — Agentstration-native declarative resource envelope](0031-agentstration-native-resource-envelope.md)
32. [ADR-0032 — Use one authoritative standalone server](0032-single-authoritative-standalone-server.md)
33. [ADR-0033 — Canonical resource names and explicit execution identities](0033-canonical-names-and-execution-identities.md)
34. [ADR-0034 — Seal MAF Flow orchestration behind the runtime adapter](0034-seal-maf-flow-orchestration-behind-runtime-adapter.md)
35. [ADR-0035 — Resource names are scoped by explicit namespaces](0035-resource-namespaces.md)
36. [ADR-0036 — Runtime resolution and control-plane hardening](0036-runtime-resolution-and-control-plane-hardening.md)
37. [ADR-0037 — Packs are Management and distribution artifacts](0037-packs-are-management-distribution-artifacts.md)
38. [ADR-0038 — Pack Projects retain sources and produce local immutable builds](0038-pack-projects-and-local-builds.md)
39. [ADR-0039 — Pack manifests use the native definition envelope](0039-pack-manifests-use-the-native-definition-envelope.md)
40. [ADR-0040 — Secrets and Vaults V1](0040-secrets-and-vaults-v1.md)
41. [ADR-0041 — Pack resource bindings are logical and installation-scoped](0041-pack-resource-bindings.md)
42. [ADR-0042 — Authentication and authorization boundaries](0042-authentication-and-authorization-boundaries.md)
43. [ADR-0043 — Console API calls propagate only an explicitly trusted Web session](0043-console-api-session-propagation.md)
44. [ADR-0044 — Identity schema and Web key material are durable](0044-durable-identity-schema-and-data-protection.md)
45. [ADR-0045 — Security events are an append-only Management log](0045-security-events-are-an-append-only-management-log.md)
46. [ADR-0046 — Platform administration is explicitly transferable](0046-platform-administration-is-explicitly-transferable.md)
47. [ADR-0047 — External identities are explicitly linked to Principals](0047-external-identities-are-explicitly-linked-to-principals.md)
48. [ADR-0048 — FlowRuns carry a durable execution scope](0048-flow-runs-carry-a-durable-execution-scope.md)
49. [ADR-0049 — Workplace Dashboards own Entry composition](0049-workplace-dashboards-own-entry-composition.md)
50. [ADR-0050 — Background Control Plane access is explicit](0050-background-control-plane-access-is-explicit.md)
51. [ADR-0051 — Pack Projects can originate from reviewed workspace snapshots](0051-pack-projects-from-workspace-snapshots.md)
52. [ADR-0052 — Pack composition distinguishes contained model configuration from bindings](0052-pack-composition-distinguishes-contained-model-configuration-from-bindings.md)
53. [ADR-0053 — Workspace scope is part of durable identity](0053-workspace-scope-is-part-of-durable-identity.md)
