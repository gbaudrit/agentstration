# Working on the documentation

Documentation is stored as Markdown/MDX under `docs/`. Docusaurus configuration, theme files, generated output, and npm dependencies live under `docs/site/`. Do not copy product pages into `docs/site`.

## Run locally

```powershell
cd docs/site
npm install
npm start
```

Docusaurus starts its development server at `http://localhost:3000` by default. For a CI-equivalent check:

```powershell
cd docs/site
npm ci
npm run build
```

The production build fails on unresolved internal links and invalid Mermaid diagrams. Generated folders (`node_modules`, `.docusaurus`, and `build`) are ignored by Git.

## Authoring rules

- Link to the source of truth instead of copying a full route/schema or contract.
- Use Mermaid only when a relationship or execution sequence is clearer as a diagram.
- Mark unavailable behavior as **Planned**, **Experimental**, **Preview**, or **Not implemented yet**.
- Keep conceptual pages independent of C# class names; put implementation details in Architecture or Reference.
- Update the sidebar when adding a new top-level page that should be discoverable.

## Versioning

The current documentation line is **Next**. Do not create Docusaurus snapshots for `0.x` releases. Versioned major documentation will be enabled after a stable `1.0` release; see the [versioning strategy](../reference/versioning.md).

## Publication

The site is configured for `https://docs.agentstration.io`. The validation workflow runs on documentation pull requests. The publication workflow is manual until GitHub Pages is enabled for GitHub Actions and the custom-domain DNS is configured. It requires no repository secret beyond GitHub's standard Pages token.
