# Contributing to Clementine

Thanks for helping. Bug reports, format requests and pull requests are all welcome. For anything bigger than a fix,
open an issue first so we can agree on the shape before you spend time on it.

## Build and test

You need Windows 10/11 x64, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and FFmpeg on your
PATH for the media tests (`winget install Gyan.FFmpeg`). LibreOffice is optional: tests that need it are skipped
when it isn't installed. Edge, which every Windows PC has, is used by a few document tests.

Always go through `build.ps1`. It holds a machine-wide build lock and keeps all output in
`%LOCALAPPDATA%\ClementineBuild`, outside the source tree.

```powershell
.\build.ps1                                          # build the solution
.\build.ps1 test tests\Clementine.Engines.Files.Tests  # one test project
.\build.ps1 test tests\Clementine.Engines.Media.Tests --filter "FullyQualifiedName~Hap"
.\build.ps1 test                                     # everything (~1,200 tests)
.\build.ps1 cli list <file>                          # what the catalog offers for a file
.\build.ps1 cli run <operation-id> --out <dir> <file>  # run one operation headless
.\build.ps1 app --snapshot-ui <dir>                  # render every window and wheel state to PNG
```

Filter to the tests you are working on while iterating; run the whole project before you open a pull request.
CI runs every test project on `windows-latest` with the same FFmpeg build that ships in releases. Pull requests that
only touch documentation skip the build and tests; either way the **CI result** check reports at the end.

## How the code is organised

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) explains the modules, the operation contract, the catalog and wheel
model, the job queue and output rules, and the testing approach. The short version:

- **Engines** (`src/Clementine.Engines.*`) only know `Clementine.Core`. They never reference each other or the UI.
- **Everything is an operation** (`IOperation`) supplied by the engine's single `IOperationProvider`.
- **Outputs go through `request.Output`** (`BeginFile`/`BeginDirectory` → write `WorkingPath` → `Commit()`).
  Never overwrite a file, and never modify, move or delete an input.
- **External programs** are found with `ToolLocator` and run with `ProcessRunner` using argument lists.
- **Expected failures** throw `OperationException` with a short, friendly sentence; technical detail goes in `details`.

## Adding an operation

1. Pick the engine and the category (Convert = Shift wheel, Tool = Ctrl+Shift, Action = Alt+Shift).
2. Give it a stable lower-case dotted id (`video.hap-q.audio`) and labels that fit the wheel: `Group` ≤ 14
   characters, `Variant` ≤ 22, at most 10 variants per group, the recommended variant first (lowest `Order`), an
   `IconKey` from `ProviderContract.IconKeys`, `Weight = Heavy` for slow work.
3. Implement it next to similar operations, register it in the engine's provider, and report it unavailable from
   `CheckAvailability()` when it needs a tool that may be missing.
4. Test it with `TestWorkspace.RunAsync` on a tiny fixture generated in code (no binary samples in the repository).
5. Try it for real with `build.ps1 cli run …`, and check the wheel with `--snapshot-ui`.

The worked example is in [docs/ARCHITECTURE.md › Adding an operation](docs/ARCHITECTURE.md#adding-an-operation).

## Conventions

- C# with nullable reference types, file-scoped namespaces and four-space indents; XAML, project files and YAML use
  two spaces. Files are UTF-8 with LF line endings (`.editorconfig` and `.gitattributes` take care of this).
- Keep classes small and testable; prefer a pure helper plus a thin operation over one large method.
- User-facing text is short and friendly, with no exclamation marks in errors ("This video has no audio track.").
- NuGet packages must be permissively licensed (MIT, Apache-2.0, BSD, MS-PL). No GPL/AGPL libraries, no iText,
  no Ghostscript. GPL tools such as FFmpeg are only ever run as separate programs. Add any new component to
  `src/Clementine.App/THIRD-PARTY-NOTICES.txt`. Bumping the bundled FFmpeg or 7-Zip in `publish.ps1` also means
  pinning the matching source archive in `.github/scripts/release-sources.ps1`.
- Tests must not change the machine: no registry writes outside `HKCU\Software\ClementineTests`, no startup entries,
  Explorer menus or shortcuts, and no files outside the test workspace. Keep fixtures small (≤ 2 s clips, ≤ 640 px).
- Never commit personal paths, user names, e-mail addresses or sample files from your own folders — use names like
  `D:\Clips\loop.mp4` in examples. Check screenshots for paths and file names before adding them.
- Commit metadata is public too. Unless you want your own e-mail address in the history, set
  `git config user.email` to the `…@users.noreply.github.com` address from GitHub's e-mail settings before your
  first commit.

## Pull request checklist

- [ ] `.\build.ps1` builds with no new warnings.
- [ ] The affected test projects pass, including each engine's `ProviderContractTests`.
- [ ] New behaviour has tests; UI changes were checked with `--snapshot-ui`.
- [ ] Inputs are never modified; outputs go through the output planner.
- [ ] New third-party components are permissively licensed and listed in `THIRD-PARTY-NOTICES.txt`.
- [ ] README/ARCHITECTURE updated if behaviour, requirements or conventions changed.

By contributing you agree that your contribution is licensed under the [MIT licence](LICENSE).
