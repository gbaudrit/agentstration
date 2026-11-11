# Transform Artifact content in a Flow

The `artifactTransform` step processes an ordered list of Artifacts. The existing
`transform` step continues to map JSON values without loading stored files.

In the Designer, add **Transform artifact(s)**. Connect the ordinary input to the
step, connect its **process** output to the processing steps, and return their
successful result to the bottom **Processing result** port. Connect **completed**
to the next outer step and **error** to an error output. Processing may include
Agents, Tools, child Flows, conditions and mappings. It does not require another
published Flow or transformation step.

## Content mode

Use **Content (text or JSON)** for a processor that accepts ordinary values.
`artifactsMapping` selects an Artifact array or an object with an `artifacts`
array. Each item identifies a sealed staged or immutable durable Artifact. Bounded
inline manifest content is supported as an exact staged input copy.

The processing input envelope contains `content`, `artifact`, `parameters`,
`inputAccess` and `outputAccess`. `content` is a string for UTF-8 text and a JSON
value for JSON media. Saved outer transformation parameters are available through
`input.parameters`. Configure the step's processing input mapping to adapt this
envelope to an existing Tool, without changing that Tool definition:

```json
{
  "text": "${input.content}",
  "language": "fr"
}
```

A processing Tool then receives `{ "text": "...", "language": "fr" }`. If it returns
`{ "redacted": "..." }`, set the owning step's result mapping to
`"${input.redacted}"`. Empty result mapping captures the entire successful result.
An Agent can return text directly. JSON output must be valid JSON; Markdown code
fences are not parsed. Empty output name/media type preserves the input values.

Content mode defaults to 65,536 input bytes; its configurable maximum is 1 MiB.
Oversized content fails explicitly. PDF, images and other binary formats require
an explicit extraction processor; reading Storage bytes is not text extraction.

## Access mode and large content

Use **Scoped input/output access** when processing chooses what to read. Its input
contains metadata and logical access descriptors, with no whole-document content.
The descriptors identify this execution context; they are not bearer credentials,
public download URLs or arbitrary Storage access.

Agents in that context automatically receive these invocation-only Tools:

| Tool | Behavior |
| --- | --- |
| `input_info` | Input name, media type, size and range unit |
| `input_read` | Bounded read using an offset or continuation cursor |
| `input_search_regex` | Regex matches with readable ranges and continuation |
| `output_info` | Current output revision, size and completion state |
| `output_write` | Append text or replace a selected range |
| `output_read` | Read back the current output revision |
| `output_search_regex` | Search the output for verification and corrections |
| `output_complete` | Declare a selected revision ready for finalization |

Ordinary Tools named `artifact.processing.<operation>` expose the same operations
to authored Tool steps. They use the trusted processing Flow context. Calls from
an unrelated or terminated execution fail; a Tool argument cannot select another
Artifact. Existing generic Tools remain usable in content mode, and a processor
that understands scoped access can use these operations in its processing context.
A Flow called inside processing inherits that context without another transform.

`input_read` and `output_read` accept `offset`, `length` or `cursor`. Positions and
regex match ranges use **UTF-16 character offsets**, not byte offsets. Reads default
to 4,096 characters and are bounded to 16,384; surrogate pairs remain intact.
Cursors stay attached to the exact input, or to one output revision. An output
cursor becomes invalid after an edit. A new acquisition cannot redirect input.

`output_write` requires `content`, `expectedRevision` and a unique `requestId`.
Omit `offset` to append. Supply `offset` and `deleteLength` to replace a range.
Retry a write with the same identifier and arguments; a changed replay fails.
The initial revision is zero. Reads and searches do not modify it. Complete with
`output_complete({ "expectedRevision": 3 })` after checking revision 3. A subsequent
correction creates a new revision and requires completion again.

The Agent decides whether to read first or search first and how to cover the
complete document. Example instructions for anonymization:

> Inspect the input, process its complete content using bounded reads, and write
> the anonymized output progressively. Preserve structure. Read/search the output
> to check for remaining personal data, correct it if necessary, then complete the
> latest output revision. Do not return the whole document in your final message.

These Tools restrict the access they provide. Other Tools already assigned to an
Agent continue to use their own grants; choosing an Agent with broader access
does not revoke those grants.

## Output, failures and limits

Each successful input produces one new durable Artifact. Originals remain intact.
All processing regions must succeed and all outputs must be prepared before final
registration begins. The owning step persists the outputs and returns an ordinary
publishable Artifact manifest. Receipts link the producing Flow/step, processing
child execution, exact input digest and actual Storage materialization. The existing
Artifact durable backend uses the selected local default where available.

The step defaults to 32 input Artifacts and permits at most 128. Access input is
bounded to 16 MiB; output defaults to 8 MiB and permits at most 16 MiB, with at most
128 writes. Staging and Storage policies can impose smaller limits. Regex patterns
are limited to 1,024 characters, searches return at most 50 matches and have a
100 ms regex timeout. Contexts expire after one hour. Superseded or abandoned
staged revisions follow existing staging retention and cleanup.

A failed processor, incomplete output, invalid JSON, stale revision, foreign cursor,
unsupported media or oversized content prevents a successful step result. No
original-content fallback is implicit. Source publication still owns activation of
the resulting Snapshot. Storage/catalog failure during finalization can leave
orphan outputs; the operation does not promise a distributed transaction.

See [ADR-0213](../decisions/0213-artifact-transformations-own-scoped-content-processing.md)
and [source composition](../architecture/implementation/data-source-composition.md).
