# Contributing

Thank you for contributing to Agentstration. Open an issue before a large change, keep branches and pull requests focused, and inspect the affected vertical before introducing a new abstraction.

## Development checks

Use MSTest for behavior changes and keep tests deterministic and offline. Before submitting a pull request, run:

```powershell
dotnet restore Agentstration.slnx
dotnet build Agentstration.slnx --configuration Release --no-restore
dotnet test Agentstration.slnx --configuration Release --no-build
```

If documentation changed, also run:

```powershell
cd docs/site
npm ci
npm run build
```

Never commit secrets, personal data, generated data stores, or real document contents used for testing.

## Documentation policy

Every change that introduces or modifies a public concept, resource, API, configuration setting, or architecture decision must include the corresponding documentation update. Documentation is reviewed and versioned with the code it describes.

Keep the root README concise. Put durable product documentation under `docs/`; `docs/site` contains only the Docusaurus renderer and must not contain a second copy of the content. See [Working on the documentation](docs/contributing/documentation.md).

Use explicit labels such as **Planned**, **Experimental**, **Preview**, or **Not implemented yet**. Do not describe a roadmap item as an available capability.

## Pull request checklist

### Documentation

- [ ] No documentation change is required
- [ ] User documentation updated
- [ ] API documentation updated
- [ ] Architecture documentation updated
- [ ] ADR added or updated

Select every applicable item and explain why no documentation change is required when choosing the first item.
