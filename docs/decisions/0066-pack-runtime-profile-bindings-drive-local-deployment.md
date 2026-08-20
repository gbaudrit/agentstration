# ADR-0062: Pack Runtime Profile bindings drive local deployment

Status: Accepted — 2026-08-20

## Context

Packs can contain Runtime Profile resources, but they cannot currently declare an environment-specific Runtime Profile requirement. Automatic local preparation also always selects `default/maf-default`, so an installation cannot choose a different runtime and namespaced Runtime Profiles cannot be represented by the deployment.

Runtime configuration remains a deployment concern. A Pack Agent nevertheless needs a portable default for the automatic local deployment created when that Agent is first run.

## Decision

`runtimeProfile` is a supported Pack binding target kind. `AgentProperties.runtimeProfile` records the Runtime Profile reference used by automatic local preparation and defaults to `default/maf-default` for backward compatibility.

The Pack Composer treats an Agent's Runtime Profile like its Model Profile: an unselected dependency becomes an installation binding, while explicitly selecting the Runtime Profile includes it in the Pack. Installation resolves the binding to an exact namespaced reference and stores the selection with the Pack configuration.

Automatic local preparation copies the resolved Runtime Profile name and namespace into the immutable revision and deployment. Explicit deployment APIs may also provide the Runtime Profile namespace and continue to default it to `default`.

## Consequences

- Pack installation can select an existing Runtime Profile without copying it.
- Reinstallation reuses the Pack-scoped Runtime Profile selection.
- Local execution no longer silently falls back to `maf-default` for a Pack Agent with a resolved runtime binding.
- Runtime Profile identity is namespace-correct in deployment validation and usage checks.
- The Runtime Profile remains deployment configuration; the Agent reference supplies only the default used by automatic local preparation.
