# Workspace seeds

An installed instance can initialize an **existing Workspace** without a preconfigured Source or
Project connector. Under **System > Bootstrap profiles** (`/settings/bootstrap`), open **Remote bootstrapping**
from the page header, select the Workspace and supply exactly one ZIP package, raw YAML pointer
document, or public HTTPS URL to that YAML in the dialog.
Inspect the content, resolve local inputs, review prerequisites and the exact preview, then apply.
This uses the ordinary administrative Bootstrap application worker and history.

The catalog and application history remain visible without an inline ingestion form. Cancelling the
dialog preserves its draft during the current page session. A failed inspection keeps the input and
request identity for retry; changing the input or target starts a new request identity. Successful
inspection closes the dialog and selects the pinned seed for the existing review/application flow.

`RemoteBootstrapDialog` is a reusable Console component. Its host supplies the authorized target
selector, format guidance and inspection callback; the component does not assume Workspace ownership
or perform authorization. Other hosts can compose Instance or Tenant target selection when their
application service and package format support it. The Workspace seed adapter on this page continues
to enforce its Workspace-only contract. No additional scope support is implied by the dialog.

## Accepted package

Format `2.0` accepts at most 4 MiB compressed, 32 MiB expanded and 8192 entries. Paths, duplicate/case
collisions, links and traversal use Source archive validation. Include exactly one `profile.yaml`,
with `targetScope: workspace`, under the manifest's `path`. The path is relative to the ZIP root.
The package may contain supporting repository content. A root `agentstration-workspace-seed.yaml`
contains, for example:

```yaml
formatVersion: '2.0'
version: 1.0.0
providerRevision: release-1
profile: local-coding
path: bootstrap
project:
  name: shared-project
  namespace: default
requirements:
  contributions:
  - kind: Tool
    extensionId: Agentstration.Extensions.Git
    contributionId: git.repository
  modelProviders:
  - name: installed-model
    scopeRef: /instance
  runtimeProfiles:
  - name: coding-local
    scopeRef: /instance
  runtime:
    name: coding-cli
    capabilityVersion: '1.0'
    materialVersion: '1.0'
importPlans: []
```

`version` is semantic version evidence; `providerRevision` identifies the provider's immutable source
revision. Acquisition additionally calculates the archive SHA-256 and exact selected profile digest.
The profile must declare exactly one matching semantic `ProjectSharing` name/namespace. Topology,
access-policy, enrollment, Instance ModelProvider and RuntimeProfile declarations are refused in this
entry path. It does not change support for ordinary Instance/Tenant catalog applications.

Order dependencies before consumers: local Secret metadata/configured inputs, protected relay
ToolDefinitions, ProjectSharing, native configuration, HarnessProfile and Coding Flow. Reuse
[HarnessProfile](harness-profiles.md), [native Coding](coding-worker.md) and
[Project sharing](project-sharing.md) contracts. A ToolDefinition must be an enabled, published relay,
with a semantic source `target` (or an `extensionContribution` selector), version and actual protected
bindings. Authored source UIDs/generations are refused: publication resolves their current identities.
A Secret binding can use `secret: { resourceRef: { name: git-credential } }`; secret values are never
embedded in the package. Existing publication contents cannot be silently replaced.

`ProjectSharing` retains `toolName`/`toolNamespace` for sharing. Optional `repositoryToolName` /
`repositoryToolNamespace` and `taskOperationsToolName` / `taskOperationsToolNamespace` bind the
corresponding local protected publications, such as `agentstration.repository.v1.0.0`. Use the proper
GitHub repository and task-operation contracts for Coding; a Git sharing Tool alone is insufficient.
Optional `codePublicationToolName` / `codePublicationToolNamespace` adds a distinct protected Git
connection with purpose `CodePublication` for [issue delivery](coding-delivery.md).
The sharing `projectId.value` is portable. Its `participantId.value` must identify this participant;
use an explicit ordinary configured input to avoid copying another instance's identity:

```yaml
participantId:
  value:
    valueFrom:
      configuration: Agentstration:Bootstrap:Parameters:ParticipantId
      type: string
```

Supply a fresh stable local participant GUID in the existing Bootstrap input review. This is not a
Workspace ID. Instance-owned providers/runtime profiles, current Workspace Discover/Use grants,
installed enabled extension contributions and a recent compatible `coding-cli` worker registration
must already exist. Worker registration follows its own local CLI/isolation prerequisite check.
No binaries, models, providers or grants are installed by seeding.

## Named connections and local review

A ProjectSharing definition may declare portable `connectionRequirements` (ADR-0291). Select
existing local services with the ordinary profile binding inputs. For example:

```yaml
# bootstrap/profile.yaml, under definition
bindings:
  - name: local-tracking
    targetKind: connectedService
    displayName: Tracking account
    required: true
```

```yaml
# ProjectSharing definition; repository identity is illustrative
connectionRequirements:
  - name: issues
    contract: agentstration.tracking.contracts.mcp.v1
    version: '1.0'
    requiredOperations: [read, update]
    target:
      providerId: <Describe-provided authority>
      targetId: '42'
connectionAssignments:
  - name: issues
    service: { binding: local-tracking }
    describeTool:
      extensionContribution:
        kind: Tool
        extensionId: Agentstration.Extensions.GitHub
        contributionId: github.tracking.describe
    exports:
      - { field: providerId, exportId: providerId }
      - { field: targetId, exportId: targetId }
      - { field: bindingId, exportId: bindingId }
      - { field: revision, exportId: revision }
```

Use the contract identifier, exports and authority returned by the installed compatible adapter;
these technical values are not inferred from an extension name or hostname. ATC and AFC needs
are independent and can select different services. Sharing-only packages require neither.
A semantic `service: { resourceRef: { name: local-account } }` can instead reference a service
created earlier by its owning ConnectedService Bootstrap handler. Order that declaration first.
Do not author service/Tool UIDs, generations, instance IDs or physical Workspace scopes in the
shared ProjectSharing YAML. A local binding selection supplies the authorized scope.

Preview identifies each named need, contract, external target and selected service's logical
administrative scope. Existing compatibility/Describe checks verify the actual service/type,
exports, operations, target and granted access. Their evidence participates in the reviewed
application digest. Replacing or changing a selected resource requires a fresh preview before
configuration writes. Missing or incompatible services never fall back to another account.
Different installations may select different UIDs, scopes and credentials for the same target.

After configuration, the handover pulls shared state and compares its canonical requirements
with the reviewed Project. If absent, it publishes those approved requirements using a stable,
retained operation identity, then pulls again. A mismatch fails the handover without replacing
shared declarations or local assignments. History retains the publication receipt and verified
metadata digest; a retry recovers the same publication before continuing. This does not activate,
allocate or execute a plan, and later sharing pulls never reapply Bootstrap. See
[ADR-0295](../decisions/0295-workspace-seeds-review-local-service-assignments.md).

## Minimal document and HTTP

A YAML pointer is at most 64 KiB:

```yaml
formatVersion: '2.0'
archiveUrl: https://example.org/releases/workspace-seed.zip
sha256: <64 lowercase hexadecimal characters>
```

Only explicit HTTPS port 443 `.yaml`/`.yml` and `.zip` URLs are accepted. Credentials, query strings,
fragments, encoded paths, redirects and private/reserved network destinations are refused. The
existing Source DNS/IP connection policy is reused. Acquisition has one ten-second deadline and
bounded streaming reads. A raw repository root is not a recursive source or directory listing.
A complete package and a pointer to the same bytes select the same profile and logical configuration.

`POST /api/bootstrap/workspace-seeds` accepts `requestId`, explicit `target` (`tenantId`, `workspaceId`),
and exactly one `archiveBase64`, `document` or `documentUrl`. Its private receipt can be reloaded at
`GET /api/bootstrap/workspaces/{workspaceId}/seeds/{seedId}?tenantId=...`. Every lookup requires the
original Workspace and administrator. Reusing the request identity with changed input conflicts.

Pass `seed: { id, archiveDigest }` through existing preview, binding-target, source-file and application
requests with the same explicit target; do not mix it with catalog/Source selections. Preview and
application pin content, target, inputs and stable installed-capability/access evidence. Heartbeats
do not invalidate the digest. Missing prerequisites remain visible and prevent application writes.

## Handover and recovery

After resource configuration, Bootstrap explicitly pulls through the Project connector. Optional
`importPlans` entries contain `planId` and `expectedDigest`; imports remain draft-only and preserve
local changes. Application history retains acquisition provenance, current prerequisite facts,
Project identity, first-sync revision, completed imports and partial/failure diagnostics. The Project
link opens its existing authorized JSON record.

A failed sync retains created resources. Repair the prerequisite/connection, review the same pinned
seed and submit a **new application request**. Existing compatible resources and local Project edits
are preserved; immutable mismatches conflict. Restart interrupts in-flight manual applications;
it does not automatically replay them. Ordinary Work sharing pulls never apply Bootstrap, activate
plans or admit work. See [ADR-0281](../decisions/0281-workspace-seeds-reuse-pinned-bootstrap-and-explicit-sharing-handover.md).

The deferred reusable Console journey is #1350. Its maintainer validation/merge gate still applies;
it is separate from implementation behavior checks and desktop/mobile smoke.

## Explicit Bootstrap file composition

For nested manifests, declare their exact relative paths in the profile descriptor, in dependency
order. For example, `bootstrap/profile.yaml` may contain:

```yaml
apiVersion: agentstration.io/v1
kind: BootstrapProfile
metadata:
  name: local-coding
definition:
  targetScope: workspace
  resources:
    - connections/git.yaml
    - project.yaml
    - harness/native.yaml
    - harness/coder.yaml
    - flows/coding.yaml
```

Only selected resources are applied. Without `resources`, existing flat profiles retain lexical
root-file order; nested YAML is not implicitly applied. Duplicate/missing paths, traversal, nested
profile descriptors and duplicate resource identities are rejected. The source viewer includes the
descriptor and selected nested paths; the complete profile files remain part of its exact content
digest. Bootstrap remains the single definition location for Flows; `data/` contains work declarations
and retained records, never a second Flow definition.

Format 1.0 JSON seeds must be regenerated as format 2.0 YAML packages/pointers. Existing receipts and
historical artifacts remain private retained evidence; the loader does not silently reinterpret an
old package. Sharing independently uses its format-2.0 YAML contract. See
[ADR-0285](../decisions/0285-yaml-seeds-select-explicit-bootstrap-compositions.md).
