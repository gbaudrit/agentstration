# Persistence

Standalone mode deliberately separates logical data stores:

| Boundary | Current local store |
| --- | --- |
| Content and memory vertical | Local JSON by default |
| Management Plane | SQLite control-plane database |
| Runtime Runs and events | Independent SQLite runtime database |
| Work Plane and Workplace projections | Independent SQLite Work database plus local artifact files |
| Flow definitions, versions, drafts, Runs, and events | Independent SQLite Flow database |

Every Workspace-owned query carries its Workspace identifier. Published Flow versions and agent revisions are immutable. Raw ingested source content is preserved and is never overwritten by normalized or generated output.

These stores are implementation details behind provider-neutral ports; no external database is required by default.
