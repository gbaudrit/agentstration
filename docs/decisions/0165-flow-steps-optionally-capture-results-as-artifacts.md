# ADR-0165: Flow steps optionally capture results as governed Artifacts

## Status

Accepted

## Context

Tool implementations may return ordinary JSON, text, or structured results that are useful both to the remainder of a Flow and as retained content. Requiring every Tool to know about the Artifact broker couples reusable Tools to a particular workflow concern. Introducing a dedicated Artifact step would instead force authors to add plumbing nodes and duplicate the provenance already known by the executor.

Agent, governed Tool, Tool-route, child-Flow, repeat and Flow-output steps already have a successful result boundary. The executor knows the Workspace, Flow Run, root and parent Run, step name, attempt and correlation identity at that boundary.

## Decision

An eligible executable Flow step may declare an optional `artifactOutput` clause. Its presence asks the Flow executor to capture either the complete successful step result or an explicitly mapped projection as a governed staged Artifact.

The clause contains an optional file name, a media type, an optional `contentMapping`, an optional byte limit, an optional staging binding, a content encoding, an optional `storageFlow` and a `clean` policy. When no mapping is supplied, the complete step result is captured. JSON-compatible media types retain JSON serialization; text uses UTF-8 and binary content uses an explicit base64 declaration. The file name and media type may be static or resolved from the current step context. `${step.name}` identifies the current step, `${step.displayName}` resolves its display label with a fallback to `name`, and `${step.output...}` selects the current raw result without repeating the authored step name. Explicit `${steps.<name>.output...}` references remain supported. When `fileName` is omitted, the capture adapter uses the step `name` and an extension inferred from the declared media type. MCP-aligned `content` and `structuredContent` output normalization is intentionally deferred to #737.

When `storageFlow` is declared, the executor invokes that published Flow after staging with the standard `artifact.storage.write/v1` input: the staged Artifact identity and authoritative producer Flow Run and step identities. The parent suspends through the normal durable child-Flow mechanism, resumes without re-executing the producing step, and replaces the staged reference on the step run with the returned durable `flowRunArtifactId`. A storage failure fails the requested capture rather than silently degrading it to temporary content.

The `clean` policy accepts `auto`, `true`, or `false`. `auto` is the default and removes the local staged copy at terminal Flow completion, whether or not a `storageFlow` is configured. `true` explicitly fixes the same cleanup guarantee independently of future automatic-policy evolution, while `false` retains the copy until the normal retention policy expires it. Terminal completion includes success, failure, cancellation and timeout. Cleanup is delayed until the parent Flow stops so downstream steps can still consume the local copy.

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
    fileName: ${step.name}
    mediaType: application/json
    contentMapping: ${step.output}
    maximumBytes: 1048576
    clean: auto
    storageFlow:
      resourceId: artifact-storage-filesystem-write-builtin
      namespace: default
      versionStrategy: active
```

## Consequences

- Reusable Agent, Tool and child-Flow implementations remain independent from Artifact semantics.
- Flow authors opt in next to the step whose result they want to retain, without adding graph plumbing.
- Artifact policy, Workspace isolation, retention and storage routing remain centralized.
- Durable capture composes the configured Storage Flow without adding a visible graph step or re-executing the producer.
- Local-copy cleanup is explicit, runs only at terminal Flow completion, and defaults to removing staged content whether or not durable storage is configured.
- A capture failure is observable as a Flow failure and can be retried with the same run/step/attempt identity.
- The initial authoring surface is the existing Flow manifest/API. A dedicated visual editor can be added separately without changing the contract.
