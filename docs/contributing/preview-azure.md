# Azure VM previews

The external adapter for [#1283](https://github.com/gbaudrit/agentstration/issues/1283)
reuses the [portable lifecycle](preview-lifecycle.md) and the existing SQLite or
PostgreSQL Compose profile. No deployment service is added to Agentstration.
See [ADR-0257](../decisions/0257-azure-previews-use-oidc-and-credential-free-oci-transfer.md).

## Execution and ownership

An Azure target now requires `subscriptionId`, `resourceGroup`, `region`, `vmSize`
and `imageVersion`. Update old contract-only Azure requests with the
new fields `resourceGroup` and `imageVersion`; Docker/SSH requests are unchanged.
Use [the target example](https://github.com/gbaudrit/agentstration/blob/main/deploy/preview/targets/azure-vm.example.json)
only as a shape: select an available **exact** Canonical `ubuntu-24_04-lts:server`
version in the target region. `latest` is rejected.

The Bicep template creates a B2ms/B4ms VM, a 64 GiB Standard SSD data disk, an
explicit outbound public IP, NIC, isolated VNet and NSG denying **all** ingress.
The VM has no Azure identity. Neither SSH nor application ports are publicly
reachable. The application binds guest loopback; `url` stays null until the
protected-access increment (#1285). Bootstrap is disabled by default, just as
in the local Preview profile; Development fixture credentials are not enabled.

CI pulls private GHCR indexes by digest with its ephemeral `GITHUB_TOKEN`,
exports the original manifests/attestations and uploads a private temporary
bundle. A read-only 30-minute single-blob URL goes exclusively into Managed Run
Command protected parameters. A root-only guest script checks its SHA-256,
unsets the URL and stages the images in a loopback registry. It then runs the
existing Docker lifecycle. `images` identifies the original GHCR deliveries;
`observedImages` identifies loopback mirrors with the **same** digests. The
registry container is removed after each transfer. No GHCR credential reaches
the VM. No workload mounts host tooling, Azure agent state or a Docker socket.

A state blob records ownership, successful guest initialization and the canonical
request **without secrets**. Once initialized, deployment refuses a guest whose
protected host state has disappeared; restore it or explicitly reset instead of
silently generating new identities.
Every ARM mutation checks the random owner, repository and environment tags.
An infinite blob lease and Actions concurrency serialize operations. Two previews
use distinct ARM names and state namespaces. Lost ownership metadata never
causes automatic resource adoption. Keep the staging account until all previews
have been reset; deleting it loses their ownership records.

| Operation | Azure behavior |
| --- | --- |
| `validate` | Pure contract validation, no Azure connection or provisioning |
| `deploy` | Provision/start the owned VM, transfer exact OCI indexes, preserve data and verify healthy containers |
| `status` | Inspect owned ARM resources; query guest containers when the VM is running |
| `destroy`, `dataMode: preserve` | Stop containers and deallocate VM; retain data, protected credentials, disks and networking |
| `destroy`, `dataMode: reset` | Delete only owned VM/NIC/IP/VNet/NSG/data disk; OS disk deletes with VM; retain shared bootstrap account/group |

Retries preserve the original expiry. Reset allows a new data lifecycle, including
a database change, with a new owner/expiry. Database conversion, automatic TTL
cleanup (#1286), revision selection UI (#1284), public URL/access policy (#1285)
and optional extension selection remain separate work. A reset removes the
environment's server and Worker identities; preserve-destroy retains them.

## One-time Azure and GitHub preparation

Perform bootstrap with a maintainer identity capable of creating the dedicated
resource group, private staging account and scoped role assignments. This is not
part of the deployment identity's permissions. Register the Compute, Network and
Storage providers and check regional B-family vCPU quota before provisioning.

1. Create a dedicated resource group, for example `agentstration-previews`.
   Do not mix production resources into it.
2. Create a dedicated `StorageV2` account in that group, Standard LRS, TLS 1.2
   minimum, with `allowBlobPublicAccess: false` and `allowSharedKeyAccess: false`.
   Create a private `previews` container using Entra authentication (`--auth-mode login`).
   With shared keys disabled, the bootstrap maintainer also needs Blob Data
   Contributor to create the container; grant it temporarily if necessary.
   Its HTTPS data endpoint must be reachable from the hosted runner and VM;
   anonymous access remains disabled. Add a storage lifecycle rule expiring
   `previews/transfer/` blobs after one day as a fallback for terminated runners
   (replace `previews` if using another container name). Azure lifecycle prefix
   matching is literal, not a wildcard. **Do not expire ownership records**
   under `<resource-prefix>/lifecycle.json`.
3. Create an Entra application/service principal with GitHub federation:
   issuer `https://token.actions.githubusercontent.com`, audience
   `api://AzureADTokenExchange`, subject
   `repo:gbaudrit@248441222/agentstration@1318041041:environment:preview-deployment`.
   This repository uses GitHub immutable subjects, verified with
   `gh api repos/gbaudrit/agentstration/actions/oidc/customization/sub`
   (`use_immutable_subject: true`, `use_default: true`). For another repository,
   inspect its actual `sub_claim_prefix` and append
   `:environment:preview-deployment`; do not assume the older name-only subject.
   See [GitHub immutable subjects](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims).
   Create no client secret.
4. Render `deploy/preview/azure/operator-role.json` with `AssignableScopes` set
   to the exact resource group ID and create that custom role through Azure CLI
   (`az role definition create --role-definition @rendered-role.json`). This is
   an **Azure RBAC** role under the resource group’s **Access control (IAM)**,
   not an Entra application role. The checked-in file uses Azure CLI format;
   [portal imports use a `properties` wrapper](https://learn.microsoft.com/en-us/azure/role-based-access-control/custom-roles-portal#start-from-json).
   Assign it to
   the federated principal **at the resource group only**. Its actions allow
   the required VM/disk/network operations and deployments, not role assignment,
   user-assigned identity assignment or storage-key access. VM write permission
   inherently allows system-assigned identity configuration: the template sets
   identity to None and the adapter rejects an observed managed identity before
   transferring workloads.
5. Assign **Storage Blob Data Contributor** to that principal **at the staging
   storage account only**. Account scope is necessary for user-delegation key
   generation; the dedicated account limits its data-plane reach. Allow time
   for RBAC propagation before testing.
6. Configure the protected GitHub environment `preview-deployment`, limit its
   deployment branches to trusted `main`, and configure these repository or
   environment variables:

   | Variable | Value |
   | --- | --- |
   | `AZURE_PREVIEW_CLIENT_ID` | Federated application client ID |
   | `AZURE_PREVIEW_TENANT_ID` | Entra tenant ID |
   | `AZURE_PREVIEW_SUBSCRIPTION_ID` | Target subscription ID |
   | `AZURE_PREVIEW_RESOURCE_GROUP` | Exact dedicated preview resource group |
   | `AZURE_PREVIEW_STORAGE_ACCOUNT` | Dedicated staging account name |
   | `AZURE_PREVIEW_STORAGE_CONTAINER` | Private container, default `previews` |

The common workflow routes Azure execution to a hosted runner. Only its Azure job
has `id-token: write`; package permissions remain read-only. Docker execution keeps
its persistent-host configuration and has no OIDC permission. GitHub validates permissions across the complete reusable workflow call graph,
including conditional jobs. All reusable callers must therefore grant
`contents: read`, `packages: read` and `id-token: write`; only the Azure execution
job actually receives OIDC permission. Both operations and selected scope are checked before authentication.
No PAT, `GHCR_BOOTSTRAP_TOKEN`, Azure client secret or account key is required.

For a locally authenticated maintainer, plan from the existing delivery receipts,
then set the two storage environment variables above and use the common CLI:

```bash
python3 scripts/preview/lifecycle.py plan \
  --server-delivery server-delivery.json --worker-delivery worker-delivery.json \
  --environment-id azure-check --provider azure-vm \
  --target azure-target.json --output request.json
python3 scripts/preview/lifecycle.py validate --request request.json
# The following command provisions billable resources.
python3 scripts/preview/lifecycle.py deploy --request request.json --output result.json
python3 scripts/preview/lifecycle.py status --request request.json --output status.json
# For complete per-preview removal, explicitly change dataMode to reset in the request.
python3 scripts/preview/lifecycle.py destroy --request reset-request.json --output destroyed.json
```

Local deployment requires Azure CLI with Bicep 0.44.1, `skopeo`, `ssh-keygen`,
Python 3.11+ and private GHCR authentication in `DOCKER_CONFIG/config.json`.
The hosted workflow supplies these tools/credentials. Workloads run only on the VM.
Cloud-init uses the selected Ubuntu image's package repositories to install Docker,
Compose and skopeo; host security packages can advance, while workload and transfer
registry image digests stay immutable.

## Failure recovery and costs

Inspect the safe result and ARM provisioning state first. Raw command output,
cloud-init logs, agent settings, guest credential files and private archives are
not uploaded as workflow artifacts. A failed transfer can be retried using the
same request; an owned leftover loopback registry is replaced safely. Reset also
works when the application never initialized. An unverified orphan OS disk or a
foreign resource blocks automatic mutation: inspect its ARM provenance and repair
ownership explicitly rather than letting a retry adopt it.

A forcibly terminated runner may leave the ownership blob leased. After confirming
that the original workflow and ARM operation have finished, break **that exact
`<resource-prefix>/lifecycle.json` blob lease** with Azure CLI (`az storage blob lease
break`, account/container/blob, `--auth-mode login`), then retry. Do not break a
lease while another run is active. Staged blobs normally delete in `finally`;
the fallback storage lifecycle rule handles terminated uploads.

Deallocation stops VM compute charges but **retained data/OS disks and the public
IPv4 address remain billable**. Reset removes per-preview resources; the shared
account/group are intentionally retained. Add an Azure Cost Management budget
and alerts for the dedicated group. TTL is currently metadata, not a cost guard.

Before accepting B2ms capacity, run a trusted Azure trial with each database
profile, record `provisionAndDeploySeconds`, guest memory and Azure Monitor
`Percentage CPU`, `CPU Credits Remaining` and `CPU Credits Consumed`. Metrics may
initially be `pending`/`unavailable`; that is not evidence of zero use. Verify the
chosen Ubuntu image, quota, successful preserve/recreate and full reset, and
confirm no owned resources remain. Price the selected regional B2ms/B4ms PAYG
SKU, both disks, public IP and staging/egress using current Azure pricing; record
actual duration and estimated cost with the trial evidence. No capacity or cost
claim is made before that run.

## Offline and CI validation

`Preview Azure contracts` compiles Bicep and runs offline ownership/transport
contracts without OIDC or Azure provisioning. For trusted repository branches,
it also calls the reusable entrypoint for all three provider contracts; the
execution jobs remain skipped. Request/result artifacts include the environment
ID so multiple calls in one run do not collide. Server image validation additionally
runs `azure_guest_smoke.py` against the exact built OCI archives for SQLite and
PostgreSQL, covering mirroring, health and preserve/recreate with no registry
credential in the guest. The existing authenticated AWP execution smoke remains
in place. These checks do not substitute for an Azure trial of federation, RBAC,
cloud-init, region/image availability, networking, billing and teardown.
