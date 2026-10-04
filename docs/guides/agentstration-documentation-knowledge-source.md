# Agentstration documentation Knowledge Source

The optional `agentstration-documentation` Bootstrap profile provisions an editable Workspace composition that crawls `https://docs.agentstration.io/`, publishes the resulting Artifact as a Knowledge Snapshot, exposes source-specific retrieval Tools, and assigns their exact ToolSet version to a dedicated documentation assistant.

The documentation site does not need to publish `llms.txt` or another consolidated export. The Crawl4AI `web.crawl` Tool traverses ordinary pages and returns an opaque, bounded normalized corpus reference. The ingestion Flow transfers that reference through the built-in StagedArtifact ToolSet and filesystem Storage Flow.

## Prerequisites

1. Enable and enroll the Crawl4AI AEP extension with `docs.agentstration.io` in `Crawl4AI:AllowedDomains`.
2. Create an AEP Tool Provider from the enrolled extension and run discovery.
3. Ensure the target Workspace has the built-in platform resources. Workspace reconciliation provides the StagedArtifact ToolSet, filesystem Storage Flow, and Knowledge retrieval Flow.
4. Prepare a compatible ModelProfile and RuntimeProfile for the documentation assistant.

## Apply the profile

As a Platform administrator, open **System → Bootstrap profiles**, select `agentstration-documentation`, and target the intended Workspace. Resolve the five required bindings:

- `crawl4ai-crawl` to the discovered `web.crawl` Tool;
- `crawl4ai-read` to the discovered `content.read` Tool;
- `crawl4ai-delete` to the discovered `content.delete` Tool;
- `assistant-model` to the assistant ModelProfile;
- `assistant-runtime` to the assistant RuntimeProfile.

Preview and apply the profile. It creates the acquisition ToolSet, chunk-transfer and ingestion Flows, Knowledge Source, source-specific retrieval ToolSet exposure, documentation Agent, assistant Flow, and Workplace Entry.

## Acquire and publish

Open **Knowledge Sources → Agentstration documentation**, start an acquisition, and wait for its FlowRun to succeed. Publish the successful acquisition artifacts as a Snapshot. The source-specific `search`, `query`, and `read` Tools then resolve the active Snapshot through the standard retrieval Flow.

The crawl is intentionally bounded by the source configuration and the Crawl4AI operator ceilings. If the normalized corpus exceeds the configured content limit, narrow the crawl or raise the operator-owned bound together with the temporary spool capacity. Credentials never belong in the Knowledge Source configuration.
