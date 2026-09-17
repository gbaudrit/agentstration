# ADR 0120: Resource ChangeSet application is durable and ordered

## Status

Accepted

## Context

Resource Plans produce reviewable ChangeSets. A successful verification checks a proposal against the current Workspace state, but applying several resources cannot be one transaction across Management, Flow, and Work storage. An application can fail or be interrupted after some canonical writes succeeded.

## Decision

- An application is bound to the plan revision, saved ChangeSet digest, and latest successful verification ID. Each ChangeSet has one durable application record in its Workspace.
- Operations run in dependency order through the existing Agent, Flow, and Entry services. Scope permission, profile evidence, and current resource version are checked again immediately before each operation.
- The attempt records each operation outcome and stops on the first failure. Successful operations remain; there is no automatic rollback across stores.
- A retry resumes recorded progress. An operation whose canonical write finished before its outcome was recorded is recognized from current state. Flow and Entry publication can resume from a matching unpublished intermediate state.
- Storage uses a concurrency token and a time bounded lease to prevent simultaneous attempts on one ChangeSet. An interrupted attempt becomes eligible for retry after the lease expires.

## Consequences

The Console can show partial success and allow an operator to resume it. Other writers may still change a target between a state read and a canonical write; canonical services must enforce their own concurrency checks. The bounded lease prevents immediate recovery of an interrupted operation until it expires.
