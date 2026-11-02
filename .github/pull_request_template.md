## What and why

<!-- What does this change, and what problem does it solve? Link the issue if there is one. -->

## How it was tested

<!-- Tests added or run (e.g. .\build.ps1 test tests\Clementine.Engines.Media.Tests --filter ...),
     real runs with .\build.ps1 cli run ..., screenshots from --snapshot-ui for UI changes. -->

## Checklist

- [ ] `.\build.ps1` builds with no new warnings
- [ ] The affected test projects pass, including `ProviderContractTests`
- [ ] Inputs are never modified; outputs go through the output planner
- [ ] New third-party components are permissively licensed and listed in `THIRD-PARTY-NOTICES.txt`
- [ ] Docs updated if behaviour or requirements changed
- [ ] No personal paths, user names or private sample files in code, tests or screenshots
