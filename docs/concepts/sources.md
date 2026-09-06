# Sources

A Source gives publisher-owned reusable content a stable identity in Agentstration. Its portable coordinate is `publisher/name`; Agentstration also assigns an immutable UID for durable references.

Sources and Source Versions are instance-scoped and administered through the Management API by Platform administrators. A Source has no repository, branch, active-version property, or Channel content. Those concerns belong to later provider and snapshot layers.

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

The manifest URL is only the definition origin. It is never interpreted as a Channel transport and does not cause Git provider inference.

Three concerns remain separate:

- the immutable published Source Version;
- local Source configuration, including the editable display name and optional origin;
- observed import state and immutable import records.

The display name is initialized from the first Source Version default, falling back to `metadata.name`. An administrator edit is preserved by every later import; this increment intentionally provides no reset action.

List Sources with `GET /api/sources`, inspect their versions with `GET /api/sources/{publisher}/{name}/versions`, and update the local display name with an ETag-protected `PUT /api/sources/{publisher}/{name}/display-name`.

Channel materialization, Source Provider selection, compatibility evaluation, periodic refresh, catalog browsing, and Bootstrap/Pack consumption are introduced by the related Source feature increments.
