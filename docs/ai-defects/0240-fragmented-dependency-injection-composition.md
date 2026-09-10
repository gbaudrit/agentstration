# CAR-0240: Fragmented dependency injection composition

## Status

Open — 2026-09-10

## References

- Issue: #240
- Introducing change: #30 for the duplicate Runtime execution-scope registration; broader composition growth spans multiple changes
- Corrective pull request: Pending
- Related ADRs: ADR-0001, ADR-0032

## Defect

The standalone composition accumulated 154 direct service registrations in one
`AddAgentstration` method while additional registrations remained in the Web
and Workplace executable roots and in a broad Console extension.

The default container silently accepted an exact duplicate
`IRuntimeRunExecutionScope` registration. Other exactly-one services relied on
caller order to replace fallbacks, including the Model Profile reference
validator and configured GenAI observability options. This contradicted the
repository's explicit modular-monolith boundaries and made the effective graph
hard to review.

## Detection

- Stage: Code review
- Detection mechanism: issue #240 audited all production
  `IServiceCollection` calls and compared exact service descriptors across the
  complete host composition.
- Why it was not detected earlier: functional host tests resolved only the last
  descriptor selected by the default container. They did not inspect descriptor
  cardinality, so duplicate exactly-one registrations remained invisible.

## Causal analysis

### Faulty approach

AI-assisted feature changes appended registrations near the feature being
implemented without assigning durable registration ownership to a module-level
composition extension. Pull request #30 added a Runtime execution-scope
registration near the Runtime queue even though pull request #28 had already
registered the same contract near Runtime Run services.

Later changes followed the same append-to-root pattern. The application
continued to start because Microsoft.Extensions.DependencyInjection resolves
the last descriptor for a single service request.

### Contributing assumptions

- Successful host startup was treated as evidence that each exactly-one
  contract had one descriptor.
- Registration order was assumed to be a sufficient replacement mechanism for
  standalone fallbacks.
- Keeping registrations in one composition root was treated as equivalent to
  keeping their module ownership explicit.
- Feature-level tests were assumed to cover the structure of the complete
  service graph.

### Missed signals

- Existing storage, identity, Model Provider, Management, MCP, and UI
  registration extensions already demonstrated cohesive ownership.
- `Agentstration.Infrastructure` is documented as composition support for
  explicit module boundaries, but its public method had become a multi-module
  implementation body.
- The two identical `IRuntimeRunExecutionScope` statements were visible in the
  same file.
- No test enumerated `ServiceDescriptor` instances for exactly-one contracts.

### Safeguard gap

The repository validated behavior and host lifecycle but did not validate
descriptor count, lifetime, implementation selection, or representative
provider graphs with `ValidateOnBuild` and `ValidateScopes`. The default
container's last-registration-wins behavior therefore masked duplicates and
undocumented replacements.

## Resolution

The platform composition is split into focused extensions for foundation,
control-plane storage, security/bootstrap, agent runtime, Packs, Sources,
tools/Triggers, Runtime Runs, Work, and Flows. The public `AddAgentstration`
method is retained as a small façade and accepts a cohesive options object,
while its existing overload remains compatible.

Web, Console, and Workplace registrations are delegated to focused host
extensions. The duplicate Runtime execution-scope descriptor is removed.
Configured GenAI options, the Management-backed Model Profile validator, and
the server composite Flow event sink now use explicit replacement semantics.

## Prevention

Registration-contract tests now inspect the descriptor collection before
provider creation. They assert cardinality for exactly-one contracts, enumerate
intentional multi-bindings, verify Deterministic and Managed resolver
selection, and validate SQLite and PostgreSQL composition.

Architecture tests keep executable roots free of direct concrete
registrations and keep `AddAgentstration` as a façade. The durable ownership map
in `docs/architecture/dependency-injection.md` documents where new
registrations belong and when `TryAdd`, `Replace`, or repeated `Add` is valid.

## Validation

- Static diff and whitespace validation completed.
- .NET restore, build, and MSTest validation pending in GitHub Actions because
  the local execution environment does not provide the .NET SDK.
