# ADR-0160: Web crawlers produce bounded aggregate content

## Status

Accepted

## Context

A reusable Knowledge ingestion Flow needs one opaque content reference that it can transfer through the governed StagedArtifact and Storage Flow boundaries. Requiring a crawled site to publish a consolidated file such as `llms.txt`, a sitemap-derived export, or another Agentstration-specific representation would make acquisition depend on how each site is authored. Passing every page reference through Flow state would instead couple the generic Flow to provider-specific result shapes and page iteration.

The acquisition boundary must remain bounded. A crawler cannot return an unlimited site corpus or place complete page bodies in ordinary Tool results.

## Decision

The optional Crawl4AI AEP extension owns page aggregation for `web.crawl`. It traverses the approved site with the existing deterministic breadth-first bounds, normalizes each acquired textual page, records the canonical source URL between page sections, and writes the resulting corpus to its temporary content store. The Tool result returns the individual page references for diagnostics and an additional opaque `corpus` reference for downstream composition; it never embeds the corpus body.

The aggregate corpus is subject to the configured content and spool limits. Acquisition fails explicitly when normalization would exceed those limits. A generic ingestion Flow reads the corpus through the separately governed `content.read` Tool, stages it in bounded chunks, invokes the selected Storage Flow, and deletes the temporary corpus reference after persistence.

Knowledge Source configuration supplies only the seed URL and crawl bounds. The published ingestion Flow continues to pin the exact acquisition ToolSet and therefore remains the authority boundary; source data cannot select an ungoverned crawler.

## Consequences

- Sites need no Agentstration-specific consolidated file or authoring convention.
- Provider-specific traversal and aggregation remain outside the Knowledge domain and Flow engine.
- The reusable Flow handles one opaque stream regardless of the number or shape of crawled pages.
- Source URLs remain present in the normalized corpus for retrieval evidence and later processing.
- Large sites must be partitioned, crawled with narrower bounds, or use another acquisition Tool whose configured limits fit the use case.
