# Connected Service operation acquisition 1.0

An operation type version can declare `acquisition`: `flowName`, `flowVersion`,
`inputSchemaProperty`, `outputSchemaProperty`, and `readOnlyProperty`. Property
names default to `inputSchema`, `outputSchema` and `readOnly`. The named published
Flow is contributed in the same extension namespace through the existing resource
bundle lifecycle. The host binds its exact local identity, version and hash;
providers do not supply product resource UIDs. Only request-response types can
advertise acquisition. The host still validates read-only eligibility per operation.

Advertise `aep.connected-service-operation-acquisition` version `1.0` at
`/aep/v1/connected-service-operations/acquire`. Register
`IAepConnectedServiceOperationAcquisition`, keyed by `OperationTypeId`. This is an
optional acquisition-specific execution boundary; it does not publish a Tool or
implement generic relay bindings.

`AepConnectedServiceAcquisitionRequest` contains `serviceTypeId`,
`serviceTypeVersion`, `options`, `boundValues`, `operationTypeId`,
`operationTypeVersion`, the exact saved operation `definition`, validated
`parameters`, and `correlationId`. The host derives all connection and operation
authority from an accepted acquisition, rechecking its exact resource references,
Flow Run, caller, logical scope, operational Workspace and current authorization.
Connection exports and caller overrides are not taken from ordinary Flow arguments.

The SDK applies enrollment transport authentication, `Cache-Control: no-store`,
a 128 KiB request bound and the selected service type's Bound Values validation.
Secured inputs require existing one-use Secret grants. Execution envelopes,
redeemed bytes and grants must never be persisted in Flow state, events or logs.
Retries issue fresh grants; cancellation revokes outstanding grants.

The provider owns its protocol, exact selector verification, effective schemas,
remote limits and connection-origin restrictions. Return
`AepConnectedServiceAcquisitionResponse` with one JSON `result`, within a 48 KiB
serialized envelope. Failures use bounded safe AEP codes/messages. The host
validates the result against the pinned output schema and publishes it through
its existing Storage gateway; the extension cannot select or widen the Storage.

Use `AepClient.AcquireConnectedServiceOperationAsync` for bounded authenticated
dispatch. AEP enrollment authenticates the host/extension transport; product
resource authorization and downstream remote credentials remain separate checks.
