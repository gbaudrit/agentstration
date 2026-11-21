# CAR-1248: Private AEP credentials disappear in business execution contexts

## Status

Prevented — 2026-11-20

## References

- Issue: [#1248](https://github.com/gbaudrit/agentstration/issues/1248)
- Introducing change: Unknown for the original token adapter. The AI-assisted
  Ollama example and its partial integration coverage were added in
  [c8e26ec1](https://github.com/gbaudrit/agentstration/commit/c8e26ec1b28be113cf4b5d36047e9d66d993bb94).
- Corrective pull request: Not opened; correction is on `feat/bootstrapping`.
- Related ADRs: [ADR-0090](../decisions/0090-aep-credentials-are-scope-owned-and-late-bound.md)
  (scope-owned, late-bound transport credentials),
  [ADR-0094](../decisions/0094-aep-extensions-initiate-enrollment.md)
  (registration-owned enrollment credentials),
  [ADR-0240](../decisions/0240-initial-bootstrap-waits-for-required-extension-contributions.md)
  and [ADR-0242](../decisions/0242-model-provider-discovery-runs-asynchronously.md).

## Defect

A shared instance Model Provider fails authentication when called from a Tenant
or Workspace. Its private transport Secret exists at the extension's scope, but
the metadata store filters it through the business caller's discovery projection.
The resulting credential_unavailable exception prevents capability discovery and
inference.

## Detection

- Stage: Maintainer execution validation on the feature branch.
- Detection mechanism: A supplied execution trace identifies
  SecretAepAccessTokenProvider, AepModelProvider.ResolveCapabilitiesAsync and
  RuntimeWorkerOperationGateway.InvokeModelAsync.
- Why it was not detected earlier: Discovery ran in an infrastructure context;
  the example's creation checks and synthetic credential resolvers did not test
  real private Secret resolution from business execution contexts.

## Causal analysis

### Faulty approach

The transport adapter passed the correct registration consumer to ISecretResolver
but retained the model caller's ambient resource-visibility context. The actual
Secret metadata lookup applies both, so explicit ownership alone was insufficient.

### Contributing assumptions

Resource creation, successful discovery and prepared Agent revisions were treated
as sufficient evidence for the scoped execution path. None exercised this
credential lookup with the caller's Tenant/Workspace projection.

### Missed signals

SecretManagementService.AuthorizeCoreAsync uses GetExactAsync, whose persistence
adapter still applies ambient read/discover checks. The instance credential is
deliberately not shared with business scopes. MutableSecretResolver test doubles
did not enforce that visibility boundary.
ADR-0090 requires the registration's ownership scope as the credential consumer;
the ambient metadata projection must not substitute the model caller for it.
The enrollment provisioning path already reads registration credentials in an
infrastructure context; the runtime token adapter did not follow that pattern.

### Safeguard gap

There was no integration assertion combining the actual SQLite Secret/Vault
implementation, the DI-configured AEP adapter and business-scope model calls.

## Resolution

Enter a narrow infrastructure scope only while resolving the trusted registration's
transport credential. Preserve the explicit Secret consumer, ancestry and use-policy
checks and restore the caller before HTTP transport. Forward the request-scope
factory through model-provider and source-provider AEP adapters. Do not add grants,
copy credentials or change the general Secret resolver or caller authorization.

## Prevention

Use actual private instance Secrets and local Vault storage in the regression.
Exercise capabilities and inference from both Tenant and Workspace scopes. Assert
authenticated HTTP calls in the original caller context, continued invisibility
of the Secret, refusal of missing and out-of-scope credentials before transport,
refusal of ancestor credentials without a registration use grant, and context
restoration on success and failure.

## Validation

- Full solution Release build: succeeded with zero warnings and errors.
- Actual SQLite Secret/Vault and DI-configured AEP regression: both Tenant and
  Workspace rows passed, including capability discovery, inference, context
  restoration and all refusal cases.
- Fast functional lane: 1,561 passed. An initial unrelated Console timing failure
  passed in isolation and in the subsequent complete run; its code was unchanged.
- Focused lifecycle, Quartz shutdown and restart-recovery regressions passed.
- Real-process OpenAPI and Ollama fixture enrollment/restart smokes passed without
  the removed startup switch; the Ollama smoke also retained the AgentRevision.
- Public waiting/login smoke passed on desktop, mobile and with JavaScript disabled;
  the NoBootstrap launch profile remained ready without automatic account creation.
- Documentation production build and changed-file whitespace checks passed.
- Complete Integration lane: 1,588 passed, zero failures and 28 optional service
  cases skipped (1,616 total). Existing model-provider authentication/rotation
  regressions and both private-credential rows passed in that complete run.
- Live Ollama inference and optional PostgreSQL/LocalAI services were not exercised;
  the inference regression used a local AEP fixture with the actual Secret boundary.
