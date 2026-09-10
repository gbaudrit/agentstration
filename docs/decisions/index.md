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
54. [ADR-0054 — Durable interactive Flow execution preserves exact runtime identity](0054-durable-interactive-flow-execution.md)
55. [ADR-0055 — Agentstration owns the Tool execution boundary](0055-agentstration-owns-tool-execution-boundary.md)
56. [ADR-0056 — Tool execution hooks are ordered Runtime guards](0056-tool-execution-hooks-are-ordered-runtime-guards.md)
57. [ADR-0057 — Tool execution Hook resources select built-in Runtime handlers](0057-tool-execution-hook-resources-select-built-in-runtime-handlers.md)
58. [ADR-0058 — Tool governance decisions are traced per physical attempt](0058-tool-governance-decisions-are-traced-per-physical-attempt.md)
59. [ADR-0059 — Tool arguments require explicit bounded retention](0059-tool-arguments-require-explicit-bounded-retention.md)
60. [ADR-0060 — Entry owns Workplace execution presentation](0060-entry-owns-workplace-execution-presentation.md)
61. [ADR-0061 — llama.cpp is an AEP provider and capabilities are resolved effectively](0061-llama-cpp-provider-and-effective-capabilities.md)
62. [ADR-0062 — Extension options use immutable versioned contracts](0062-versioned-extension-option-contracts.md)
63. [ADR-0063 — Extension registrations are managed discovery sources](0063-extension-registrations-are-managed-discovery-sources.md)
64. [ADR-0064 — Extension option migrations are explicit](0064-extension-option-migrations-are-explicit.md)
65. [ADR-0065 — Model Providers bind registered extension contributions](0065-model-providers-bind-extension-contributions.md)
66. [ADR-0066 — Pack Runtime Profile bindings drive local deployment](0066-pack-runtime-profile-bindings-drive-local-deployment.md)
67. [ADR-0067 — LocalAI is an independent AEP provider](0067-localai-is-an-independent-aep-provider.md)
68. [ADR-0068 — Triggers submit Work through a reconstructible Quartz projection](0068-triggers-submit-work-through-quartz-projection.md)
69. [ADR-0069 — Built-in resources have explicit provenance](0069-built-in-resources-have-explicit-provenance.md)
70. [ADR-0070 — Personal access tokens are revocable Workspace delegations](0070-personal-access-tokens-are-revocable-workspace-delegations.md)
71. [ADR-0071 — Remove the legacy content and mission vertical](0071-remove-legacy-content-vertical.md)
72. [ADR-0072 — Pack updates reconcile stable resources and preserve Work history](0072-pack-updates-reconcile-stable-resources.md)
73. [ADR-0073 — Bootstrap is a declarative initial-state source](0073-bootstrap-is-a-declarative-initial-state-source.md)
74. [ADR-0074 — Initial topology is declarative and Platform administration is global](0074-initial-topology-is-declarative-and-platform-administration-is-global.md)
75. [ADR-0075 — Bootstrap files are an ordered profile catalog](0075-bootstrap-files-are-an-ordered-profile-catalog.md)
76. [ADR-0076 — UI localization uses RESX and Principal culture preferences](0076-ui-localization-uses-resx-and-principal-culture-preferences.md)
77. [ADR-0077 — Bootstrap profiles are explicit administrative applications](0077-bootstrap-profiles-are-explicit-administrative-applications.md)
78. [ADR-0078 — PostgreSQL is an optional server storage profile](0078-postgresql-is-an-optional-server-storage-profile.md)
79. [ADR-0079 — Management resources use explicit hierarchical scopes](0079-management-resources-have-explicit-ownership-scopes.md)
80. [ADR-0080 — Management resource kinds have an initial scope policy](0080-management-resource-kinds-have-an-initial-scope-policy.md)
81. [ADR-0081 — Source Providers are bounded AEP contributions](0081-source-providers-are-bounded-aep-contributions.md)
82. [ADR-0082 — Sources have immutable versioned definitions](0082-sources-have-immutable-versioned-definitions.md)
83. [ADR-0083 — Git Source Provider pins and archives exact commits](0083-git-source-provider-pins-and-archives-exact-commits.md)
84. [ADR-0084 — Source Provider bindings are local version-aware configuration](0084-source-provider-bindings-are-local-version-aware-configuration.md)
85. [ADR-0085 — Source Channel snapshots pin provider provenance](0085-source-channel-snapshots-pin-provider-provenance.md)
86. [ADR-0086 — Source catalogs resolve inside pinned snapshots](0086-source-catalogs-resolve-inside-pinned-snapshots.md)
87. [ADR-0087 — Source Channel compatibility uses Semantic Version intervals](0087-source-channel-compatibility-uses-semantic-version-intervals.md)
88. [ADR-0088 — Source verification binds exact definitions and snapshots](0088-source-verification-binds-exact-definitions-and-snapshots.md)
89. [ADR-0089 — Source Bootstrap profiles reuse administrative applications](0089-source-bootstrap-profiles-reuse-administrative-applications.md)
90. [ADR-0090 — AEP credentials are scope-owned and late-bound](0090-aep-credentials-are-scope-owned-and-late-bound.md)
91. [ADR-0091 — Orchestrators own development AEP shared keys](0091-orchestrators-own-development-aep-shared-keys.md)
92. [ADR-0092 — Pair manually hosted AEP extensions with workspace-bound codes](0092-pair-aep-extensions-with-workspace-bound-codes.md)
93. [ADR-0093 — AEP enrollment lifecycle is explicit and audited](0093-aep-enrollment-lifecycle-is-explicit-and-audited.md)
94. [ADR-0094 — AEP extensions initiate enrollment](0094-aep-extensions-initiate-enrollment.md)
95. [ADR-0095 — Source Pack installation reuses the Pack lifecycle](0095-source-pack-installation-reuses-pack-lifecycle.md)
96. [ADR-0096 — The official Source registry uses last-known-good observations](0096-official-source-registry-uses-last-known-good-observations.md)
97. [ADR-0097 — Source and Channel refresh are scheduled independently](0097-source-and-channel-refresh-are-scheduled-independently.md)
98. [ADR-0098 — Source registry registrations are instance-owned policies](0098-source-registry-registrations-are-instance-owned.md)
99. [ADR-0099 — Provider-specific Compose owns inference services](0099-provider-specific-compose-owns-inference-services.md)
100. [ADR-0100 — AEP unenrollment is an explicit recoverable transition](0100-aep-unenrollment-is-an-explicit-recoverable-transition.md)
101. [ADR-0101 — Source registry refresh joins the shared local scheduling lifecycle](0101-source-registry-refresh-joins-the-shared-local-scheduling-lifecycle.md)
102. [ADR-0102 — Source Providers follow hierarchical resource visibility](0102-source-providers-follow-hierarchical-resource-visibility.md)
103. [ADR-0103 — Source registry trust evaluates independent evidence dimensions](0103-source-registry-trust-evaluates-independent-evidence-dimensions.md)
104. [ADR-0104 — Source registry discovery imports retained observations exactly](0104-source-registry-discovery-imports-retained-observations.md)
