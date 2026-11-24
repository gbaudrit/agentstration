# ADR-0258: Manual previews resolve published revisions as data

Status: Accepted — 2026-11-24

## Context

[#1284](https://github.com/gbaudrit/agentstration/issues/1284) needs a maintainer
form over the canonical request and owned Azure lifecycle from ADR-0256/0257.
The server and MAF Worker have different source/version cycles. Deploying a PR
must not execute its deployment scripts with the Azure operator identity, and
teardown must survive moving refs, unavailable images or changed environment
defaults.

## Decision

Keep deployment tooling external to Agentstration. Run the operator workflow on
trusted `main`, with protected defaults in `preview-deployment`. Resolve the
selected application revision via GitHub as metadata, then select only an
already-published private GHCR index for that exact SHA. Freeze its digest before
reading platform-specific OCI source/version labels. Read bounded catalogue and
version metadata at the image's source SHA without executing repository code or
MSBuild. Require supported component identities, platform and Worker protocol.
Refuse missing publications, foreign/fork PR selections and inconsistent labels
or digests; never build images during deployment or substitute another SHA.

Select the Worker independently, defaulting to the newest tagged Worker listed by
GitHub. Ignore untagged attestation manifests. Persist both canonical deliveries,
the selected database and resolved Azure defaults in the existing lifecycle state.
Do not add a parallel deployment inventory or catalogue service.

Resolve `status` and `destroy` by environment ID from that private state, without
registry access or current platform defaults. The reusable executor revalidates
the request and ownership under its existing provider lease. Missing metadata
never authorizes adoption. Keep caller/provider concurrency groups distinct;
queue operations without automatic cancellation. Preserve is the default;
reset remains an explicit destroy choice. Wait for bounded ARM inventory
convergence before claiming complete reset.

## Consequences

Maintainers can deploy independently packaged server/Worker revisions with SQLite
or PostgreSQL and remove previews without composing JSON or using Azure manually.
An unbuilt branch head fails clearly; it is not an implicit packaging trigger.
Public summaries contain frozen selection, lifecycle state, expiry and resource
IDs, never provider credentials. Existing raw-request workflows remain compatible.
This first #1284 vertical withholds a public URL until #1285 verifies protected
access, so #1284 remains open. Automatic TTL/PR reconciliation (#1286), deployed
Playwright (#1287), optional extension selection and SSH execution (#1288) retain
their separate scopes. No Azure dependency enters product or portable contracts.
