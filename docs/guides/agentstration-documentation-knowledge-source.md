# Agentstration documentation Knowledge Source

The optional `agentstration-documentation` Bootstrap profile creates two editable Workspace resources:

- the `agentstration-documentation` Data Source, which acquires `https://docs.agentstration.io/` through the reusable `crawl4ai-web` profile;
- the `agentstration-documentation` Knowledge Source, which projects that Data Source through `knowledge-projection-builtin` and consults its active Snapshot through `knowledge-retrieval-builtin`.

The profile does not create an Agent, Entry, source-specific Tool exposure, or a second acquisition implementation.

## Prerequisites

1. Enable and enroll the Crawl4AI AEP extension with `docs.agentstration.io` in `Crawl4AI:AllowedDomains`.
2. Create the AEP Tool Provider and run Tool discovery.
3. Install the extension's `crawl4ai-web` Data Source Profile bundle in the same Workspace. Automatic bundle installation during enrollment is not implemented yet and is tracked by [#696](https://github.com/gbaudrit/agentstration/issues/696).
4. Ensure Workspace platform reconciliation has provisioned the built-in staged Artifact ToolSet, filesystem Storage Flows, projection Flow, and retrieval Flow.

The documentation site does not need `llms.txt`. Crawl4AI traverses ordinary pages, produces one bounded normalized corpus reference, and the acquisition Flow transfers that reference through governed staging into one durable Artifact.

## Apply the Bootstrap profile

As a Platform administrator, open **System → Bootstrap profiles**, select `agentstration-documentation`, and target the intended Workspace. Preview and apply the profile.

The Data Source references the logical `crawl4ai-web` profile and supplies only origin-specific configuration:

```yaml
definition:
  profile: { name: crawl4ai-web }
  configuration:
    url: https://docs.agentstration.io/
    maximumDepth: 3
    maximumPages: 25
```

The Knowledge Source binds the Data Source and pins the built-in projection and retrieval Flow versions:

```yaml
definition:
  dataSources:
    - name: documentation
      dataSource: { name: agentstration-documentation }
  projectionFlow:
    name: knowledge-projection-builtin
    version: 1.0.0
    useActiveVersion: false
  retrievalFlow:
    name: knowledge-retrieval-builtin
    version: 1.0.0
    useActiveVersion: false
```

## Acquire, project, and consult

1. Open **Data Sources → Agentstration documentation origin** and start an acquisition.
2. Wait for the acquisition Flow Run to succeed and verify that its manifest contains one durable, publishable Artifact.
3. Open **Knowledge Sources → Agentstration documentation** and start a projection.
4. Verify that the successful projection created and activated a new immutable Snapshot containing the Artifact.
5. Open **Consultation** to search, query, or perform a bounded read against the active Snapshot.

Repeat the acquisition when the origin changes, then launch another projection. Each successful projection creates a new immutable Snapshot even when the selected acquisition artifacts are unchanged; the older Snapshot remains historical evidence.

## Failure and recovery

- A failed acquisition does not alter any Knowledge Snapshot. Correct the extension, profile, origin, or bounds, then retry the Data Source acquisition.
- A required Knowledge binding with no acceptable successful acquisition prevents projection. Run or select a successful acquisition first.
- A stale acquisition that exceeds a binding's optional maximum age is rejected before the projection Flow starts.
- A failed transformation or projection leaves the previously active Snapshot unchanged. Correct the Flow or input and launch a new projection.
- Temporary Crawl4AI content expires and is deleted after successful transfer. If it expires before persistence, restart acquisition; do not treat the opaque extension reference as a durable Artifact.
- Search and read remain bounded to the selected Snapshot. An Artifact identifier from another Snapshot is rejected.

The crawl is constrained by both Data Source configuration and operator-owned Crawl4AI ceilings. If the normalized corpus exceeds the configured limit, narrow the crawl or raise the operator bound together with spool capacity. Credentials belong in governed secret references and never in Data Source configuration.
