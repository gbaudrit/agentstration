# ADR-0264: Preview labels select independently delivered AEP extensions

Status: Proposed — 2026-11-26

## Context

[#1388](https://github.com/gbaudrit/agentstration/issues/1388), a native child of
#1286, adds optional extensions to the issue-command deployment vertical from
ADR-0263. The image catalogue and independent releases already exist (ADR-0252).
Deploying Foundry must not require local inference or put cloud orchestration
inside Runtime. The catalogue will grow through separate packaging requests.

## Decision

Use ordinary issue labels `preview:extension:<catalogue-id>` as deployment intent.
Keep `extension:*` labels for work classification. Admission reads live labels,
accepts only implemented AEP catalogue entries, freezes a sorted selection, and
rechecks it before announcing digests. A changed selection requires a new command.
Labels alone never trigger infrastructure. Preserve the existing ten-input manual
form; issue relay options carry the optional list. Advanced callers may supply
canonical extension deliveries directly.

Resolve each selected private GHCR image independently from the server revision,
using the existing newest-tagged publication rule. Verify the exact index digest,
Linux amd64 config, repository/source/version and source catalogue identity.
Deployment never builds images. Use contract version 2 only for requests with a
nonempty `extensions` map of canonical public receipts; preserve version 1 for
core-only deployments. Status/destroy use saved receipts without registry reads
or requiring a still-current catalogue. Deployment checks current trusted recipes.

Generate a small Compose overlay from trusted metadata: one digest-pinned AEP
container, its own read-only SharedKeyFile secret and its own persisted state
volume. The authority receives only the selected keys and explicit internal HTTP
host allowances. Keep extension discovery ports on guest loopback for root-side
verification; do not route them through Cloudflare or open Azure ingress. Keep
registry/cloud/Access credentials outside workloads, as in ADR-0257 and ADR-0259.
Transfer each selected OCI archive with digests preserved through the existing
private bundle and disposable loopback mirror.

Verify OCI identity, isolated mounts, authenticated manifest and a signed,
idempotent announcement acknowledged as available by the authority. This works
before manual application bootstrap and does not use a privileged fixture user
or inspect the application's database. Keep Azure Run Command output bounded by
returning only the verified extension IDs alongside core observations; canonical
requests/results still retain the complete digest selection.

Redeploy under the existing ownership lock. Deselect only previously recorded,
inspected owned extension containers; preserve their keys and volumes for later
reactivation. Remove managed trust for deselected extensions. Do not delete
application registrations or user definitions; they can remain disconnected.
Full reset removes all owned volumes, including deselected extension state.

## Consequences

Foundry is the initial real fixture. New packaged AEP entries reuse the same
recipe and label synchronization script without editing deployment workflows.
Selecting an extension exposes its contributions; it does not create a configured
model, supply downstream credentials, provision an inference server, or download
models. A PostgreSQL AEP extension is independent of the control-plane database
profile. Such services remain explicit operator choices.

The host owns one secret/state volume per extension. They survive deselection and
remain subject to explicit reset and the existing TTL limitations. No selection
is silently substituted when a package, key or supported identity is missing.
The PR's offline Docker regression covers transport and enrollment; protected
Azure issue-event qualification must be recorded after merge.
