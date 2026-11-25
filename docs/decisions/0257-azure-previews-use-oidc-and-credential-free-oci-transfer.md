# ADR-0257: Azure previews use OIDC and credential-free OCI transfer

Status: Accepted — 2026-11-24

## Context

[#1283](https://github.com/gbaudrit/agentstration/issues/1283) adds Azure execution
to ADR-0256's external preview contract. Private GHCR images must retain their
exact index digests, and neither application workloads nor VM bootstrap may
receive a reusable registry or Azure credential. Public administrative ingress
and subscription-wide deployment privileges are unnecessary.

## Decision

Keep the portable request and existing Docker lifecycle. Add an external Bicep
VM adapter in a pre-created, dedicated preview resource group. Pin the Ubuntu
platform image version explicitly. Use Standard_B2ms as the configurable default
for the qualified lightweight preview baseline and allow Standard_B4ms; larger
workloads still require their own measurements. Attach one managed data disk containing Docker volumes and private
host state. SQLite remains the default; PostgreSQL reuses the existing overlay.

Use GitHub OIDC for a protected main workflow and a group-scoped custom VM
operator role. Grant Blob Data Contributor only on a dedicated staging account;
disable shared-key authentication and anonymous blob access. The operator cannot
create role assignments, assign user-assigned identities or operate unrelated
resource kinds. The template sets VM identity to None; the adapter checks the
observed identity, VM size, location and OS version before transferring workloads. Deny all inbound ports; a public IP provides explicit
outbound connectivity without adding a NAT gateway or Bastion.

CI exports the selected GHCR indexes with `skopeo --all --preserve-digests`.
It uploads a private temporary bundle and supplies a 30-minute, HTTPS, single-blob,
read-only user-delegation URL through Managed Run Command **protectedParameters**.
The URL never enters Bicep, cloud-init, public command parameters or exported logs.
The guest verifies the bundle hash, then copies its indexes to a host-loopback
registry before calling the owned Docker lifecycle. GHCR credentials remain on CI.
The download URL is removed from the guest command environment before workloads
start; root-only agent files are outside all container mounts. Delete the staged
blob and Run Command resource after execution. The public infrastructure registry
and PostgreSQL images also use pinned digests. This does not publish to Docker Hub.

Bind ownership to a private state blob, random owner ID, repository, environment
and deterministic ARM IDs. Serialize operations with an exclusive blob lease as
well as Actions concurrency. An interrupted runner can leave an infinite lease;
explicit recovery after checking the original run is safer than concurrent ARM
mutations. Refuse foreign resources and unverified orphan OS disks.

Preserve-destroy stops the application and deallocates the VM, retaining disks,
identity files and networking. Explicit reset deletes inspected owned ARM resources;
Azure deletes the VM's OS disk with its parent. Shared bootstrap infrastructure
is retained. Do not return a public application URL before #1285 verifies Access.
TTL enforcement remains #1286. The adapter records measured startup duration,
available guest memory and Azure CPU/credit metrics when available. Resolve public
GitHub protected-environment OS/image/size defaults once into the canonical request; later status/destroy
operations use the recorded selection. The only qualified OS profile is Ubuntu
24.04; legacy v1 requests without an explicit profile retain that meaning.

## Consequences

Azure is an optional deployment transport, with no Runtime or image-package
references to Azure. Shared bootstrap resources and scoped role assignments are
one-time prerequisites. Preserved disks and the public IP remain billable; reset
is required for complete per-preview teardown. Local contract/IaC checks and a
real Docker guest smoke run without Azure credentials. The
[live qualification and public-rate assumptions](../contributing/preview-azure.md#live-qualification--2026-11-24)
record successful SQLite/PostgreSQL deploy, preserve/recreate and full reset on
B2ms, with exact digests and unchanged expiry. Point-in-time memory/CPU-credit
measurements qualify the functional preview baseline; they are not a sustained-load
SLA or a promise that B2ms fits arbitrary extensions or local inference. Public
PAYG estimates are not subscription invoices. Independent status checks account
for delayed ARM inventory convergence after deletion. Automatic TTL enforcement
and protected public access remain their owning follow-ups.
