# ADR-0177: Model infrastructure is Instance-owned and Model Profiles are hierarchical

Status: Accepted — 2026-10-14

## Context

Model Providers, their discovered Models, and Runtime Profiles describe infrastructure shared by the Agentstration instance. Treating those resources as Tenant-owned duplicated infrastructure declarations and allowed a Workspace consumer to resolve configuration by name without retaining the exact resource identity. Model Profiles have different semantics: they express reusable model intent and therefore need to exist at Instance, Tenant, or Workspace scope.

ADR-0172 established a default-deny administrative overlay for descendant access. The model and runtime verticals need to apply that decision consistently while keeping deterministic local execution and late-bound Secret handling intact.

## Decision

- `ModelProvider`, provider-owned `Model`, and `RuntimeProfile` resources are Instance-only. A discovered Model inherits the exact Instance scope and immutable provider UID of its Model Provider.
- `ModelProfile` resources may be owned by Instance, Tenant, or Workspace. Their durable identity includes exact scope, namespace, UID, and generation.
- A Model Profile references its Model Provider at the explicit Instance scope. A Tenant or Workspace profile may use that provider only when an exact, current `use` grant authorizes its scope through the ADR-0172 access overlay. Missing, stale, sibling, cross-tenant, descendant, ambiguous, and name-fallback access fails closed.
- An Agent may consume a same-scope or authorized ancestor Model Profile and Runtime Profile. Publishing its immutable revision pins each dependency's UID, exact scope, namespace, kind, name, and generation; runtime materialization must use those pins rather than repeat name resolution.
- A Model Profile's Secret override is authorized as that exact profile consumer. Provider-level Parameter and Secret bindings remain authorized as the Instance Model Provider consumer. Secret values remain late-bound and are never copied into profiles, revisions, previews, or diagnostics.
- Bootstrap handlers follow resource ownership: Model Providers and Runtime Profiles are created only by Instance profiles, while Model Profiles follow their profile's Instance, Tenant, or Workspace target. Cross-scope consumers require separately administered access grants.
- The deterministic chat client remains the offline and automated-test fallback and does not require model infrastructure resources.

## Consequences

- Infrastructure is declared once per instance while Model Profiles can express platform defaults, Tenant policy, or Workspace-specific intent.
- Exact access and revision pins make scope changes, stale grants, and homonymous resources observable instead of silently selecting another resource.
- Development bootstrap data grants the disposable Development Tenant access to its seeded Instance provider, default Model Profile, and built-in Runtime Profile. Production and non-Development instances remain closed until an administrator grants access.
- Existing pre-release databases must be reseeded or migrated; no released compatibility guarantee is affected.
- Workspace bootstrap profiles that previously embedded Model Providers or Runtime Profiles must be split so an Instance profile creates infrastructure before a narrower profile creates consumers.
- This decision supersedes the model-resource scope rows and mixed-scope Workspace bootstrap behavior in ADR-0080 and ADR-0077. It also supersedes ADR-0090 where that decision describes Model Providers as Tenant-owned. Their remaining decisions stay in force.
