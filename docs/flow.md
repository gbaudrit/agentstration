# Flow definitions and execution

This legacy page is superseded by [Runtime — Flow execution](architecture/runtime/flow-execution.md), the [dynamic architecture views](architecture/runtime/dynamic-views.md), and [Flow modes](concepts/flow-modes.md).

## Transform process regions

A Transform may optionally declare `processing`; the default remains an ordinary mapping:

```yaml
- type: transform
  name: fragments
  mapping: "${transition.output}"
  processing:
    partition: TextChunks
    unitSize: 2048
    maximumUnits: 32
    maximumConcurrency: 2
```

`Batches` partitions an array into batches of `unitSize` elements. `TextChunks`
partitions a string by UTF-16 characters without splitting surrogate pairs. The
executor rejects mismatched input types and more than `maximumUnits` (1–128).
`unitSize` is 1–65,536; `maximumConcurrency` is 1–16, default 1. The mapped value is
the partition input. Each unit becomes the process chain's `input`; connect the
step's `process` event to its first processor and return the final result to the
owning step's result port. `completed` continues with an array ordered by unit
ordinal, including an empty array for empty input. `error` handles a unit failure.
The Transform's output schema validates the aggregate, separately from individual
producer contracts. Worker and assignment limits can further restrict parallelism.
