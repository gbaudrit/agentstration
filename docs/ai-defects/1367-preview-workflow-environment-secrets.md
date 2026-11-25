# CAR-1367: Preview workflow environment secrets do not resolve

## Status

Prevented — 2026-11-25

## References

- Issue: [#1367](https://github.com/gbaudrit/agentstration/issues/1367), child of [#1285](https://github.com/gbaudrit/agentstration/issues/1285).
- Introducing change: [#1366](https://github.com/gbaudrit/agentstration/pull/1366), merged as [8adbdba3](https://github.com/gbaudrit/agentstration/commit/8adbdba3f7a463e71036556e570a52d821a38809).
- Corrective pull request: [#1368](https://github.com/gbaudrit/agentstration/pull/1368).
- Verification and safe probe diagnostics: [#1371](https://github.com/gbaudrit/agentstration/pull/1371).
- Related ADRs: [ADR-0259](../decisions/0259-preview-routes-use-owned-tunnels-and-an-isolated-access-proxy.md).

## Defect

The manual preview caller reaches its reusable Azure executor with empty
`CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCESS_CLIENT_SECRET`, even though the
executor selects `preview-deployment` and receives its public variables. Both
the maintainer form and the raw Azure wrapper omit secret inheritance. The
deployment stops before creating a VM/tunnel; its ownership blob has already
been initialized. No secret value or length is recorded here.

## Detection

- Stage: Post-merge live qualification and maintainer observation.
- Detection mechanism: [Deployment 45642136038](https://github.com/gbaudrit/agentstration/actions/runs/45642136038)
  fails at the required-token check. Its two secret fields are empty while the
  public fields are populated. The standalone Access job in
  [audit 45643858591](https://github.com/gbaudrit/agentstration/actions/runs/45643858591)
  succeeds on the same main commit and protected environment.
- Why it was not detected earlier: The standalone audit does not cross a reusable
  workflow boundary. Offline Python tests inject credentials directly; pure
  validation workflow calls intentionally do not receive deployment credentials.

## Causal analysis

### Faulty approach

The AI-assisted integration added secret consumers to the reusable job without
adding the required opt-in to its two deployment callers.

### Contributing assumptions

Selecting the protected environment in the callee was treated as sufficient for
secret resolution. The observed runner behavior requires `secrets: inherit` on
the caller as well; public variables resolving successfully did not establish
that secrets would resolve. See [the upstream report](https://github.com/actions/runner/issues/4453)
and [GitHub reuse guidance](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows).

### Missed signals

Both call sites existed and could have been inspected alongside the callee.
Repository instructions require inspecting the affected vertical end to end.
The previous audit and OCI smokes established configuration/transport behavior,
not environment-secret delivery through these call sites.

### Safeguard gap

No CI contract distinguished credential-free validation callers from deployment
callers requiring explicit secret inheritance. Actionlint validates syntax, but
does not prove that required environment secrets are available at runtime.

## Resolution

Add `secrets: inherit` only to the two trusted-main deployment callers. Preserve
the callee's protected environment, main-only gate and request-as-data selection.
Keep Cloudflare values in their existing environment scope. The pure PR validation
caller does not inherit secrets. Do not rotate, copy or print working credentials
to compensate for a workflow integration omission.

## Prevention

The existing preview offline suite discovers preview workflow call sites and
requires explicit inheritance for deployment-capable calls while forbidding it
for pure validation. Mutation checks reject missing, commented or incorrectly
nested inheritance and a newly introduced deployment caller without opt-in.
Document the operational distinction in the Access guide.

## Validation

- The standalone main Access audit succeeded with the existing secrets.
- Four focused call-site contracts passed before PR creation; actionlint passed
  for the two callers and reusable executor.
- Preview offline suite: 162 total, 161 passed and one optional Azure CLI skip.
  Packaging suite: 32 passed. Actionlint and `git diff --check` passed.
- Full documentation build passed, including 254 ADRs, LikeC4 and Docusaurus.
- The trial's explicit reset succeeded in
  [cleanup 45644059933](https://github.com/gbaudrit/agentstration/actions/runs/45644059933).
- No C# source changed. The unchanged product tree was validated earlier in this
  session for #1366: Release build with zero warnings/errors, Fast 2,670 passed
  on rerun and Integration 1,681 passed with 28 optional skips. Those lanes were
  not repeated for this workflow-only correction.
- Live secret delivery is validated by [deployment 45650059013](https://github.com/gbaudrit/agentstration/actions/runs/45650059013)
  on protected main `e56dffb9d0fbc8a7387dc1c309c452e992b0fe9d` after #1368.
  Both secret fields are present in the job; their values and lengths were not
  inspected or exported. Configuration validation, owned tunnel setup, healthy
  guest/connector and observed closed-ingress checks complete before the later
  authenticated HTTPS readiness probe fails. That downstream failure remains
  #1285 qualification work; it does not negate the corrected secret delivery.
- API-triggered [preserve teardown 45651693763](https://github.com/gbaudrit/agentstration/actions/runs/45651693763)
  also succeeds through the corrected caller: it stops the guest, deallocates
  the VM and removes the owned tunnel while retaining application data.
