# ADR-0005: REST, UI, and MCP share application services

Status: Accepted — 2026-07-31

Transport adapters contain binding and HTTP/MCP response mapping only. Workspace, ingestion, memory, and mission logic is called through the same services. This prevents policy drift and makes transport behavior testable without duplicating the domain workflow.
