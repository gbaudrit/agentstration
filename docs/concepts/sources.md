# Sources

A Source gives publisher-owned reusable content a stable identity in Agentstration. Its portable coordinate is `publisher/name`; Agentstration also assigns an immutable UID for durable references.

Sources and Source Versions are owned by an explicit instance, tenant, or workspace scope and administered through the Management API by Platform administrators. An interactive import defaults to the current workspace unless an explicit scope is supplied. A Source has no repository, branch, active-version property, or Channel content. Those concerns belong to later provider and snapshot layers.

## Published definition

A publisher supplies one Agentstration resource document:

```yaml
apiVersion: agentstration.io/v1
kind: SourceVersion
metadata:
  name: official-samples
definition:
  version: "2"
  displayName: Agentstration official samples
  publisher:
    name: agentstration
  bindings:
    - name: distribution
      targetKind: sourceProvider
  channels:
    - name: stable
      compatibility:
        agentstration:
          minVersion: 0.2.0-alpha.1
      provider:
        binding: distribution
      configuration:
        optionSet: git/source-channel
        version: "1.0"
        schemaDigest: sha256:example
        values: {}
  catalogs: []
```

`definition.version` is an opaque change identifier, not an ordered or Semantic Versioning value. When the complete published definition changes—including Channel removal—the publisher changes this value.

Agentstration retains the exact YAML and a canonical SHA-256 digest. Importing the same version and digest is idempotent. Reusing a version with different content is rejected. A new version creates another immutable Source Version, while every older version remains queryable.

## Import and local state

Platform administrators can import pasted YAML through `POST /api/sources/imports/yaml` or an HTTP(S) URL through `POST /api/sources/imports/url`. A manifest is limited to one MiB and one YAML document. URL retrieval has the same content limit and a finite timeout.

Import requests may carry `scopeRef` (`/instance`, `/tenants/{id}`, or `/workspaces/{id}`). When it is omitted from an interactive request, the current workspace is used. Detail, version, and display-name routes accept the same value as the `scopeRef` query parameter so a homonymous Source is addressed exactly.

The manifest URL is only the definition origin. It is never interpreted as a Channel transport and does not cause Git provider inference.

## Git Channel transport

The `Agentstration.Extensions.Git` AEP extension provides the first Source Channel transport. Its versioned `io.agentstration.git/source-channel` options require a public HTTPS repository and an explicit branch, tag, or commit:

```yaml
configuration:
  optionSet: io.agentstration.git/source-channel
  version: 1.0.0
  schemaDigest: <digest advertised by the extension>
  values:
    repository: https://github.com/example/catalog.git
    ref: refs/heads/main
    rootPath: distribution/stable
```

Branches and tags are resolved to an immutable commit SHA before content is acquired. Materialization fetches that exact commit and returns a bounded ZIP; it never checks out or executes repository code, hooks, submodules, or build scripts. The provider accepts no implicit default branch and supports public repositories only. `credentialsRef` is reserved but rejected until private authentication has a complete local Secret boundary.

The standalone extension requires `git` on `PATH` and can be started with `dotnet run --project src/Agentstration.Extensions.Git`; its default development endpoint is `http://localhost:5290`. `GitSourceProvider:GitExecutable`, `GitSourceProvider:MaximumRepositoryBytes`, and `GitSourceProvider:ResolveTimeoutSeconds` configure its executable and hard limits. Local file repositories remain disabled unless `GitSourceProvider:AllowLocalRepositories=true` is set explicitly for development or offline tests. Aspire starts and registers the extension automatically.

## Local Source Providers

A local `SourceProvider` is an instance-owned Management resource that selects one `source-provider` contribution from an instance-visible `ExtensionRegistration`. The registration remains the only owner of the endpoint, enabled state, expected extension identity, and credentials. Repository, ref, path, locale, and all other transport values remain immutable Channel configuration.

Platform administrators manage these resources under **Configure > Source providers**. The Extensions view links each discovered source-provider contribution to a prefilled creation form. The provider detail view reports the observed contribution status, advertised source-channel option contracts, and every Source binding that references it. A referenced provider cannot be deleted.

The REST surface is `/api/sourceproviders`: list, create, get, update, and delete operations are complemented by `/{providerName}/status` and `/{providerName}/usages`. Writes use ETags. Source binding forms only list already configured providers and never create or select one automatically.

Three concerns remain separate:

- the immutable published Source Version;
- local Source configuration, including the editable display name and optional origin;
- observed import state and immutable import records.

The display name is initialized from the first Source Version default, falling back to `metadata.name`. An administrator edit is preserved by every later import; this increment intentionally provides no reset action.

List Sources with `GET /api/sources`, inspect their versions with `GET /api/sources/{publisher}/{name}/versions?scopeRef=...`, and update the local display name with an ETag-protected `PUT /api/sources/{publisher}/{name}/display-name?scopeRef=...`.

The published `publisher/name` pair is immutable and cannot be changed by refreshing a Source. Renaming that identity requires deleting the local Source and importing the manifest under its new identity. The ETag-protected `DELETE /api/sources/{publisher}/{name}?scopeRef=...` operation removes the Source definition, versions, local configuration, import history, and Channel snapshot metadata. It does not uninstall Packs already installed from the Source, delete their retained provenance, or modify the external registry.

## Optional verification index

Agentstration can consult a static verification index without making it a startup or offline dependency:

```json
{
  "Agentstration": {
    "Sources": {
      "VerificationIndex": {
        "Url": "https://registry.example/verified-sources.yaml",
        "TimeoutSeconds": 10,
        "MaximumBytes": 1048576
      }
    }
  }
}
```

No URL is configured by default. The index is loaded only when an import or verification query asks for it. An unavailable or invalid index reports verification as unavailable but never rolls back an import or invalidates a retained Source Version or snapshot.

Definition verification matches the exact `publisher/name`, opaque Source Version, and canonical manifest digest. The origin URL is not part of trust, so pasted YAML and the same document retrieved from a moving or immutable URL produce the same result. Query it with `GET /api/sources/{publisher}/{name}/versions/{versionUid}/verification?scopeRef=...`.

Snapshot verification is independent and additionally requires the exact Channel, immutable provider revision, and complete snapshot archive digest. Query it with `GET /api/sources/{publisher}/{name}/versions/{versionUid}/channels/{channel}/snapshots/{snapshotUid}/verification?scopeRef=...`. A verified definition alone does not verify Channel content, and a locale or descendant path never establishes trust.

Git Channel acquisition, explicit Source Provider administration and binding selection, durable Channel snapshots, compatibility evaluation, catalog browsing, Bootstrap application, and Pack installation are available through their respective Management APIs and the Console. Source Pack preview and installation retain the exact version, Channel, snapshot, catalog, entry, descendant path, provider revision, and digests while delegating target scope, bindings, replacement, ownership, and uninstall to the existing Pack lifecycle. Periodic refresh remains a separate increment.
