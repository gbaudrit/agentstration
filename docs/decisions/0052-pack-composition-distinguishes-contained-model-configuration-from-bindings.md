# ADR-0052: Pack composition distinguishes contained model configuration from bindings

Status: Accepted — 2026-08-17

## Context

Workspace-authored Packs initially treated every Model Profile as an installation binding and did not allow Model Providers or Runtime Profiles to be selected. This kept environment configuration outside the artifact, but prevented authors from producing self-contained local Packs. The resources do not have identical portability characteristics: a Model Profile primarily expresses reusable model intent, a Model Provider contains an endpoint and provider-specific options, and a Runtime Profile describes runtime behavior that is selected by deployment rather than by the Agent definition.

## Decision

The Pack Composer supports `ModelProfile`, `ModelProvider`, and `RuntimeProfile` as selectable resources. A dependency that can be either contained or externally resolved uses the current explicit selection as its authoring strategy:

- an Agent's unselected Model Profile remains a `modelProfile` installation binding;
- selecting that Model Profile includes its clean manifest and makes the Agent reference relative to the Pack namespace;
- an included Model Profile's unselected Model Provider becomes a `modelProvider` installation binding;
- selecting that Model Provider includes its clean manifest and makes the profile reference relative to the Pack namespace;
- a Model Provider credential is always a `secret` binding and Secret values are never exported;
- a Runtime Profile can be selected explicitly, but is not inferred from an Agent because the deployment, not the Agent definition, owns that association.

The Composer removes control-plane identity, status, ETag, workspace, tenant, and Pack-provenance state from exported resources. Provider endpoint and option configuration remains visible in review because an explicitly selected provider is an intentional snapshot of configuration. Installation persists `modelProvider` binding choices with the other Pack bindings so reinstallation can reuse them.

## Consequences

- Authors can choose between portable bindings and self-contained model configuration without a second strategy editor.
- Adding or removing a dependency target immediately changes the preview from binding to contained resource or back.
- Installed resources continue to use the Pack identity namespace and internal references remain collision-free.
- Runtime Profiles are distributable, but deployment templates and automatic runtime selection remain separate follow-up work.
- Provider-specific option schemas and stronger sensitive-option inspection remain defense-in-depth follow-up work; credentials continue to be represented only by Secret bindings.
