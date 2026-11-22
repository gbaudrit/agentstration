# ADR-0242: Acquisition text content is selected at admission

## Status

Proposed

## Context

The explicit `json.select /content` sequence in ADR-0241 exposes a transport detail
for the common case of a text acquisition followed by a text transformation.
The maintainer requested direct selection of the first function in #1214 / PR #1218.
JSON invocation contracts and native content contracts must remain distinct.

## Decision

At composition admission, only the first function at a point directly consuming the
governed `acquisition` point may receive its text content automatically. The
acquisition point must contain one platform-owned direct Flow call. Its effective
output contract must be an object with exactly two required string properties,
`mediaType` and `content`, and `additionalProperties: false`. Merely naming a business
property `content` does not qualify. The function must expect a string as its single
pipeline value, either directly or through its `value` invocation property. Other
literal configuration parameters remain independent of that value.

A governed Operation output schema can refine a compatible generic acquisition
Flow output contract. An incompatible explicit Flow contract is not overridden.
The existing raw acquisition validator still validates that exact Operation
contract before exposing the result to transformations.

The compiler records `functionInputSource` (`acquisitionContent` or `previousResult`)
in each accepted function call and compiles the selected field into the ordinary
invocation mapping. The executable hash therefore includes the selection. Runtime
uses the accepted selection and exact Function/Flow references; it does not inspect
payloads to choose an adapter. Missing source fields, invalid XML, invalid function
results and limits still fail through existing validators. Accepted receipts
without this field retain the previous-result behavior. A changed sequence is an
explicit new acceptance. A forged content selector outside the eligible boundary
is rejected, including when it targets a later function.

Object/array-valued functions and all later transformations keep the previous
result. Existing explicit JSON selections remain valid. Knowledge transformations
and ordinary Flow transforms keep their existing invocation mappings. The shared
sequence editor explains automatic content selection only for the first text
function and a qualifying acquisition contract.

This supersedes ADR-0241's requirement to manually select `/content` for this narrow
case. It does not change Schema exposure, Bootstrap procedure, provider identities,
authorization or processing limits, and introduces no RSS-specific product code.

## Consequences

The common acquisition-to-text-function sequence needs one selected function.
Selection remains inspectable and reproducible, and cannot silently change an
accepted composition. Other response shapes require explicit JSON selection.
Tests cover admission, frozen replay, ordinary-object rejection, function order,
whole-response functions, old receipts and executable XML-to-JSON publication.
The reusable UX scenario #1215 remains gated on explicit maintainer UX validation
and implementation merge; #1214 remains open.
