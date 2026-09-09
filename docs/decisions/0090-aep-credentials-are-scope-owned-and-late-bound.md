# ADR-0090: AEP credentials are scope-owned and late-bound

Status: Accepted — 2026-09-09

## Context

Extension Registrations could reference a Secret, but the AEP adapter did not consume that reference. Persisting a clear token in an endpoint, provider option, resolved profile, or shared HTTP header would cross resource and extension boundaries and would make rotation ineffective until configuration was rewritten.

ADR-0079 and ADR-0080 now define hierarchical ownership more precisely than the earlier workspace-only extension design: manually registered extensions and Model Providers are tenant-owned, configuration/Aspire registrations are instance-owned, and Secrets can be owned by instance, tenant, or workspace scopes.

## Decision

An Extension Registration declares transport authentication independently from enrollment. The initial modes are `none` and `staticBearer`. `staticBearer` requires a Secret reference; `none` rejects one so a configured credential is never silently ignored.

The AEP adapter resolves the Secret immediately before every discovery, configuration, model, chat, streaming, or Source Provider HTTP request. Resolution uses the Extension Registration's actual namespace and ownership scope as the consumer context. The ordinary resource visibility rule applies: a credential may be in that scope or an ancestor, while sibling and descendant scopes are inaccessible. This refines the original workspace wording of FR-175 without weakening its isolation goal.

The resolved value is decoded as a single-line UTF-8 Bearer token, applied only to that request, and its owned byte buffer is disposed immediately. It is never copied into Management resources, model/profile options, Packs, telemetry, audit payloads, or `HttpClient.DefaultRequestHeaders`. A missing, inaccessible, malformed, or unsupported credential fails before the HTTP request is sent. Resolution on every request makes Secret value rotation and removal effective immediately.

The AEP client also verifies the expected extension ID centrally during discovery. Every functional model and Source Provider call performs discovery first, so an identity mismatch fails before the functional request. Disabled registrations are rejected before invocation.

Native provider credentials and MCP server credentials remain separate mechanisms and are not forwarded to AEP.

## Consequences

- Each extension request is authenticated with the credential owned by its registration context.
- Rotation does not rewrite Model Providers or Model Profiles.
- Secret resolution adds one scoped lookup per outbound request.
- Clear credential strings exist only for the lifetime required by the .NET HTTP authorization header; owned Secret buffers are zeroed through `SecretValue.Dispose`.
- Enrollment can later provision or rotate the referenced Secret without changing the transport authentication contract.
