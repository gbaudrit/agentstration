# Flow execution

A submitted Entry is resolved to an exact immutable Flow version. Work creates functional state; the Flow engine creates a durable FlowRun; Agent steps invoke Runtime; results and events are projected back into Work and Workplace. The canonical behavioral scenarios are [Submit Work](dynamic-views.md#submit-work), [Execute Flow](dynamic-views.md#execute-flow), and [Execute Tool](dynamic-views.md#execute-tool); the [interactive L3 view](../interactive.mdx) owns the structural relationships.

Draft Runs retain the exact draft revision, definition hash, and immutable snapshot. Published Runs resolve an immutable semantic Flow version. Continuations create a new FlowRun and link it to the terminal predecessor.

## Execution context in expressions

Flow mappings and conditions can read a bounded execution context in addition to `input`, `steps.*`, and `transition.output`:

| Expression | Value |
| --- | --- |
| `${execution.flowRunId}` | Current persisted FlowRun identity. |
| `${execution.rootFlowRunId}` | Root identity for the current FlowRun tree; equal to `flowRunId` for a root Run. |
| `${execution.parentFlowRunId}` | Direct parent identity, or JSON `null` for a root Run. |
| `${execution.stepName}` | Name of the step currently evaluating the expression. |
| `${execution.correlationId}` | Durable correlation identity of the FlowRun, or JSON `null` when absent. |

These values are server-owned and cannot be supplied through the Flow input. For example, a producer can preserve Artifact lineage when calling a Storage Flow:

```yaml
inputMapping:
  stagedArtifactId: "${steps.seal.output.artifactId}"
  producerFlowRunId: "${execution.flowRunId}"
  producerFlowStepId: persist
```

The same values are reconstructed from durable FlowRun state after a restart. See ADR-0160.
