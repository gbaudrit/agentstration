# ADR-0270: Additional settings compose independent startup Bootstrap

Status: Proposed — 2026-11-27

## Context

[#1419](https://github.com/gbaudrit/agentstration/issues/1419) needs reproducible
preview scenarios authored before a PR number exists. JSON array overlays cannot
represent independent ordered Bootstrap applications, and a global catalogue root
cannot locate both shared and scenario-local profiles. ADR-0075, ADR-0143,
ADR-0243, ADR-0249 and ADR-0250 already define the catalogue, fenced phased
initialization, contribution resolution and readiness. ADR-0258, ADR-0266 and
ADR-0269 define trusted external selection and the owned deployment lifecycle.

## Decision

Capture `Agentstration:AdditionalSettings` once, before host services/options are
constructed. Load at most sixteen GUID-named JSON files in that order, relative
to the application content root. Use the native .NET JSON provider to validate
isolated bytes, including its case-insensitive and colon-delimited key semantics.
Refuse any key equal to or below `Agentstration:Bootstrap` or
`Agentstration:AdditionalSettings`, including empty/null section replacements.
Allow other global overrides. Integrate validated providers after ordinary JSON
and before environment/arguments; never recursively read the resulting list and
never reload additional files at runtime.

`InitialBootstrapEnabled=false` on Core disables all initial applications,
including additional ones. Core `EnableAdditionalSettingsBootstrap` defaults to
false and additionally authorizes processing only selected GUID blocks. Each
`<guid>:Bootstrap` retains its activation, named-profile arrays, scoped
applications and `ProfileReferences`. Each reference names its own profile
directory and target; it does not require a global catalogue root. Existing
`Path`, `InitialProfiles`, manual catalogue behaviour and Development launch
profiles retain their contracts. Relative reference paths use the content root;
manifest/Pack content retains its own profile directory as its base.

Prepare all selected applications structurally before applying topology. Reuse
the existing phases, authenticated enrollment, bounded dependency waits,
Instance → Tenant → Workspace ordering and create-only handlers. Preserve
Core, then file order within a scope/phase. Recheck profile digests after the
contribution barrier and at execution. Never concatenate Bootstrap arrays or
produce a rewritten Bootstrap settings file. Readiness follows complete resource
configuration, rather than an enrollment acknowledgement.

Sources live under `deploy/profiles/previews/<guid>/`. One visible `Preview
configuration` subsection of the PR description contains a bounded JSON object
with an ordered `profiles` array of unique canonical GUIDs. Absence selects none.
Trusted-main tooling freezes that selection with the server source SHA and image
digest, validates exact source blobs and profile boundaries, and records settings
SHA-256 and content pins in deployment request v3. Shared referenced content is
included in the content pin. The image contains all versioned profile content;
only selected files are listed at runtime. Its root filesystem is read-only,
with `/data` and temporary storage retaining their existing writable purposes.

Recheck PR head/order before mutation and under the existing lifecycle lease.
Automatic reconciliation includes ordered metadata changes in its desired
intent. A metadata-only selection change preserves data, including when the
persistent automatic redeploy modifier is present. Status/destroy use saved
requests without consulting the PR body, branch, current catalogue or registry.
A normal update/restart replays create-only initialization. Explicit redeploy
retains the existing owned full-reset contract; overlays never reconcile existing
passwords, resource definitions or operator choices.

Report requested versus successfully applied GUID order and Bootstrap readiness
without publishing settings or credentials. Existing pending/error/deployed
labels retain their meanings. Pre-merge validation uses offline fake providers;
trusted-main cloud qualification remains separate and does not close #1419.

## Consequences

- Application loading and Bootstrap contain no GitHub, Azure or PR identity.
- A GUID folder does not activate itself in another PR after merge.
- Profile-only changes select server image validation and enter packaging inputs.
- Configuration is immutable for one process; changes require a normal restart.
- Deployment request v1/v2 and existing saved selections remain readable.
- Native startup validation remains authoritative for full manifest semantics;
  source checks do not execute candidate code with deployment credentials.
- Live cloud qualification, deployed browser scenarios (#1287) and SSH (#1288)
  remain outside this change. #1385's parallel qualification is untouched.
