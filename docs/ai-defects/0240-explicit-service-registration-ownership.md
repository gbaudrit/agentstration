# CAR-0240: Explicit service-registration ownership

## Status

Open — 2026-11-04

## References

- Issue: [#240](https://github.com/gbaudrit/agentstration/issues/240)
- Introducing change: Unknown. The issue describes incremental growth; no single introducing change has been established.
- Corrective pull request: [#1021](https://github.com/gbaudrit/agentstration/pull/1021).
- Related ADRs: [ADR-0189](../decisions/0189-service-registration-has-explicit-module-ownership.md), [ADR-0032](../decisions/0032-single-authoritative-standalone-server.md)
- Registration ownership: [Composition and service registration](../architecture/implementation/service-registration.md)

## Defect

The application could resolve services while its composition contained accidental duplicate descriptors and implicit replacement rules. The main-branch review at `6c8570fecc087a546427cb26b71acac78514c5e8` found two registrations of `IRuntimeRunExecutionScope` in Infrastructure, repeated `TimeProvider.System` defaults, fallback and configured GenAI observability registrations, and a deferred model-profile validator superseded by the host's later model-management registration.

Infrastructure's registration method also mixed storage, identity, bootstrap, resource families, tools, scheduling, execution, and client fallback concerns. The standalone host and both Console compositions contained further registration responsibilities. Successful startup alone did not establish correct descriptor cardinality, explicit ownership, or safe composition order.

## Detection

- Stage: Code review.
- Detection mechanism: Review of the current `main` graph against the issue's expected ownership and selection contracts, followed by descriptor-level and provider-validation tests.
- Why it was not detected earlier: Existing behavioral tests exercised resolved services and host startup. The default container resolves the last descriptor for single-service requests, so a working request can conceal duplicate instances or an unintended fallback.

## Causal analysis

### Faulty approach

Incremental changes extended central composition methods and relied on later registrations to select the effective implementation. That approach did not give each vertical a discoverable owner or encode whether a registration was a default, an override, or an enumerable contribution.

### Contributing assumptions

- Registering a more specific service later was treated as sufficient evidence that the graph was correct.
- Passing startup and behavior tests was treated as evidence that exactly-one contracts had one descriptor.
- The central façade remained a convenient place to add unrelated verticals as the resource-family and host boundaries evolved.

### Missed signals

Module-owned model-provider, MCP, identity, UI, and storage extensions already demonstrated explicit composition boundaries. Repository rules also kept business ownership out of executable roots and preserved local defaults. The duplicated execution scopes and fallback registrations were observable in source without executing an application.

### Safeguard gap

There was no comprehensive registration ownership map and no contract that inspected descriptor multiplicity before provider construction across the supported storage, AI, and host selections. Resolution and integration tests therefore could not distinguish intended selection from accidental last-registration-wins behavior.

## Resolution

The corrective work decomposes Infrastructure and host composition into named extension methods, introduces a validated composition-options façade with the existing overload retained, and records ownership in the architecture documentation. Explicit default and replacement semantics remove accidental duplicate selections while preserving intentional handler, provider, hook, sink, scope-accessor and hosted-service contributions. In particular, Work deliberately consumes both the HTTP request accessor and ambient Flow accessor through `IEnumerable<IWorkExecutionScopeAccessor>`, in that order. These complementary registrations are preserved. Registration remains separate from endpoint mapping and ordered initialization.

## Prevention

Registration-contract tests inspect service types, lifetimes, implementation selection, and cardinality before building providers. Provider validation covers supported SQLite/PostgreSQL and deterministic/managed AI graphs; host lifecycle checks cover background-worker toggles. The checked-in ownership map and ADR-0189 make the owner and selection semantics part of future registration reviews.

## Validation

- Reviewed the current-main registration sources and compared them with #240's acceptance criteria.
- Verified that all 71 concrete registration-method names in the ownership map exist and that the new documentation links resolve.
- `npm run decisions:validate`: passed; 187 ADR identifiers/index entries validated.
- LikeC4 validation and web-component generation: passed during documentation prebuild.
- `npm run build` in `docs/site`: passed; Docusaurus generated the complete static site.
- Build, contract tests, functional lanes, and formatting validation are pending completion of the corrective implementation. This CAR remains Open until their actual results are recorded.
