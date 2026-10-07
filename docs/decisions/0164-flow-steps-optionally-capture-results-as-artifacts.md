# ADR-0164: Flow steps optionally capture results as governed Artifacts

## Status

Accepted

## Context

Tool implementations may return ordinary JSON, text, or structured results that are useful both to the remainder of a Flow and as retained content. Requiring every Tool to know about the Artifact broker couples reusable Tools to a particular workflow concern. Introducing a dedicated Artifact step would instead force authors to add plumbing nodes and duplicate the provenance already known by the executor.

Agent, governed Tool, Tool-route, child-Flow, repeat and Flow-output steps already have a successful result boundary. The executor knows the Workspace, Flow Run, root and parent Run, step name, attempt and correlation identity at that boundary.

## Decision

An eligible executable Flow step may declare an optional `artifactOutput` clause. Its presence asks the Flow executor to capture either the complete successful step result or an explicitly mapped projection as a governed staged Artifact.

The clause contains an optional file name, a media type, an optional `contentMapping`, an optional byte limit, an optional staging binding and a content encoding. When no mapping is supplied, the complete step result is captured. JSON-compatible media types retain JSON serialization; text uses UTF-8 and binary content uses an explicit base64 declaration.

Capture is an execution side effect, not a new Flow step kind. The original result remains the authoritative step output for transitions, expressions and the final Flow output. A successful capture is recorded on the `FlowStepRun` as an Artifact reference.

The Flow application owns a provider-neutral capture port. Infrastructure implements it through the existing Artifact management boundary, selected or default staging binding, size limits, retention, authorization and audit behavior. Provenance identifies the producing Flow Run and step plus the exact Agent revision, Tool generation/provider or child Flow version when applicable. The key `flow-step:{flowRunId}:{stepName}:{attempt}` makes recovery reuse the same staged Artifact instead of creating a duplicate.

Capture occurs only for successful eligible steps. A requested capture that cannot be completed fails the Flow execution; a Flow must not report the step as successful while silently losing a requested Artifact.

For example, a Tool step can preserve a projected body as text while keeping its complete result available to the rest of the Flow:

```yaml
- type: tool
  name: fetch
  tool:
    name: http-get
  artifactOutput:
    fileName: response.html
    mediaType: text/html
    contentEncoding: utf8
    contentMapping: ${steps.fetch.output.body}
    maximumBytes: 1048576
```

## Consequences

- Reusable Agent, Tool and child-Flow implementations remain independent from Artifact semantics.
- Flow authors opt in next to the step whose result they want to retain, without adding graph plumbing.
- Artifact policy, Workspace isolation, retention and storage routing remain centralized.
- A capture failure is observable as a Flow failure and can be retried with the same run/step/attempt identity.
- The initial authoring surface is the existing Flow manifest/API. A dedicated visual editor can be added separately without changing the contract.
