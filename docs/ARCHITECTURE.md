# Clementine architecture

This is the map for contributors: how the pieces fit, the rules every operation follows, and where to look when
you want to add or change something. For building and testing, see [CONTRIBUTING.md](../CONTRIBUTING.md).

- [The big picture](#the-big-picture)
- [Projects](#projects)
- [Core contracts](#core-contracts)
- [The operation catalog and the wheel](#the-operation-catalog-and-the-wheel)
- [Jobs and outputs](#jobs-and-outputs)
- [Engines and what they depend on](#engines-and-what-they-depend-on)
- [The shell (Clementine.exe)](#the-shell-clementineexe)
- [Bundled and external tools](#bundled-and-external-tools)
- [Testing](#testing)
- [Adding an operation](#adding-an-operation)
- [Design language](#design-language)

## The big picture

```
 Explorer / any app                          Clementine.exe (tray app, WPF)
 ──────────────────                          ──────────────────────────────────────────────────────────
 Shift-drag files ──► Clementine.Wheel ──► OperationCatalog.ForInputs(files, category)
                      (global drag hook,      │  (every IOperation from every engine + the shell's actions)
                       catcher, radial wheel) ▼
                                           IOperationLauncher (UiLauncher: options dialog, remembered values)
                                              │
                                              ▼
                                           JobQueue ── heavy pool / light pool ──► IOperation.RunAsync
                                              │                                      │  writes through
                                              ▼                                      ▼  OutputPlanner
                                           toasts, main window, CLI            result saved next to the original
```

Everything is an **operation** (`IOperation`): "convert this MP4 to HAP Alpha", "trim", "copy path". Four engine
assemblies each expose one `IOperationProvider`; the shell adds a fifth for file actions. The catalog collects them,
the wheel and the main window ask the catalog what fits the selection, and the job queue runs the chosen operation in
the background. The headless `clementine-cli` uses exactly the same catalog and queue, which is how most engine
behaviour is exercised outside the UI.

## Projects

| Project | What it is | Depends on |
|---|---|---|
| `src/Clementine.Core` | Contracts and shared services: operations, catalog, job queue, output planner, option schemas, settings, tool locator, process runner, logging. No UI. | .NET only |
| `src/Clementine.Engines.Media` | Video and audio through FFmpeg/ffprobe: conversions (incl. DXV, HAP, ProRes), tools, probing, encoder planning. | Core, FFmpeg (external program) |
| `src/Clementine.Engines.Images` | Images through Magick.NET, including PSD/PSB composites and layer export, RAW, HEIC, SVG; QR/barcode reading. | Core, Magick.NET, ZXing.Net |
| `src/Clementine.Engines.Documents` | PDF tools, built-in Word/Excel/CSV converters, Office/OpenDocument through LibreOffice, Markdown, HTML, EPUB, Apple iWork previews. | Core, PDFsharp, PdfPig, PDFtoImage/PDFium, SkiaSharp, Open XML SDK, Markdig, AngleSharp; Edge and LibreOffice (external) |
| `src/Clementine.Engines.Files` | Archives, checksums, subtitles, JSON/YAML/CSV/XML, text encodings. | Core, SharpCompress, YamlDotNet, System.IO.Hashing; 7-Zip (external) |
| `src/Clementine.Wheel` | Global drag detection, the catcher window, the radial wheel, the selection hotkey. WPF class library. | Core |
| `src/Clementine.App` | `Clementine.exe`: tray icon, main window, options dialogs, toasts, settings, shelf, file actions, Explorer integration, Velopack hooks and updates from GitHub Releases. | everything above, Velopack |
| `src/Clementine.Cli` | `clementine-cli.exe`: `list`, `run`, `tools`. Headless, no settings are written. | Core and the four engines |
| `tests/Clementine.TestSupport` | `TestWorkspace`, `MediaFixtures`, `ProviderContract` shared by the test projects. | Core |
| `tests/Clementine.<Project>.Tests` | One xUnit project per module. | the module under test |

Engines never reference each other or any UI project. The wheel references only Core. The app is the only place where
everything meets.

## Core contracts

All of these live in `src/Clementine.Core`; read the source, it is short and documented.

### IOperationProvider

```csharp
public interface IOperationProvider
{
    string Name { get; }
    IEnumerable<IOperation> CreateOperations(EngineContext context);
}
```

Each engine has exactly one: `MediaOperationProvider`, `ImageOperationProvider`, `DocumentOperationProvider`,
`FilesOperationProvider`. The shell adds `ActionsOperationProvider`. `EngineContext` hands every operation the
`ToolLocator`, the current `ClementineSettings` snapshot and an `ILog`.

### IOperation

| Member | Contract |
|---|---|
| `Info` | Static description (`OperationInfo`, below). |
| `InputMode` | `PerFile` (one job per input) or `Combined` (one job gets every input: merge PDFs, join clips, zip). |
| `Options` | An `OptionSchema`, or `null` to run immediately. The app renders it as a dialog; the CLI takes the same keys as `--set key=value`. |
| `Accepts(InputFile)` | Fast and extension-based. **Never opens the file** (it runs on every drag, for every operation). |
| `AcceptsAll(files)` | Whether the whole selection fits (counts for combined operations, mixes of kinds). |
| `CheckAvailability()` | `Availability.Unavailable("Requires Microsoft Edge")` when a tool is missing. The wheel shows the segment dimmed with the reason. Must not throw. |
| `GetDynamicDefaultsAsync` | Option defaults that depend on the inputs (trim end = clip duration). May probe files. |
| `RunAsync(request, progress, ct)` | Does the work. See the rules below. |

`OperationBase` provides sensible defaults (`MinInputs`/`MaxInputs` for combined operations, `AcceptsAll` = every
file accepted) and `DelegateOperation` builds families of near-identical variants from delegates.

### OperationInfo

| Field | Rule |
|---|---|
| `Id` | Lower-case dotted, stable forever once shipped: `video.prores.4444.audio`, `image.png`, `pdf.merge`. Remembered option values and the CLI key off it. |
| `Category` | `Convert` (Shift wheel), `Tool` (Ctrl+Shift wheel), `Action` (Alt+Shift wheel). |
| `Group` | Inner-ring label, ≤ 14 characters: the target format for conversions ("ProRes", "PNG"), the tool name for tools ("Trim"). |
| `Variant` | Outer-ring label, ≤ 22 characters. Required whenever a group shows more than one operation for the same kind of file. At most 10 per group. The recommended variant gets the lowest `Order`. |
| `Title` | Full name for toasts, lists and the CLI ("ProRes 4444 · alpha · with audio"). |
| `Description` | One helpful sentence for the wheel centre and tooltips. Be honest about trade-offs ("No alpha — use HAP Alpha or ProRes 4444 for transparency."). |
| `IconKey` | One of `ProviderContract.IconKeys` in `tests/Clementine.TestSupport/ProviderContract.cs`; the wheel and the main window map it to a glyph. |
| `OutputExtension` | Lower-case with a leading dot, or `null` for folders/text results. |
| `Order` | Lower sorts first. Everyday formats 10–90, pro/VJ 100–199, niche 200–399, last resort 400+. A group sorts by its lowest member. |
| `Weight` | `Heavy` for anything that encodes video, runs LibreOffice or Edge, or can take more than ~10 s. |

`ProviderContract.Validate(provider, context)` checks the id pattern, label lengths, icon keys, extensions, option
schema defaults, variant labels and the 10-variant limit. Every engine's `ProviderContractTests` calls it.

### OperationRequest, results and errors

- `request.Inputs` / `request.Input`, `request.Options` (already merged with defaults), `request.Output` (the
  `OutputPlanner`), `request.Context`, and `request.TempDirectory` — a private scratch folder that is deleted after
  the run.
- Return `OperationResult.FromOutputs(paths…)`, `OperationResult.FromText(title, text)` for reports (media info,
  checksums, QR contents — the UI offers Copy and View), plus optional `Warnings` for non-fatal issues
  ("Source has no transparency — the alpha channel is fully opaque.").
- Throw `OperationException("Friendly sentence.", details: stderrTail)` for expected failures: no audio track, wrong
  password, corrupt file, tool missing. The message is shown to the user as-is; `details` goes behind "Details" and
  into the log. Anything else that escapes is treated as a bug ("Something went wrong: …") and logged with a stack.
- Report `JobProgress(fraction 0..1, "Encoding…")` often; a `null` fraction means indeterminate. The queue throttles
  UI updates, so reporting a lot is fine.
- Pass the `CancellationToken` everywhere. `ProcessRunner` kills the whole process tree on cancel.

### Running external programs

`ProcessRunner.RunAsync(exe, argumentList, options, ct)` takes an **argument list**, never a joined string, so paths
with spaces, quotes and Unicode just work. It hides the console window, streams stdout/stderr lines to callbacks, keeps
the tail of stderr for error details, supports timeouts, and puts children in a job object so they die with
Clementine. FFmpeg calls always add `-nostdin -hide_banner`.

`ToolLocator` finds the programs (`Ffmpeg`, `Ffprobe`, `SevenZip`, `LibreOffice`, `Edge`). Never hard-code a path.

### Settings and paths

`ClementineSettings` is a plain JSON-serialised snapshot (`%APPDATA%\Clementine\settings.json`) owned by
`SettingsStore`, which replaces rather than mutates it on every update. Logs go to `%LOCALAPPDATA%\Clementine\logs`,
job scratch folders to `%TEMP%\Clementine`. Setting `CLEMENTINE_DATA_DIR` redirects settings and logs (tests do this).

## The operation catalog and the wheel

`OperationCatalog` holds every operation by id (a provider that throws, or a duplicate id, is logged and skipped —
never fatal). The two queries everything uses:

- `ForInputs(files, category)` — operations whose `AcceptsAll` is true for the whole selection, sorted by `Order`
  then `Title`. A mixed selection only shows what every file accepts.
- `OperationCatalog.Group(operations)` — folds them into `OperationGroup`s by `Group` name. A group's first operation
  is its default.

The wheel model follows directly:

- **Inner ring** = groups. Dropping on an inner segment runs the group's default (first) operation.
- **Outer ring** = the hovered group's variants, fanned around its angle (up to 10). Dropping there runs that variant.
- **Centre** = cancel, and the place where the hovered operation's title and description (or the reason it is
  unavailable) are shown. Unavailable operations are dimmed and can't be dropped on.
- More than 16 groups are split into pages with a trailing **More** segment (`WheelPager`).
- The same catalog queries feed the main window's Convert / Tools / Actions tabs (groups as tiles, variants as chips).

### How a drag becomes a wheel (Clementine.Wheel)

1. `DragMonitor` installs a `WH_MOUSE_LL` hook on a dedicated background thread with its own message loop (a busy UI
   thread would make Windows silently drop the hook) and feeds a pure state machine, `DragDetector`: button down →
   moved beyond twice the system drag threshold → drag candidate → modifiers match a wheel (exact match against
   `ConvertModifiers` / `ToolsModifiers` / `ActionsModifiers`).
2. On a match an almost invisible **catcher** window (alpha 1/255, topmost, no-activate) follows the cursor. Only when
   an OLE drag carrying files enters it do we know files are being dragged; anything else (text, browser images,
   window moves) is ignored and the catcher steps aside.
3. The wheel window opens at that point, follows the OLE drag (`DragOver`), switches mode live when the modifiers
   change, and on drop calls `IOperationLauncher.LaunchAsync`.

**Drop effects are always `Copy` or `None`, never `Move`.** Shift normally means "move" in Explorer, and accepting a
move could make Explorer delete the originals. Every handler is exception-safe: a bug in the wheel must never break
the user's drag.

The same wheel also opens in **click mode** (`IWheelPresenter.ShowForFiles`): for the selection hotkey
(Ctrl+Shift+Space reads the selection of the foreground Explorer window through `Shell.Application`), the Explorer
context menu, Send To and `Clementine.exe --wheel`. Click mode takes focus and supports mouse, arrow keys, Tab, Enter
and Esc.

## Jobs and outputs

### JobQueue

- `Enqueue(operation, inputs, options)` creates one job per file for `PerFile` operations and one job for `Combined`
  ones, merging the options with the schema defaults.
- **Two pools**: `Heavy` operations share a small pool sized by `Settings.MaxConcurrentHeavyJobs` (default 2, read
  live, so changing it applies to queued jobs); `Light` ones share a pool of `ProcessorCount` (2–16).
- Each run gets a private temp folder (`%TEMP%\Clementine\<job id>`) that is deleted before the job is reported
  finished, even on failure or cancellation.
- States: `Queued → Running → Succeeded | Failed | Canceled`. `JobAdded`/`JobChanged` events fire on worker threads
  (progress throttled to ~10 per second per job); UIs marshal them.
- `OperationException` → `Failed` with the friendly message; cancellation → `Canceled`; anything else → `Failed` with
  "Something went wrong" and a logged stack.

### OutputPlanner

All output goes through the planner; operations never pick paths themselves.

```csharp
using var output = request.Output.BeginFile(input, ".mov", suffix: null);   // or BeginDirectory(input, "PNG pages")
// … write to output.WorkingPath …
return OperationResult.FromOutputs(output.Commit());
```

- **Where**: next to the original, or the custom folder from Settings › Output. If that folder isn't writable the
  output falls back to `Documents\Clementine`.
- **Name**: `<input base name>[ (suffix)]<extension>`. "clip.mp4" → "clip.mov". Pass a short suffix when the
  extension doesn't change or the content does: "clip (trimmed).mp4", "photo (50%).jpg", "clip (H.265).mp4".
  Combined outputs are named after the first input ("clip (joined).mp4", "report (merged).pdf").
- **Never overwrites**: an existing name becomes "name (2).ext", and names are reserved process-wide so parallel jobs
  can't collide. Invalid file-name characters are replaced.
- **Atomic**: you write to a hidden-ish `.<name>.clementine-xxxxxxxx.partial.<ext>` in the destination folder (same
  extension, so FFmpeg picks the right muxer). `Commit()` renames it into place; disposing without committing deletes
  it, so a cancelled or failed job never leaves a half-written file.
- **Inputs are sacred**: never modify, move or delete an input. The only exception is the shell's explicit
  "Move to…" action.

## Engines and what they depend on

| Engine | In-process libraries | External programs | Highlights |
|---|---|---|---|
| Media | — | **FFmpeg + ffprobe** (bundled in releases) | `FfprobeService` (JSON → `MediaInfo`, cached per path/size/mtime), `FfmpegRunner` (`-progress pipe:1`, friendly error mapping), `VideoEncodePlanner`/`AudioEncodePlanner` (codec, pixel format, filters, audio per target), hardware H.264/H.265 (NVENC → Quick Sync → AMF after a probe encode, software fallback). |
| Images | Magick.NET (Q16 HDRI, native ImageMagick with its delegates), ZXing.Net | — | PSD/PSB composite with layer fallback, layer export, CMYK → sRGB, EXIF orientation, metadata/location scrubbing, target-size compression, images → PDF. |
| Documents | PDFsharp 6, PdfPig, PDFtoImage (PDFium) + SkiaSharp, Open XML SDK, Markdig, AngleSharp | **Edge** (part of Windows), **LibreOffice** (optional) | All PDF tools are native. Markdown/HTML/EPUB → PDF print through headless Edge with a throw-away profile and a locked-down page (no scripts, all network requests sent to a dead proxy). Word (DOCX) and Excel (XLSX)/CSV → PDF and HTML are built in: Clementine's own converters write HTML (`OpenXml/Word`, `Spreadsheets/SheetHtml`) and Edge prints it. When LibreOffice is installed, `Office/BuiltInOffice` prefers it for those PDFs (closest layout) and falls back to the built-in converter, with a warning, if LibreOffice fails. PowerPoint → PDF/PNG and legacy/OpenDocument formats need LibreOffice, run with a private profile, one conversion at a time. DOCX/PPTX → text and XLSX ↔ CSV are native. |
| Files | SharpCompress, System.IO.Compression/Formats.Tar, YamlDotNet, System.IO.Hashing | **7-Zip** (bundled `7za.exe`) for writing .7z and as a fallback extractor | Zip-slip protection, symlink entries skipped, expansion-ratio limits, password archives, subtitle parsers/writers (SRT/VTT/ASS/SSA/SBV), encoding detection and mojibake repair. |
| App actions | — | Explorer, Windows Terminal/PowerShell | Copy path(s), Copy to…, Move to…, Show in folder, Terminal here, Open with…, Shelf, Rename…, Duplicate. |

### Video for VJs

The pro/VJ codecs are first-class and have the strictest rules (see `Catalog/VideoConversions.cs` and
`Encoders/VideoEncodePlanner.cs`):

- **DXV**: Resolume DXV3 Normal Quality (DXT1) in MOV. FFmpeg can't encode DXV alpha or High Quality, so the
  description points to HAP Alpha / ProRes 4444 for transparency.
- **HAP**: HAP, HAP Alpha and HAP Q (snappy). HAP and DXV sizes are rounded to the 4×4 blocks the encoders need.
- **ProRes** (`prores_ks`, vendor `apl0`): 422 HQ, 4444 (alpha), 422, 422 LT, 422 Proxy, 4444 XQ (alpha).
- **Audio**: "with audio" variants write PCM at `Settings.VjAudioSampleRate` (48 kHz): 16-bit for DXV/HAP (downmixed
  to stereo), 24-bit for ProRes. "No audio" variants write video only.
- **Alpha**: alpha variants keep source alpha (VP9-alpha WebM is decoded with libvpx so the alpha survives); with an
  opaque source they still write an alpha channel and add a warning. Non-alpha targets composite over black.
- **Frame rate and colour**: VFR sources become CFR (`Settings.ForceConstantFrameRate`), interlaced sources are
  deinterlaced (`bwdif`), YUV → RGB uses the source matrix or BT.709/BT.601 by height, and HDR is tone-mapped for
  everything except ProRes (which keeps it, with a warning).
- Tests verify codec tags (`DXD3`, `Hap1`/`Hap5`/`HapY`, `apch`/`ap4h`/`apcn`/`apcs`/`apco`/`ap4x`), pixel formats,
  alpha, audio and a clean decode for every VJ output.

## The shell (Clementine.exe)

- **Startup**: `Program.Main` enforces a single instance per user (named mutex + named pipe). A second launch forwards
  its arguments and exits. The primary instance listens on the pipe immediately, builds the engines and catalog on a
  worker thread (`GuardedProvider` keeps one broken engine from stopping the app) and shows the tray icon meanwhile.
- **Command line**: none → main window; `--background` → tray only (used by "launch at login"); `--wheel <files…>`
  (with `--mode convert|tools|actions`, `--tools`, `--actions`) → click-mode wheel at the cursor, aggregating requests
  that arrive within a few hundred ms (Explorer starts one process per selected file); `--open <files…>`;
  `--settings`; `--quit`; `--unregister` (removes everything the app registered, for portable copies);
  `--snapshot-ui <dir>` → render every window and wheel state to PNG and exit; `--make-icon <dir>`.
- **UiLauncher** (`IOperationLauncher`): checks availability, shows the options dialog generated from the schema
  (pre-filled: schema defaults → remembered values → dynamic defaults; passwords are never remembered), queues the
  jobs and starts a toast card.
- **Toasts**: stacked cards bottom-right with progress, cancel, "Show in folder"/"Open", text results with Copy/View,
  warnings, and failures with Details. Batch drops collapse into one card.
- **Settings**: General (start with Windows, sounds, notifications, automatic update checks), Wheel (modifiers with
  conflict detection, selection hotkey), Output, Performance, Tools (status — "Built in · FFmpeg 8.1.2" for the
  bundled copies under `<app>\tools`, LibreOffice "Optional — for PowerPoint and older Office files" — and path
  overrides), Explorer (context menu under `HKCU\Software\Classes`, Send To shortcut), About (version, MIT licence
  and project link, updates, the bundled programs, libraries and external programs with their licences). Registry
  and shortcut changes happen only when the user flips a toggle.
- **Installer integration**: Velopack installs per user (packId `ClementineApp`, so `%LOCALAPPDATA%\ClementineApp`)
  and runs Clementine's hooks first thing in `Program.Main` (`Startup/VelopackHooks.cs`, `Preferences/UserIntegration.cs`):
  uninstall removes every registration, and the after-update hook rewrites the enabled ones for the new exe path.
  Velopack is only started for a Velopack install (Update.exe in the folder above the exe) or a `--veloapp-*` hook
  launch, never for a development build. Auto-apply on startup is off: a downloaded update is installed only when the
  user picks "Restart to update". Velopack's `OnRestarted` hook makes the first launch of a new version show
  "Updated to Clementine x.y.z".
- **Updates** (`Updates/`): `<ClementineUpdateRepo>` in `Directory.Build.props` is compiled into the app as assembly
  metadata (`ClementineUpdateRepo`, read and validated by `UpdateSetup`; empty = no update source). `Updater` is the
  state machine — Unavailable (not a Velopack install, or no repository) · Idle · Checking · UpToDate · Downloading ·
  Ready · Failed · Restarting — behind a small `IUpdateBackend` interface; `VelopackUpdateBackend` implements it with Velopack's
  `UpdateManager` and `GithubSource` (no token, stable releases only), and tests use a fake. The shell starts the
  schedule after startup: a first check after 60 s, then every 6 h, each only while `Settings.CheckForUpdates` is on
  (read before every check). A newer release is downloaded in the background; at Ready the shell shows a card with
  **Restart to update** (once per version) and a tray item "Restart to update (x.y.z)". Settings › About has
  **Check for updates** (it runs even with automatic checks off) and the status line. Restarting reuses Quit:
  confirm when jobs run, `Updater.ApplyAfterExitAsync` starts Velopack's updater (`WaitExitThenApplyUpdates`, which
  waits for this process to exit), then the quit drain cancels the jobs and waits for their cleanup, and the app exits;
  Velopack installs the new version and starts it with `--background`. From the moment applying starts, the updater
  starts no new check (a newer download would let Velopack clean out the package being installed) and stops a
  running one before it can download; once the installer waits, the schedule ends and the status stays Restarting,
  which turns off the About page's buttons and the tray item. Everything runs on worker threads; every
  failure (offline, rate limit, no releases yet, a malformed feed, a failed download) is logged, and only a check the
  user asked for shows it ("Couldn't reach GitHub — try again later"). No telemetry, no other network access.
  For end-to-end tests of DEBUG builds only, `CLEMENTINE_UPDATE_FEED=<folder>` replaces GitHub with a local Velopack
  feed (`vpk pack` output) and `CLEMENTINE_UPDATE_FIRST_CHECK_SECONDS` shortens the first delay; neither exists in
  Release builds.
- Global exception handlers log and show a friendly error; a failing job never takes the app down.

## Bundled and external tools

| Tool | How Clementine gets it | Used for | If missing |
|---|---|---|---|
| FFmpeg 8.1.2 (gyan.dev "full_build-shared", GPLv3) | Bundled by `publish.ps1` in `tools\ffmpeg\bin`, downloaded from a pinned URL and checked against a pinned SHA-256. For development: settings override → `tools\ffmpeg` → PATH → winget/scoop/choco. | Every video and audio operation | Media operations fail with "FFmpeg isn't installed…" |
| 7-Zip 25.01 `7za.exe` (LGPL-2.1, with BSD-3-Clause and BSD-2-Clause parts) | Bundled by `publish.ps1` in `tools\7zip`, pinned URL and SHA-256. Falls back to an installed 7-Zip. | Writing .7z; fallback extractor | 7Z targets are shown as unavailable |
| Microsoft Edge | Part of Windows | Markdown/HTML/EPUB → PDF; printing the built-in Word and Excel/CSV conversions to PDF | Those conversions are unavailable (Word/Excel → PDF still work when LibreOffice is installed) |
| LibreOffice | Optional, installed by the user | PowerPoint → PDF/PNG, legacy and OpenDocument formats, conversions between Office formats; preferred for Word/Excel → PDF when present | Those conversions are dimmed with "Needs LibreOffice (free, optional)…" in the wheel; Word/Excel → PDF use the built-in converters |

FFmpeg and 7-Zip are separate programs that Clementine starts as processes; they are never linked. Their licences, the
FFmpeg build notes and source pointers (`FFmpeg-LICENSE.txt`, `FFmpeg-BUILD-README.txt`, `FFmpeg-SOURCE.txt`,
`7-Zip-License.txt`) are written to `licenses\` in the published app (see `publish.ps1`), next to
`Magick.NET-Notice.txt`, and the Release workflow attaches the source archives of the exact bundled versions to every
GitHub release (`.github/scripts/release-sources.ps1`, pinned URL and SHA-256 per version; a release fails if the
version `publish.ps1` bundles isn't pinned there). The release stays a draft (invisible to the update check) until
the installer copies and both source archives are attached; only then is it published. The external libraries
compiled into the FFmpeg DLLs are covered by a three-year written offer of their source in `FFmpeg-SOURCE.txt`,
`THIRD-PARTY-NOTICES.txt` and the README. The app names the bundled versions in `Preferences/Licences.cs`
(`BundledFfmpegVersion`, `BundledSevenZipVersion`) for the About page and `THIRD-PARTY-NOTICES.txt`; a test fails
when they drift from the URLs pinned in `publish.ps1`, so bump all three together. Settings › Tools treats a tool as
built in when the located path is under `<app>\tools` (`ToolStatus.IsUnder`); an override chosen there still wins.

## Testing

- One xUnit project per module (`tests/Clementine.<Project>.Tests`), ~1,200 tests in total.
- `TestWorkspace` gives each test a throw-away folder under `%TEMP%\ClementineTests`, in-memory settings, an
  `EngineContext`, an `OutputPlanner`, a `RecordingLog` (assert on warnings) and `RunAsync(operation, paths, options)`,
  which runs an operation exactly the way the job queue does. `CLEMENTINE_KEEP_TEST_FILES=1` keeps the folders.
- `MediaFixtures` generates tiny clips, tones and stills with FFmpeg's built-in test sources and inspects results with
  ffprobe (`ProbeAsync`, `DecodesCleanlyAsync`). Fixtures for documents, archives, PSDs and so on are built in code —
  the repository contains no binary sample files.
- **External tools**: Media tests (and a few image/app tests) need FFmpeg on PATH. Tests that need LibreOffice or Edge
  use `[LibreOfficeFact]` / `[EdgeFact]` and are skipped, not failed, where the tool is missing.
- **Opt-in tests**: `CLEMENTINE_E2E=1` (moves the real mouse and holds Shift), `CLEMENTINE_VISIBLE=1` (briefly shows
  the wheel), `CLEMENTINE_REAL_CATALOG=1` (checks the wheel against the real engines after building the app).
- Tests never change the machine: registry code is tested against a throw-away key under
  `HKCU\Software\ClementineTests`, and settings/logs are redirected with `CLEMENTINE_DATA_DIR`.
- UI work is reviewed with `build.ps1 app --snapshot-ui <dir>`, which renders every window, dialog, toast and wheel
  state to PNG. Look at the PNGs.
- CI (`.github/workflows/ci.yml`) builds the solution and runs every test project on `windows-latest` with the same
  pinned FFmpeg build that ships in releases. Pull requests that only change documentation skip the build and tests;
  the final **CI result** job always reports, so it is the check to require in branch protection. The Release
  workflow calls the same CI workflow on the tagged commit and publishes nothing if it fails.

## Adding an operation

1. **Pick the engine** by what does the work (FFmpeg → Media, Magick.NET → Images, PDF/Office/markup → Documents,
   archives/data/subtitles → Files, shell behaviour → App actions) and the **category** (Convert, Tool, Action).
2. **Choose the id and labels** by the `OperationInfo` rules above. Look at how neighbouring operations are declared
   in the engine's catalog files (`Catalog/VideoConversions.cs`, `Operations/*.cs`, `Office/OfficeOperations.cs` …)
   and reuse its helpers rather than building `OperationInfo` by hand.
3. **Implement it** — usually a small class deriving from the engine's operation base (or `OperationBase`):

   ```csharp
   sealed class ReverseLinesOperation() : OperationBase(new OperationInfo
   {
       Id = "text.reverse-lines",
       Category = OperationCategory.Tool,
       Group = "Reverse",
       Title = "Reverse lines",
       Description = "Writes a copy with the lines in reverse order.",
       IconKey = "reverse",
       OutputExtension = ".txt",
       Order = 300,
   })
   {
       public override bool Accepts(InputFile file) => file.HasExtension(".txt");

       public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
       {
           var lines = await File.ReadAllLinesAsync(request.Input.Path, cancellationToken);
           using var output = request.Output.BeginFile(request.Input, ".txt", "reversed");
           await File.WriteAllLinesAsync(output.WorkingPath, Enumerable.Reverse(lines), cancellationToken);
           return OperationResult.FromOutputs(output.Commit());
       }
   }
   ```

   Need parameters? Return an `OptionSchema` from `Options` (`NumberField`, `ChoiceField`, `BoolField`, `TextField`,
   `TimeField`, `FolderField`, with `VisibleWhen`) and read them with `request.Options.GetDouble/GetString/GetBool/
   GetTime`. Need an external program? Find it with `request.Context.Tools`, run it with `ProcessRunner`, and report
   it in `CheckAvailability()`.
4. **Register it** in the engine's provider (`CreateOperations`).
5. **Test it** with `TestWorkspace.RunAsync` on a fixture built in code: outputs, naming, warnings, a Unicode path with
   spaces, cancellation leaving no output, and the friendly error for a bad input. Keep fixtures tiny (≤ 2 s clips,
   ≤ 640 px). `ProviderContractTests` must still pass.
6. **Try it for real**: `build.ps1 cli list <file>` shows whether it is offered, `build.ps1 cli run <id> --out <dir>
   <file>` runs it, and `build.ps1 app --snapshot-ui <dir>` shows how the wheel looks with it.

## Design language

Dark, warm and minimal. Surfaces and text use the `Clementine.*` resources in `src/Clementine.Wheel/Themes/Colors.xaml`;
the accent is clementine orange `#FF8A1F` (gradient to `#E8650A`) and success is leaf green `#43A047`. Segoe UI
Variable and Segoe Fluent Icons, 8 px corner radius, subtle 1 px strokes, quick motion (100–160 ms ease-out).
Copy is friendly and short ("Converted to ProRes 4444 · Show in folder"), with no exclamation marks in errors.
