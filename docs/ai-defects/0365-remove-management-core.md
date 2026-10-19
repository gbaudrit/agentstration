# CAR-0365: Management.Core remained a cross-family application catch-all

## Status

Prevented — 2026-09-14

## References

- Issue: #365
- Introducing change: Unknown; the catch-all accumulated across historical Management-plane implementations
- Corrective pull request: #364
- Related ADRs: ADR-0109

## Defect

`Agentstration.Management.Core` owned application behavior for unrelated Identity, Extensions/AEP, Models, Runtime Profiles, Packs, Sources, and resource-scope capabilities. API, Infrastructure, Runtime, Web, and test projects depended directly on this shared implementation hub, even though those capabilities have separate lifecycles and accepted resource-family owners.

The project made the architectural Control Plane umbrella appear to be one business domain. It also allowed new behavior to accumulate in a central assembly and obscured the dependency direction between resource families.

## Detection

- Stage: Code review
- Detection mechanism: inventory of the projects and types remaining under the Management prefix while decomposing the control plane for #362
- Why it was not detected earlier: earlier architecture treated Management as both a plane and an implementation module, and no repository guard required family behavior to leave the aggregate project

## Causal analysis

### Faulty approach

Independent resource-family use cases were added to a shared Management application project because they all participated in control-plane administration. Architectural-plane membership was used as a substitute for domain ownership, producing a broad dependency hub.

### Contributing assumptions

- All declarative or administrable resources were assumed to belong to one Management application module.
- Moving contracts or endpoints was assumed to provide sufficient modularity while application services remained centralized.
- A shared implementation project was assumed to simplify composition without materially coupling resource families.

### Missed signals

- The project contained unrelated services for identity, extensions, models, runtime profiles, packs, and sources.
- Host and adapter projects referenced the aggregate implementation instead of narrow family modules.
- Test ownership retained historical Management names even when tests exercised one family.
- ADR-0109's distinction between the Control Plane umbrella and resource-family owners required the aggregate project to disappear.

### Safeguard gap

Architecture tests restricted some downstream dependencies but did not assert that `Agentstration.Management.Core` was absent. The build and solution inventories could therefore remain green while the catch-all project survived.

## Resolution

Commit `e4b03bd` in PR #364 moves application services to explicit Identity, Extensions, Extensions.Aep, Models.Application, Runtime.Profiles, Packs, Sources, and Tools owners. Composition references those modules directly, test ownership and solution inventories are realigned, and `src/Agentstration.Management.Core` is deleted without introducing a replacement `ControlPlane.Core` catch-all.

## Prevention

Architecture tests now assert that the `Agentstration.Management.Core` directory is absent and that no project references the removed assembly. Family application modules are checked against host, concrete storage, Entity Framework, Agent Framework, and other forbidden implementation dependencies. ADR-0109 and repository instructions explicitly reserve Control Plane for the architectural umbrella and prohibit another global business aggregator.

## Validation

- Release build: succeeded with 0 warnings and 0 errors
- Architecture tests: 56/56 passed
- Functional validation: 855 executed, 852 passed, 3 optional external-service tests skipped, 0 failed
- Test assemblies were executed directly through generated MSTest executables because the validation environment did not support the usual .NET 10 socket-IPC path used by `dotnet test`
- Repository inspection confirmed deletion of `src/Agentstration.Management.Core` and absence of project references to the removed assembly
