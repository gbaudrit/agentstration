# ADR-0088 — Source verification binds exact definitions and snapshots

## Status

Accepted

## Context

A publisher name and a hosting domain are declarations, not evidence. Agentstration needs to recognize definitions published through an optional official catalog while remaining fully usable offline. The immutable Source Version manifest and the mutable Channel selector are also different trust boundaries: verifying one cannot verify content later resolved from the other.

## Decision

- An administrator may configure one optional bounded static index through `Agentstration:Sources:VerificationIndex:Url`. No index is configured by default, and the index is never loaded during startup.
- The index is an Agentstration `kind: VerifiedSourceIndex` envelope. Each entry binds the exact Source `publisher/name`, opaque published version, canonical manifest SHA-256 digest, publisher metadata, manifest locations, and explicit evidence. Locations may include both a moving URL and immutable versioned URLs; locations do not participate in matching and never establish trust by themselves.
- Definition status is `verified` only when identity, version, and canonical manifest digest all match one entry. A pasted manifest and the same bytes retrieved by URL therefore receive the same result. Changed content is unverified until its new digest is listed independently.
- Channel content evidence is optional and separate. It binds the verified definition to one exact Channel name, resolved revision, and complete snapshot archive digest. Definition verification never promotes a Channel Snapshot, repository, domain, locale, or descendant path to verified.
- Index retrieval is lazy, HTTPS-only including after redirects, bounded to one MiB, timed out, and strict. An absent index returns `unverified`; a retrieval or validation failure returns `unavailable`. Neither state invalidates an import, a retained Source Version, or an existing snapshot.
- The import result and dedicated definition/snapshot verification endpoints expose declared publisher metadata separately from matched evidence. The integrated Console presentation remains owned by the Source administration increment.

The initial index shape is:

```yaml
apiVersion: agentstration.io/v1
kind: VerifiedSourceIndex
metadata:
  name: official
definition:
  sources:
    - source:
        publisher: agentstration
        name: bootstrap-samples
      version: "1"
      manifestDigest: sha256:<canonical-manifest-digest>
      publisher:
        name: agentstration
        displayName: Agentstration
        url: https://agentstration.io
      manifestLocations:
        - url: https://example.invalid/latest/source.yaml
          mutable: true
        - url: https://example.invalid/versions/1/source.yaml
          mutable: false
      evidence:
        type: official-static-index
        authority: agentstration
      channels:
        - name: latest
          revision: <immutable-provider-revision>
          snapshotDigest: sha256:<complete-archive-digest>
          evidence:
            type: official-snapshot
            authority: agentstration
```

## Consequences

The official index remains an optional trust anchor instead of a registry runtime dependency. Definition and snapshot status can change as the configured index changes without rewriting immutable local resources. A localized variant is covered only through the exact verified snapshot containing it; changing any catalog declaration or descendant file changes the complete archive digest and loses the match.
