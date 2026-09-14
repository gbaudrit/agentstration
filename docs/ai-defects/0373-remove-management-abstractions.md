# CAR-0373: Management abstractions remained a control-plane catch-all

## Status

Prevented — 2026-09-14

## References

- Issue: #373
- Introducing change: Unknown; the catch-all accumulated across historical control-plane changes
- Corrective pull request: #364
- Related ADRs: ADR-0109

## Defect

`Agentstration.Management.Abstractions` remained a shared owner for unrelated control-plane resources, provider-neutral ports, API-version constants, and a global resource-kind catalogue after the resource families had acquired explicit modules. This preserved the obsolete assumption that Management owned every control-plane concept and allowed consumers to bypass the intended family boundaries.

Observable evidence included project references to `Agentstration.Management.Abstractions`, widespread use of `ManagementApiVersions` and `ResourceKinds`, the `IControlPlaneStore` compatibility alias, and family resources retained in `ManagementResources.cs`.

## Detection

- Stage: Code review
- Detection mechanism: review of the resource-family extraction performed for #362 and PR #364, followed by an inventory of the remaining `Agentstration.Management.Abstractions` types and references
- Why it was not detected earlier: earlier extraction increments verified that individual families had moved, but did not require removal of the compatibility assembly or forbid future dependencies on its namespace

## Causal analysis

### Faulty approach

The decomposition used a compatibility-first migration in which family-owned contracts were extracted incrementally while the former catch-all assembly remained available. That reduced the size of each migration, but the compatibility layer was treated as a stable boundary instead of a strictly temporary migration mechanism with a required deletion step.

### Contributing assumptions

- Keeping shared API-version and resource-kind constants in Management was assumed to be harmless after the corresponding resources moved.
- The `IControlPlaneStore` alias was assumed to preserve useful compatibility without materially weakening ownership.
- Successful family-level compilation was assumed to prove that the previous aggregate boundary had been eliminated.

### Missed signals

- The project and namespace names still declared Management as the owner of abstractions belonging to independent resource families.
- Consumers continued to reference the catch-all even though `Agentstration.Resources`, `Agentstration.ResourceManagement`, and family contract assemblies provided narrower owners.
- Architecture tests checked several extracted types but did not assert that the obsolete assembly itself was absent.

### Safeguard gap

No architecture rule failed when the compatibility project, namespace, or project references remained. Resource wire values were also not compared through their new family-owned constants, so removal of the global catalogue had no focused regression guard.

## Resolution

PR #364 removes the `Agentstration.Management.Abstractions` project, namespace, solution entry, and project references. Remaining consumers use `IResourceStore`, `ResourceApiVersions`, and family-owned resource-kind constants directly. A final Windows checkout exposed one mixed-line-ending test blob that still used the removed aliases; the test was migrated and normalized before handoff. The obsolete compatibility resources and documentation are removed or updated while persisted wire values remain unchanged.

## Prevention

Architecture tests now assert that the catch-all project file is absent, no project references it, and no product or test C# source uses its namespace, `IControlPlaneStore`, or `ManagementApiVersions`. A focused resource-kind test verifies that family-owned constants preserve the existing wire values. Dependency documentation and ADR-0109 explicitly describe family ownership and prohibit recreating another Management catch-all.

## Validation

- Release build: succeeded with 0 warnings and 0 errors
- Architecture guardrails: 75/75 passed, including project removal, absence of residual source/test usages, and preservation of persisted resource-kind wire values
- Functional validation: 874 executed, 871 passed, 3 optional external integrations skipped, 0 failed
- Git tree inspection confirmed removal of `src/Agentstration.Management.Abstractions` and addition of focused architecture guards
