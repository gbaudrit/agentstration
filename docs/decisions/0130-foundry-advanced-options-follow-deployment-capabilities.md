# ADR-0130: Foundry advanced options follow deployment capabilities

Status: Accepted — 2026-09-18

## Context

ADR-0129 added streaming and governed Tool calls. FR-448 adds structured output and explicit reasoning through the same AEP chat contribution. Foundry deployments can differ in supported options, and a model name alone is not evidence of support.

## Decision

- The extension advertises structured output only when deployment discovery affirmatively reports `jsonObject` or `jsonSchema`; it advertises reasoning only for affirmative `reasoning`. An optional bounded `reasoningEfforts` array lists supported explicit effort values. Missing, false or unrecognized metadata grants no capability. Model names are never used to infer support.
- For an advanced request, the extension reads current deployment metadata before inference. It rejects an unavailable deployment, an unadvertised output format, or an unadvertised reasoning effort. A change in deployment metadata therefore takes effect on the next advanced call without persisting a provider model.
- AEP carries `json_object` or bounded `json_schema` response formats, including a strict schema flag, and maps enabled or disabled reasoning to the OpenAI/v1 `reasoning_effort` field. Unknown fields, malformed schemas, unsupported effort values and oversized Tool or schema definitions fail before inference. Text chat, streaming and the governed Tool pipeline retain their existing paths.
- The extension does not parse provider reasoning content, execute Tools, administer Foundry deployments, or guess capabilities from a model family. Offline tests use synthetic deployment metadata; the release gate for a live preview remains FR-449.

## Consequences

Profiles can request advanced options only for deployments that explicitly advertise them. A deployment whose metadata omits these fields remains available for ordinary text chat. Advanced requests incur a fresh bounded discovery call so a changed deployment cannot silently retain stale capability assumptions.
