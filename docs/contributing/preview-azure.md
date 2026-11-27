# Azure VM previews

The external adapter for [#1283](https://github.com/gbaudrit/agentstration/issues/1283)
reuses the [portable lifecycle](preview-lifecycle.md) and the existing SQLite or
PostgreSQL Compose profile. No deployment service is added to Agentstration.
See [ADR-0257](../decisions/0257-azure-previews-use-oidc-and-credential-free-oci-transfer.md).
For the maintainer form selecting a published revision without composing JSON,
see [manual previews from GitHub Actions](preview-on-demand.md).

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
reachable. The application binds guest loopback; the [protected outbound route](preview-access.md)
publishes `url` only after current Access, ingress and HTTPS probes succeed.
Bootstrap is disabled by default, just as
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

The issue/PR command `/preview redeploy` requests explicit reset-before-deploy:
validate/freeze the new image selection, remove only owned infrastructure and
data, then recreate under one lease. The previous data is lost; a partial reset
stops recreation. A normal `/preview` or `/preview deploy` preserves data.
Status returns a bounded live container/health/restart snapshot even for older
guest tooling, without exporting container configuration or health-check logs.

Retries preserve the original expiry. Reset allows a new data lifecycle, including
a database change, with a new owner/expiry. [Scheduled reconciliation](preview-automation.md)
enforces expiry, capacity and opted-in PR lifecycle; database conversion remains separate work. The
protected route's live qualification remains #1285. A reset removes the
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

### GitHub deployment defaults

Keep these non-secret variables in **Settings > Environments >
preview-deployment > Environment variables**, alongside the six identity/storage
variables above. The **Preview Azure environment** manual entrypoint reads them
from a protected main-only job with no Azure authentication, freezes the resolved
request, then calls the existing generic lifecycle workflow.

| Variable | Example | Behavior |
| --- | --- | --- |
| `AZURE_PREVIEW_OS_PROFILE` | `ubuntu-24.04` | Only the qualified Ubuntu profile is supported |
| `AZURE_PREVIEW_IMAGE_VERSION` | Exact regionally verified version | Default only when omitted from the input target |
| `AZURE_PREVIEW_VM_SIZE1` | `Standard_B2ms` | Default size when omitted from the input target |
| `AZURE_PREVIEW_VM_SIZE2` | `Standard_B4ms` | Optional second size slot |
| `AZURE_PREVIEW_VM_SIZE3` through `SIZE5` | Unset | Optional slots; empty/unsupported selections fail before Azure access |

The Azure manual entrypoint exposes `azure_vm_size_profile`: `request`
(default), or slots `1` through `5`. `request` preserves explicit target values
and fills a missing size from slot 1. An explicit slot must be configured to
`Standard_B2ms` or `Standard_B4ms` and must agree with an explicitly supplied size;
omit that target field when choosing another slot. GitHub's static choice list
keeps unused slots visible.

Only `deploy` and `validate` resolve defaults. The resulting request artifact
contains the exact `vmSize`, `imageVersion` and `osProfile`; use that artifact for
later operations. `status` and `destroy` require the recorded target and ignore
changed defaults. They reject slot overrides. Legacy v1 requests without
`osProfile` still mean `ubuntu-24.04` and remain inspectable/removable. The local
CLI and generic **Preview environment** workflow continue to require a complete
request; GitHub defaults belong only to the external Azure entrypoint. A complete
request can be validated without accessing the protected environment. Testing its
configured environment defaults requires the Azure entrypoint on trusted `main`;
`validate` resolves them without Azure login or infrastructure operations.
Other distributions require their own tested
bootstrap and are not activated by setting a variable to an arbitrary value.

### Verify the exact Ubuntu image before deployment

Use a maintainer session such as Azure Cloud Shell to inspect the regional image
catalog. Azure CLI's publisher, offer and SKU filters accept partial names. A list
of versions alone can therefore select a version belonging to another SKU or
architecture. Keep the full URN and apply exact filters:

```bash
preview_subscription_id='<subscription-id>'
preview_region='westeurope'
az vm image list --subscription "$preview_subscription_id" --location "$preview_region" \
  --publisher Canonical --offer ubuntu-24_04-lts --sku server --architecture x64 --all \
  --query "[?publisher=='Canonical' && offer=='ubuntu-24_04-lts' && sku=='server'] | [-5:].{urn:urn,version:version}" \
  --output json
preview_image_version='<selected-exact-version>'
az vm image show --subscription "$preview_subscription_id" --location "$preview_region" \
  --urn "Canonical:ubuntu-24_04-lts:server:$preview_image_version" \
  --query '{id:id,architecture:architecture,hyperVGeneration:hyperVGeneration}' --output json
```

Require a successful lookup of the selected x64 Generation 2 image, then put that
exact version in the target's `imageVersion`. Do not copy the example version
without checking its regional availability or substitute `latest` in a deployment
request. The group-scoped deployment identity need not gain subscription-wide
catalog permissions for this maintainer check. See the
[Azure CLI image reference](https://learn.microsoft.com/cli/azure/vm/image).

### Run the lifecycle

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

Inspect the safe result and ARM provisioning state first. CLI failures identify
the operation and up to six Azure error codes, including nested ARM JSON details,
when present; parameter values, error messages/targets and
raw provider output remain withheld. Raw command output,
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
and alerts for the dedicated group. Scheduled reconciliation applies recorded
expiry unless an authorized `preview:keep` label retains the environment;
queueing, required approvals or failed runs can delay cleanup. This is
not an Azure currency spending cap.

Before accepting B2ms capacity, run a trusted Azure trial with each database
profile, record `provisionAndDeploySeconds`, guest memory and Azure Monitor
`Percentage CPU`, `CPU Credits Remaining` and `CPU Credits Consumed`. Metrics may
initially be `pending`/`unavailable`; that is not evidence of zero use. Verify the
chosen Ubuntu image, quota, successful preserve/recreate and full reset, and
confirm no owned resources remain. Price the selected regional B2ms/B4ms PAYG
SKU, both disks, public IP and staging/egress using current Azure pricing; record
actual duration and estimated cost with the trial evidence. The functional baseline
and its cost assumptions are recorded below; they do not establish a load SLA.

### West Europe PAYG cost baseline

The [public rate snapshot](https://github.com/gbaudrit/agentstration/actions/runs/45445309638)
retrieved on **2026-11-24 at 17:09 UTC** reports these EUR list prices, before tax,
discounts or reservations:

| Component | Price | Unit |
| --- | ---: | --- |
| Linux B2ms | 0.0862 | EUR/hour |
| Linux B4ms | 0.1724 | EUR/hour |
| Standard IPv4 static public IP | 0.0045 | EUR/hour |
| Standard SSD LRS E4 OS disk, up to 32 GiB | 2.1545 | EUR/month |
| Standard SSD LRS E6 data disk, 64 GiB | 4.3090 | EUR/month |

Using a 730-hour month for disk prorating, the fixed baseline is approximately
**EUR 0.099554/hour for B2ms**, or **EUR 0.185754/hour for B4ms**. A two-hour
preview is approximately EUR 0.20 / EUR 0.37 respectively. Keeping everything
running for 730 hours is approximately EUR 72.67 / EUR 135.60. Verify the selected
image's OS disk tier when changing image versions; this estimate assumes E4.

These figures exclude disk transactions, shared staging Blob capacity/operations
and outbound transfer. They are estimates, not subscription invoices. Preserve-
destroy removes compute billing after deallocation but retains approximately
**EUR 9.75/month** of disks and IPv4 at these rates; reset is needed to remove that
per-preview baseline. The shared storage account and small ownership records remain.

Image delivery has no expected bandwidth charge when staging and VM share a
region: GitHub currently makes [GHCR storage/bandwidth free](https://docs.github.com/en/billing/concepts/product-billing/github-packages),
Azure ingress is free, and [same-region Azure transfer is free](https://azure.microsoft.com/en-us/pricing/details/bandwidth/).
Confirm the storage account's region. Cross-region staging or application Internet
egress has separate rates. Blob requests and temporary capacity remain billable;
seven-day soft delete can retain deleted image bundles for seven days. Do not
mistake deletion from the active blob list for immediate capacity reclamation.

Budget alerts do not stop resources. The maintainer verified **10 regional vCPUs
and 10 BS-family vCPUs available** before these trials; B2ms uses two and B4ms four.
SKU restrictions were empty for B2ms in West Europe. Recheck quota/SKU availability
when changing region or adding parallel previews. The independent
[preview capacity limits](preview-automation.md) default to two reserved environments
and four vCPU; these do not replace Azure quota or spending controls.

## Live qualification — 2026-11-24

Both database profiles completed the trusted-main lifecycle on **B2ms in West
Europe**, using exact private server/Worker digests and the Ubuntu x64 image
`Canonical:ubuntu-24_04-lts:server:24.04.202611170`. PostgreSQL also retained its
checked-in public image digest. No local model or live model endpoint was required.

| Profile | Initial provision/deploy | Preserve/redeploy | Initial available memory | Latest observed CPU average / credits remaining |
| --- | ---: | ---: | ---: | --- |
| SQLite | 262.3 s | 216.9 s | 6,986,480 KiB | 5.34% / 60.72 |
| PostgreSQL | 239.4 s | 208.2 s | 6,971,688 KiB | 12.44% / 64.93 |

These are point-in-time readiness/startup measurements, not sustained-load,
peak-memory or inference benchmarks. CPU/credit samples can lag the guest.
The tested configuration supports B2ms as the configurable default for this
lightweight preview baseline; assess actual workloads/extensions before sizing
them. B4ms remains an available configuration, not a separately measured trial.

| Profile | Deploy | Preserve | Redeploy | Reset | Independent final status |
| --- | --- | --- | --- | --- | --- |
| SQLite | [45436683907](https://github.com/gbaudrit/agentstration/actions/runs/45436683907) | [45438377903](https://github.com/gbaudrit/agentstration/actions/runs/45438377903) | [45439039818](https://github.com/gbaudrit/agentstration/actions/runs/45439039818) | [45440036663](https://github.com/gbaudrit/agentstration/actions/runs/45440036663) | [45440743417](https://github.com/gbaudrit/agentstration/actions/runs/45440743417) |
| PostgreSQL | [45443076440](https://github.com/gbaudrit/agentstration/actions/runs/45443076440) | [45444252247](https://github.com/gbaudrit/agentstration/actions/runs/45444252247) | [45445033172](https://github.com/gbaudrit/agentstration/actions/runs/45445033172) | [45446112201](https://github.com/gbaudrit/agentstration/actions/runs/45446112201) | [45446742151](https://github.com/gbaudrit/agentstration/actions/runs/45446742151) |

Recreation found the required initialized host state and retained the exact
digests, principal resource IDs and original expiry. Both independent final
results report `state: absent` and `resources: []`. The immediate reset snapshots
still listed the final data disk: ARM list convergence is delayed after successful
deletion. Shared bootstrap storage/role assignments and ownership blobs remain.

The earlier invalid-image attempt also exercised safe partial-failure recovery:
the [refusal](https://github.com/gbaudrit/agentstration/actions/runs/45433631082)
was followed by an [explicit empty-resource reset](https://github.com/gbaudrit/agentstration/actions/runs/45434039133)
before deploying the corrected exact image. No foreign resources were adopted.

The metadata-creation-to-reset-completion windows were 1,087.2 seconds for SQLite
and 1,040.5 seconds for PostgreSQL. Applying the full fixed hourly baseline to
those entire windows gives approximately EUR 0.0301 and EUR 0.0288 respectively;
this deliberately includes provisioning and preserved/deallocated intervals.
A conservative whole-hour fixed-resource allowance is about EUR 0.10 per trial.
Actual invoice rounding and the excluded storage/transaction/transfer charges
remain separate; these estimates are not measured Azure invoices.

## Offline and CI validation

The optional public-price snapshot needs no Azure login:

```bash
gh workflow run preview-azure-validation.yml --ref main \
  -f retail_prices=true -f pricing_region=westeurope
```

Its `azure-public-retail-prices` artifact retains the selected meter IDs, units,
currency, effective dates and candidate records. Ordinary PR/push checks skip
this online pricing job; the deterministic test suite uses offline meter fixtures.
The collector refuses missing/ambiguous meters and excludes Windows, Spot,
reservations and managed PostgreSQL compute. A regional IP meter can legitimately
have `isPrimaryMeterRegion: false`; that flag alone does not invalidate its rate.

`Preview Azure contracts` compiles Bicep and runs offline ownership/transport
contracts without OIDC or Azure provisioning. When Azure CLI is installed, an
offline boundary check compares generated storage options with its built-in help
using an empty CLI configuration and disabled telemetry; it never authenticates
or accesses Azure resources. For trusted repository branches,
it also calls the reusable entrypoint for all three provider contracts; the
execution jobs remain skipped. Request/result artifacts include the environment
ID so multiple calls in one run do not collide. Server image validation additionally
runs `azure_guest_smoke.py` against the exact built OCI archives for SQLite and
PostgreSQL, covering mirroring, health and preserve/recreate with no registry
credential in the guest. The existing authenticated AWP execution smoke remains
in place. These checks do not substitute for an Azure trial of federation, RBAC,
cloud-init, region/image availability, networking, billing and teardown.
