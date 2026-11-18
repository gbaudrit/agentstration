# Connected Service operation discovery 1.0

An extension may advertise `aep.connected-service-operation-discovery` version
`1.0`, endpoint `/aep/v1/connected-service-operations/discover`, in addition to its
Connected Service type and operation type contributions. This is execution of
provider discovery, not business-operation invocation or a second Flow engine.

`POST` accepts `AepConnectedServiceDiscoveryRequest`: `serviceTypeId`,
`serviceTypeVersion`, non-sensitive `options`, `boundValues`, and `correlationId`.
It carries no product resource identities. The host selects this request from an
authorized exact Connected Service and the type's authenticated extension owner.
The SDK endpoint uses the same enrollment authentication policy as other AEP
endpoints, sets `Cache-Control: no-store`, limits the execution envelope to 64 KiB,
and validates Bound Values against the selected version's typed/protected inputs.
Secured inputs reject inline values and use the existing one-use Secret grants.
Do not persist or log execution requests or redeemed buffers.

Register `IAepConnectedServiceOperationDiscovery` implementations in the server
container, keyed by `ServiceTypeId`, and advertise the capability explicitly.
The provider validates options, retrieves bounded external contract metadata and
returns `AepConnectedServiceDiscoveryResponse`. Each operation contains a stable
`externalId`, `displayName`, contributed `typeId`/`typeVersion` and a JSON object
`definition`; diagnostics contain bounded non-sensitive codes/messages. The result
limit is 48 KiB. The host replaces contribution references with its exact local
type UID/version/digest pins and enforces the same bound on the resulting manifest
before generic validation/materialization.

Use `AepClient.DiscoverConnectedServiceOperationsAsync` for bounded authenticated
dispatch and `AepValueResolver` for protected value redemption. Host authorization,
extension transport authentication and remote-system credentials are separate
boundaries. An extension cannot select another product Secret or another
extension. Cancellation revokes outstanding grants; retries issue fresh grants.

The existing resource-bundle installation lifecycle may install a Flow-only bundle
when every manifest is a Flow with contract
`connected-service-operation-discovery/v1` or
`connected-service-operation-acquisition/v1`. The wire container retains its historic
`DataSourceProfileBundles` name; no Data Source Profile is required in this narrow
case. Normal Data Source bundles retain their validation and governance rules.
