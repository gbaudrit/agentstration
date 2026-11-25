# Workspace seeds

An installed instance can initialize an **existing Workspace** without a preconfigured Source or
Project connector. Under **System > Bootstrap profiles** (`/settings/bootstrap`), select the Workspace
and supply exactly one ZIP package, raw JSON pointer document, or public HTTPS URL to that JSON.
Inspect the content, resolve local inputs, review prerequisites and the exact preview, then apply.
This uses the ordinary administrative Bootstrap application worker and history.

## Accepted package

Format `1.0` accepts at most 4 MiB compressed, 32 MiB expanded and 8192 entries. Paths, duplicate/case
collisions, links and traversal use Source archive validation. Include exactly one `profile.yaml`,
with `targetScope: workspace`, under the manifest's `path`. The path is relative to the ZIP root.
The package may contain supporting repository content. A root `agentstration-workspace-seed.json`
contains, for example:

```json
{
  "formatVersion": "1.0",
  "version": "1.0.0",
  "providerRevision": "release-1",
  "profile": "local-coding",
  "path": "bootstrap",
  "project": { "name": "shared-project", "namespace": "default" },
  "requirements": {
    "contributions": [
      { "kind": "Tool", "extensionId": "Agentstration.Extensions.Git", "contributionId": "git.repository" }
    ],
    "modelProviders": [{ "name": "installed-model", "scopeRef": "/instance" }],
    "runtimeProfiles": [{ "name": "coding-local", "scopeRef": "/instance" }],
    "runtime": { "name": "coding-cli", "capabilityVersion": "1.0", "materialVersion": "1.0" }
  },
  "importPlans": []
}
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

## Minimal document and HTTP

A JSON pointer is at most 64 KiB:

```json
{
  "formatVersion": "1.0",
  "archiveUrl": "https://example.org/releases/workspace-seed.zip",
  "sha256": "<64 lowercase hexadecimal characters>"
}
```

Only explicit HTTPS port 443 `.json` and `.zip` URLs are accepted. Credentials, query strings,
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
plans or admit work. See [ADR-0271](../decisions/0271-workspace-seeds-reuse-pinned-bootstrap-and-explicit-sharing-handover.md).

The deferred reusable Console journey is #1350. Its maintainer validation/merge gate still applies;
it is separate from implementation behavior checks and desktop/mobile smoke.
