# ADR-0001: Start with a modular monolith

Status: Accepted — 2026-07-31

One ASP.NET Core process hosts REST, Razor, MCP, workflows, and workers. Module boundaries live in application namespaces and contracts; Domain/Application/Infrastructure/Web remain physical dependency boundaries. This minimizes operational cost and cross-process failure modes while preserving future extraction seams. Microservices are rejected for the first version.
