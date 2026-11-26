# ADR-0278: Task execution evidence projects canonical plan attempts

## Status

Proposed — implementation for [#956](https://github.com/gbaudrit/agentstration/issues/956).

## Context

A stable atomic task may have several explicit execution attempts. The current task result can
change on retry, and global Run enumeration cannot establish complete task history. ADR-0268
already owns durable admission and #129 supplies indexed task-specific Flow history.

## Decision

Canonical WorkPlan attempts retain their existing identities, exact plan/task pins, WorkItem and
root Flow Run coordinates. Terminal observation now captures its result/error on that attempt
before the plan and task transition commit. A later retry cannot erase a previous receipt. Runtime
success and retained evidence continue to leave functional acceptance pending.

Both Work stores maintain a task/owner/Workspace-indexed `WorkTaskAttempts` projection in the same
transaction as the canonical plan change. Reads page that projection directly, never global Runs
or a truncated latest-run list. SQLite initialization backfills existing persisted plans in batches;
the PostgreSQL migration backfills the same attempt coordinates. Existing task-specific Flow Run
queries remain the separate Flow-owned technical history. No additional scheduler, runtime or
execution attempt identity is introduced.

Explicit evidence identifies the exact plan, task and attempt, admission task version/digest,
current observed task version, expected plan version and an idempotency event. Only the current
terminal attempt can acquire evidence; an obsolete retry, stale version or changed immutable pin
fails. Results/artifacts must exist under that same Workspace/task and name that exact Flow Run.
An output from a child Flow is eligible only when an authorized Flow observation proves that same
task, scope and root attempt. References are bounded and cannot grant access to a foreign artifact. Evidence is immutable:
identical replay returns the receipt, while another payload/event conflicts. A plan command receipt,
evidence mutation and history projection commit together behind the existing task/plan CAS guards.

Execution history exposes stable attempt, task, plan revision, WorkItem and Flow coordinates for
later native invocation/usage/delivery correlation. Result and artifact pages use their existing Work
tables with an exact Workspace/task filter and server-side count/pagination. Services check the
authenticated private task before these queries. API artifact descriptors exclude physical storage
keys. Reads do not mark evidence accepted or change task state.

## Consequences

History remains available across restarts, retries and unrelated Run volume. An additive relational
projection and PostgreSQL migration are required. Old attempts without newly captured terminal
snapshots retain their original pins and existing WorkItem/Flow/result/artifact history; missing
snapshots are not fabricated. Attempt pages sort by admission ticks then ID; existing result/artifact
pages sort by stable ID on both stores, without relying on SQLite DateTimeOffset ordering.

This slice adds no browser journey, CLI observation, worker lease transfer or automatic recovery
of a lost AWP worker. ADR-0168 remains unchanged. Functional review continues through the existing
task outcome command. Broader usage accounting and end-to-end delivery remain #1348/#1349.
