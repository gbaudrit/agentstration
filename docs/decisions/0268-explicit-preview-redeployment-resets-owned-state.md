# ADR-0268: Explicit preview redeployment resets owned state

Status: Proposed — 2026-11-26

## Context

After qualifying PR-comment deployment and status in #1405, the maintainer
requested a bare `/preview` alias, component health and `/preview redeploy` as
destroy followed by deploy. ADR-0256 keeps ordinary deploy data-preserving and
requires explicit reset. A reset must not be inferred from an ordinary deploy,
a changed label, a failed update or a retried workflow.

## Decision

Normalize `/preview` alone to the existing deploy command. Normalize the explicit
`/preview redeploy` command to deploy with a bounded boolean reset intent, bound
to the original unedited command and the complete owned feedback marker. Keep
the ten-input manual form and portable request schema unchanged. The current
Azure executor receives reset intent through a typed reusable-workflow input;
it refuses the flag on other operations or unsupported providers.

Freeze the selected current head, labels and private image digests and validate
private settings before touching the old deployment. Under the existing exclusive
blob lease, verify scope/repository ownership and audit the current shared Access
policy before withdrawing the old route or deleting infrastructure. Remove owned
infrastructure, wait for empty inventory, remove the owned tunnel and record
completion of reset. Then recreate using the existing deploy path with a
new owner, expiry and credentials. Shared resources remain. An absent environment
is created normally. A failed reset prevents recreation; a failed recreation never
restores deleted data and can resume with ordinary data-preserving deploy.
Only explicit reset permits changing database/location/OS between lifecycles.

Expose component container state, Docker health and restart count as bounded
public result data. Normalize unknown/duplicate/missing states and never export
inspect configuration, error strings or health logs. For Azure status, include
the current trusted collector in Run Command so it works with already deployed
guest tooling. Keep the combined result below the existing 4 KiB transport bound.
Unavailable observations remain unavailable, including when the VM is stopped.
Remove the redundant Access-provider sentence from comments while preserving all
protected-route checks and URL withholding on failure.

## Consequences

Maintainers can explicitly request a fresh preview without two separate commands.
Feedback states that redeploy erases data before execution and describes recovery
after partial failure. No automatic reset, deployment on push, TTL cleanup, new
Runtime capability, registry or cloud service is introduced. Offline tests cover
intent substitution, ownership, reset failure, retry and health boundaries;
destructive live qualification follows merge and an explicit maintainer command.
