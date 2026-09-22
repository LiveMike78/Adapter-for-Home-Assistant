# Contributing

Thanks for your interest in improving the AVEVA Adapter for Home Assistant.

## Before you start

Read `README.md`'s "Verify before building" section and `docs/PREP-NOTES.md` first — several
integration points with the AVEVA Adapter Framework are marked as needing confirmation against the
actual restored NuGet packages, and issues/PRs that close those gaps are especially welcome.

## Development

```bash
dotnet restore AdapterForHomeAssistant.sln
dotnet build AdapterForHomeAssistant.sln
dotnet test AdapterForHomeAssistant.sln
```

## Pull requests

- Keep the Home Assistant-specific logic (`src/AdapterFramework.Data.Adapter.HomeAssistant/`)
  decoupled from the framework hosting shell where practical — it's unit tested independently of
  any live HA instance or AVEVA package.
- Add/extend unit tests for new filtering, discovery, or conversion logic.
- Note any AVEVA Adapter Framework API assumptions you had to make in the PR description.
