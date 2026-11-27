# ADR-0272: Preview publication promotes selective CI archives

Status: Proposed — 2026-11-27. Owning feature: [#1286](https://github.com/gbaudrit/agentstration/issues/1286).

## Context

ADR-0271's automatic delivery invokes OCI validation again after PR CI already
builds images. CI images identify GitHub's merge commit while the delivery build
identifies the PR head, preventing direct artifact reuse. The preview selector
also binds its requested revision to the server image and always selects the
latest published extensions, even when an extension is the subject of the PR.

The requested behavior is uniform for server, Worker and selected AEP extensions:
publish CI images for affected components and reuse existing private images for
unaffected components. An extension-only preview must not require a new server
OCI delivery image.

## Decision

Keep catalogue-driven CI selection from #1426. Preview labels never force an
additional server build. PR OCI validation checks out the exact PR head; product
builds and functional checks retain their merge-commit checkout. Common build or
protocol inputs still select every affected image. Existing source-built
authority/Worker fixtures used by integration smokes are retained; they are test
infrastructure, not additional OCI delivery artifacts.

Admit automatic publication from trusted main after PR workflow completions,
late opt-in/extension labels or explicit retry. Inventory the latest attempt of
each workflow for the same repository, PR and exact head. Require successful CI
and completed successful or intentionally skipped parallel PR workflows. A test
keeps the completion-listener names aligned with independent PR workflows.
Forks, drafts, closed PRs, obsolete heads and unverified human opt-ins cannot
publish. Per-component `preview/publication/<id>` commit statuses expose waiting,
reuse and publication directly in the PR without granting deployment permission.

The successful CI image jobs define the affected components. For required
components in that set, dispatch the existing OCI workflow in archive-promotion
mode with a verified CI run and attempt. Its build job is always skipped. Bind
the artifact to that run's latest successful component validation, including a
previous successful image job when only failed jobs were rerun. Download only
the immutable artifact ID using read-only Actions access. Revalidate CI, live
approval, component selection, receipt, local checksum, OCI index digest and
image labels before registry mutation. The publisher executes main tooling and
never candidate scripts. Existing matching private images are reusable; missing
or expired archives require CI validation again, never a publication-side build.

Only requested components may be promoted, into their fixed server, Worker or
AEP namespaces. Packages must already be private and grant repository Actions
Write access. Publish by immutable source tag and preserve all digests and
attestations. Record the component, source CI run/attempt and artifact ID with
the publication receipt. Manual validation/release flows retain their existing
explicit build path; automatic promotion has no Docker Hub path.

For unaffected required components, resolve the newest tagged private package
version with the existing registry selector. Ignore untagged attestation
manifests and validate each selected image against its own source catalogue,
platform and version. This policy also applies to the server. Freeze every
result by digest in the deployment request; unchanged running intent is not
redeployed just because another image is published.

Version 4 of the external deployment contract separates the requested PR SHA
from individual image build SHAs. It retains canonical public receipts and
requires exact SHAs/digests. Versions 1/2 keep their original server-SHA invariant
and remain readable. Guest-local state continues to identify its actual server
image. Version 3 is reserved for the parallel GUID-profile work in #1422; merging
that work must retain the independent-source policy when composing profiles with
version 4. Feedback shows requested revision and per-component build identities.
PR commands and automatic reconciliation share this selection policy; explicit
non-PR manual revision selection remains available.

After promotion, a trusted metadata reporter requests existing main
reconciliation without cloud or registry-write credentials. If all required
components are reused, admission can request reconciliation directly. Selection
waits for any affected component's exact PR image before allocating/resetting
resources. Runtime gains no deployment or GitHub dependency.

## Consequences

This replaces ADR-0271's extra automatic build and server-only publication
policy. Existing PRs need to synchronize main before their CI archives carry the
required head identity. Missing unchanged images require initial publication;
missing changed images cannot fall back to another revision. Requested and image
source identities can differ, and remain inspectable/reproducible in the frozen
request. No PAT, cloud permission, new service or mandatory inference is added.
Live cross-run promotion requires qualification after merge.
