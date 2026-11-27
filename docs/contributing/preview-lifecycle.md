# Portable preview lifecycle

The first executable increment of [#1282](https://github.com/gbaudrit/agentstration/issues/1282)
adds a versioned deployment request, safe results and a Docker host implementation
around the existing [database preview](preview-environments.md). Everything stays
under `scripts/preview`, `deploy/preview` and GitHub Actions, outside the Runtime.
See [ADR-0256](../decisions/0256-preview-lifecycle-binds-provider-neutral-requests-to-owned-resources.md).

## Public request and result

`scripts/preview/contract.py` is the executable request validator (v1 core-only; v2 adds selected AEP extensions; v3 freezes GUID deployment profiles). Unknown
fields, duplicate JSON keys, input above 64 KiB, unresolved revisions, mutable
image references and invalid provider/database combinations are rejected.

| Request field | Meaning |
| --- | --- |
| `schemaVersion` | Integer `1` for core-only requests, `2` with selected extensions, or `3` with frozen deployment-profile metadata |
| `environmentId` | 1–40 lowercase letters/digits/hyphens, starting with a letter |
| `provider` | `docker`, `azure-vm` or `ssh-docker` |
| `sourceSha` | Exact server commit; independent Worker source remains in its receipt |
| `registry`, `deliveries` | Private GHCR and canonical server/Worker metadata, reduced from existing delivery receipts |
| `extensions` | v2/v3: sorted map (v3 may be empty) of catalogue IDs to canonical AEP delivery receipts, each with its independent source/version/digest |
| `deploymentProfiles` | v3: ordered `{guid, settingsSha256, contentSha256}` pins at the exact server source SHA |
| `selectionPullRequest` | v3: the PR whose current head/order must still match before deployment, or null for explicit source-pinned requests |
| `database`, `postgresImage` | SQLite by default; PostgreSQL selects its exact supported image digest |
| `ttlHours` | Integer 1–168; metadata for later lifecycle/retention orchestration |
| `dataMode` | `preserve` or explicit destroy-only `reset` |
| `testProfile` | `readiness` (default) or `none`; deploy always waits for container health and exact image selection |
| `target` | Provider-specific, credential-free fields below |

Docker targets contain `daemonId` and host `port` (default 5080; 0 allocates a
disposable test port). Azure targets contain `subscriptionId`, `resourceGroup`, `region`, `imageVersion` and
`vmSize` (`Standard_B2ms` or `Standard_B4ms`). SSH targets contain `host`, `sshPort`,
`user`, public `hostKeyFingerprint` and application `port`.

Azure requests route to the [external VM adapter](preview-azure.md). SSH requests
can be validated; execution remains unavailable until #1288. Remote targets never
fall back to local Docker. Example
targets live under `deploy/preview/targets`; replace their sample identities.
No provisioning framework, empty adapter or provider field is added to packaging.

Results contain `schemaVersion`, `environmentId`, `provider`, `sourceSha`, selected
`images`, `database`, `observedImages`, `state`, host-local `url`, `expiresAt` and
`providerResourceIds`. The selected receipts describe the desired deployment;
`observedImages` exposes actual container references when resources exist. Azure keeps
its bounded guest response to core image observations and verified extension IDs;
the canonical request/result retains every selected extension digest. States
include `validated`, `absent`, `running`, `stopped`, `unhealthy`, `drifted` and
the `destroyed` operation outcome. The reusable workflow reports `failed` for
failed operations; it does not substitute a successful validation result for a
failed deployment. Validation returns no provider resources.

The public `health` snapshot maps selected components to normalized container
state, Docker health and restart count. It contains no raw inspect/configuration
or health-log content. Missing observations do not imply healthy containers.
The Azure comment-level `/preview redeploy` command composes an owned full reset
and fresh deploy under one provider lease; the portable request still uses
`deploy` with `dataMode: preserve`, and ordinary deploy preserves data.

## Run against a trusted Docker host

Requirements are Linux, Python 3 and Docker Compose v2. Use a trusted Docker CLI
context for the host; its exact daemon ID is checked on every operation.

```sh
python3 scripts/preview/lifecycle.py plan \
  --server-delivery /path/to/server/delivery.json \
  --worker-delivery /path/to/worker/delivery.json \
  --environment-id example --ttl-hours 24 \
  --output .agentstration-preview-requests/example.json

python3 scripts/preview/lifecycle.py validate \
  --request .agentstration-preview-requests/example.json

python3 scripts/preview/lifecycle.py deploy \
  --request .agentstration-preview-requests/example.json \
  --state-root .agentstration-preview-host

python3 scripts/preview/lifecycle.py status \
  --request .agentstration-preview-requests/example.json \
  --state-root .agentstration-preview-host

python3 scripts/preview/lifecycle.py destroy \
  --request .agentstration-preview-requests/example.json \
  --state-root .agentstration-preview-host
```

`plan` resolves the current daemon identity when no Docker `--target` file is
provided. Add `--database postgresql` to select that profile, or use `--provider`
and `--target` to describe a remote provider. No image is built during execution.
For explicit initial data, `deploy --bootstrap-config` accepts the existing private
Bootstrap file; it is never part of the public request/result.

## Ownership, retries and data

The private state directory is `<state-root>/<environment-id>`. Keep one canonical
state root per host and retain its credentials, `deployment.json` and `lifecycle.json`
together. Mode 0700 directories/0600 files protect configuration; these are local
files, not an encrypted secret manager. Per-environment non-blocking file locks
reject simultaneous local writers without blocking other IDs.

The lifecycle binds state to a Docker daemon and a random ownership ID. A small
empty `preview-<id>-owner` volume claims the namespace atomically: an existing
volume's labels cannot be overwritten by a racing claimant. Every Compose
container, network and data volume carries that ownership ID. All project
resources are inspected before changing or deleting them; foreign/unmanaged
resources, lost ownership state and stale status/destroy image/target selections
are rejected. Partial startup keeps original identities/keys and can be retried.

Existing helper-only deployments are not silently adopted. Continue managing
them with the previous helper or use a fresh lifecycle environment; restoration
of matching original state is required for managed resources. There is no global
prune or automatic orphan adoption.

`deploy` preserves data and rejects reset mode. `destroy` stops Workers, server,
then PostgreSQL, and removes only inspected owned containers/networks. Its default
preserves all volumes, including the ownership claim. A request with `dataMode`
`reset` explicitly removes those owned volumes too. Repeated destroy is safe;
host configuration/keys remain available for a deliberate redeployment. No host
or infrastructure is deleted, and no database-provider conversion is performed.

Expiry is anchored to the first managed deployment; a retry does not renew it.
Changing TTL recalculates from that anchor. [Azure reconciliation](preview-automation.md)
enforces that expiry with full cleanup and persists deletion completion/reason.
The standalone Docker CLI still records expiry without scheduling a host worker.

## GitHub Actions entrypoint

`.github/workflows/preview-environment.yml` supports `workflow_dispatch` and
`workflow_call`, with the same `operation` and public request JSON inputs. Static
`validate` runs on a GitHub-hosted runner. Host operations require trusted `main`
invocation and the `preview-deployment` environment. Docker execution uses operator-configured
`PREVIEW_RUNNER_LABELS` (JSON array including `self-hosted` and `linux`, pinned to
the intended persistent host) plus absolute `PREVIEW_STATE_ROOT`. Optionally set
`PREVIEW_BOOTSTRAP_CONFIG` to an existing protected host file. Configure environment
branch restrictions and approvals according to the host's trust policy; PR code
must never run on a deployment host with its credentials.

The requested daemon ID rejects accidental routing to another host. Mutations are
serialized by environment ID without cancellation. GHCR login uses only the job's
read-only `GITHUB_TOKEN`, with a temporary Docker auth directory; no bootstrap PAT
or cloud credential is required. Only validated public requests and safe results
are uploaded; host keys/configuration/logs are excluded. URLs are host-local in
the Docker provider. Azure uses the [VM adapter](preview-azure.md) on a hosted
runner with its own scoped OIDC configuration; accessible URLs remain #1285.

The reusable outputs expose result JSON, environment ID, state, URL and provider
resource IDs. Calling workflows grant `contents: read`, `packages: read` and
`id-token: write`: GitHub validates the complete conditional call graph. Only the
Azure execution job actually receives OIDC permission; pure validation and
Docker execution do not. Pass request JSON as a string to `request` and
one of `validate`, `deploy`, `status`, `destroy` to `operation`.

OCI validation exercises this same lifecycle against both database profiles,
including authenticated execution, preserve/recreate, repeated reset, stable
expiry, refusal to delete an unmanaged same-name volume and preservation of an
unrelated environment's volume.

## Optional extension regression

Issue labels and extension lifecycle semantics are documented in
[Preview on demand](preview-on-demand.md#choose-optional-aep-extensions-with-issue-labels).
Advanced callers can pass `extension_deliveries={"foundry": receipt}` to
`contract.plan`; canonical receipts exclude build recipes and credentials.
Version 1 requests remain unchanged when the map is empty.

To qualify real AEP containers using already-built OCI deliveries, run:

```bash
python3 scripts/preview/extension_smoke.py \
  --server-delivery /path/to/server/delivery.json \
  --worker-delivery /path/to/maf-worker/delivery.json \
  --extension-delivery /path/to/foundry/delivery.json \
  --output /tmp/preview-extension-evidence.json
```

This uses the existing Azure guest transport locally with exact archives and a
disposable loopback registry. It requires Docker Compose and Skopeo, but no Azure,
GHCR credential, administrative bootstrap account or model. It checks authenticated
enrollment before manual bootstrap, invalid discovery/enrollment proofs, persisted
identity on recreation/reactivation, deselection and full owned-resource cleanup.
The deployment-contract CI builds its fixtures with the shared OCI recipes and
pinned bases; it does not publish them or create cloud infrastructure.

GUID settings, independent Bootstrap references, generated private administrator initialization, and the deterministic executable example are documented in [Additional settings and GUID deployment profiles](../reference/additional-settings.md). Status/destroy validate the saved v3 pins without looking up the current PR or source.
