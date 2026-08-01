# ADR-0004: Standalone scheduler before Quartz

Status: Accepted — 2026-07-31

The MVP uses `BackgroundService`, `PeriodicTimer`, and persisted `NextRunAt`. Quartz is preferred once PostgreSQL is the active store because it adds durable clustering, calendars, and misfire policies. Introducing it before a durable repository would add machinery without improving the single-process guarantee.
