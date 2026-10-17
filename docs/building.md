# Building and releasing C.A.R.S. Control

For people who want to build it from source, change it, or cut a release. If you just want to use it, see [install.md](install.md).

## What you need

* Windows 10 or 11 (the app is Windows-only; the portable pieces are noted below).
* The **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`). The .NET 8 SDK also works: `Directory.Build.props` falls back to net8 when MSBuild 18 isn't installed.
* Git. That's all: **no Visual Studio**, no Windows SDK, no .NET Framework developer pack (the installer's reference assemblies come from NuGet).
* Optional: [WizTree](https://diskanalyzer.com/) in `C:\Program Files\WizTree` for Fast scan, the GitHub CLI (`gh`) if you want to test self-update against a private repository.

## Build and test

```powershell
git clone https://github.com/CarCar08/cars-control.git
cd cars-control
dotnet build CarsControl.sln
dotnet test tests\CarsControl.Tests
```

The tests don't need administrator rights and don't change your PC (registry tests use a scratch key under `HKCU\Software\CarsControlTest` and remove it; installer tests run in a temp folder).

## Layout

| Project | What it is | Targets |
|---|---|---|
| `src\CarsControl.Contracts` | The admin helper's protocol: typed operations, results, JSON. | net10/net8 |
| `src\CarsControl.Core` | Everything that isn't UI: inventory, updates, cleanup, storage scanning, settings, alerts, privacy switches, the helper client and its operations. Portable by design. | net10/net8 (windows) |
| `src\CarsControl.App` | The WPF app (`CarsControl.exe`): view models, pages, services (maintenance, Gaming mode, command palette, self-update…). | net10/net8 (windows) |
| `src\CarsControl.Helper` | `CarsControl.Helper.exe`, the small elevated process. It does nothing but run the typed operations the app sends. | net10/net8 (windows) |
| `src\CarsControl.Setup.Core` | Installer and update logic shared by Setup and the app: versions, payload manifest and checks, .NET runtime metadata, folder swap with rollback, release parsing. | netstandard2.0 |
| `src\CarsControl.Setup` | `CarsControl-Setup.exe`, the themed installer (WPF-UI), one file with the app inside. | net48 |
| `tools\CarsControl.ReleaseTool` | Build-time helper: packs the payload, writes `SHA256SUMS.txt`, extracts release notes. | net10/net8 |
| `tools\CarsControl.Probe` | A read-only CLI that runs Core against the real PC (`arp`, `winget`, `inventory`, `games`, `scan <path>`…). | net10/net8 |
| `tools\ui-audit` | PowerShell screen-capture and click/keyboard audits for test copies. | PS 5.1 |
| `tests\CarsControl.Tests` | xUnit tests. | net10/net8 |

**Why Setup is .NET Framework 4.8:** it must run on a PC that doesn't have .NET 10 yet (it installs it). .NET Framework 4.8 ships with every Windows 10/11. `CarsControl.Setup.Core` is netstandard2.0, so the app and a future macOS/Linux setup can share its logic.

## Running your changes

**Quick UI work.** Run the app from the build folder as a *test copy*, which runs alongside an installed copy and never takes focus:

```powershell
$env:CARS_DEV_INSTANCE = '1'          # separate single-instance name, "TEST COPY" title, opens on another monitor
$env:CARS_HELPER_UNELEVATED = '1'     # the helper starts without a UAC prompt and only accepts dry runs
dotnet run --project src\CarsControl.App
```

Test-copy environment variables:

| Variable | Effect |
|---|---|
| `CARS_DEV_INSTANCE=1` | Test copy (see above). |
| `CARS_DEV_MONITOR` | Which monitor it opens on: `tv` (default: the secondary that doesn't touch the primary), `adjacent`, or a device name like `\\.\DISPLAY3`. |
| `CARS_HELPER_UNELEVATED=1` | Helper without elevation; dry runs only. |
| `CARS_DUMP_VISUALS=1` + `--page dump` | Writes the visual tree to `%TEMP%\CarsControl\visual-tree.txt`. |
| `CARS_DEV_FAKE_APPROVALS=1` | Three sample "admin items waiting" on Home (never saved). |
| `CARS_DEV_MAINTENANCE=1`, `CARS_DEV_ALERTS=1`, `CARS_DEV_HOTKEY=1`, `CARS_DEV_UPDATE=1`, `CARS_DEV_BACKUP=1` | Let a test copy run scheduled maintenance, send phone alerts, take the global shortcut, check for app updates, run backup jobs. All off by default because test copies share your settings. |

`CarsControl.exe --page <home|apps|refresh|storage|cleanup|health|privacy|backup|history|settings|palette|firstrun|tabaudit|quit>` opens a page in the running copy (and `--explore "<folder>"` opens Storage › Explore there).

**The real install path.** `scripts\dev-install.ps1` builds the installer from your working copy and starts it, so you try your change exactly as users get it.

## UI audits

All of them drive a test copy through UI Automation and screenshots (never keystrokes, never focus):

```powershell
powershell -ExecutionPolicy Bypass -File tools\ui-audit\Capture-All.ps1 [-Quick]   # every screen, 6 looks/sizes, contact sheet
powershell -ExecutionPolicy Bypass -File tools\ui-audit\Audit-Ui.ps1               # scroll + click audit
powershell -ExecutionPolicy Bypass -File tools\ui-audit\Audit-Keyboard.ps1         # Tab order and accessible names
powershell -ExecutionPolicy Bypass -File tools\ui-audit\Shoot-Setup.ps1 -Setup old.exe -NewSetup new.exe   # the installer's wizard, in a sandbox
```

Output goes to `tools\ui-audit\screens\` (git-ignored: screenshots can show personal files).

## Building a release

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Version 1.0.0
```

produces, in `artifacts\`:

* `CarsControl-Setup-1.0.0.exe`: the installer, with the app, the helper and every library embedded (one file),
* `SHA256SUMS.txt`: its checksum, for the release page and for self-update,
* `release-notes.md`: the section of `CHANGELOG.md` whose heading mentions the version.

What the script does: publishes the app (which publishes the helper alongside), packs the publish folder into `payload.zip` with a `payload.json` manifest of per-file SHA-256 hashes, builds Setup with those embedded, and writes the checksum file. The installer verifies every extracted file against the manifest before it replaces anything.

### The update test

`scripts\Test-Update.ps1 -OldSetup <previous Setup> -NewSetup <new Setup>` checks, in a sandbox (temp folder, no admin, no registry), that the new Setup can update an install made by the previous one the way the app's self-update does: started from inside the install folder, while another process briefly holds a file open. It checks the exit code, the installed version, leftover folders and that no Setup process stays running. The release workflow runs it against the previous release before publishing; run it yourself after any change to Setup or self-update.

### Publishing on GitHub

1. Move the release notes into `CHANGELOG.md` under a heading that contains the version (for example `## 1.0.0 (2026-11-02)`).
2. Set `<Version>` in `Directory.Build.props` and commit.
3. Tag and push:

   ```powershell
   git tag v1.0.0
   git push origin v1.0.0
   ```

   `.github\workflows\release.yml` runs the tests, runs `build-release.ps1` and creates the GitHub Release with the installer, `SHA256SUMS.txt` and the notes. A tag with a suffix (`v1.1.0-rc.1`) is published as a **pre-release**, which the in-app updater only offers to people who opted in.

`.github\workflows\ci.yml` builds, tests and builds the installer on every push and pull request.

### Versions

`MAJOR.MINOR.PATCH[-prerelease]`. The app reports it from its `InformationalVersion`; self-update compares versions with proper pre-release ordering (`1.0.0-rc.2` < `1.0.0-rc.10` < `1.0.0`).

### Signing (not enabled yet)

Unsigned installers trigger SmartScreen's "Windows protected your PC" box. Options when you're ready:

* **Azure Trusted Signing** (about $10/month, identity verification) or an **EV/OV code-signing certificate**: sign `CarsControl-Setup-<version>.exe` in `scripts\build-release.ps1` *before* the checksum is taken, and sign `CarsControl.exe`/`CarsControl.Helper.exe` before they're packed. `release.yml` has a marked place for the signing step and secrets.
* **SignPath Foundation** signs open-source projects for free once the repository is public.

## Making changes safely

Performance measurements: publish a Release copy, then run `tools\ui-audit\Measure-Performance.ps1 -Exe <absolute repository exe path> -Label <name> -Samples 3`. It measures first paint, memory after ten pages, and visible/tray CPU, using isolated data on the TV without activation. Results stay in ignored `artifacts-perf`. `build-release.ps1 -ReadyToRun` is available for comparisons; it increases payload size and is not the default. Release `-Output` accepts only a repository-root `artifacts` or `artifacts-<name>` directory, whose contents will be replaced.

* **The helper is the security boundary.** A new elevated operation is a new record in `CarsControl.Contracts\HelperProtocol.cs` (identifiers only, never a path or command line), a handler in `CarsControl.Core\Elevation\` that **validates every identifier against its own allow-list or definition**, a rejection test in `tests\` and an entry in [security.md](security.md). Operations that run unattended must work over an already-open session (`SendIfConnectedAsync`) and never start one.
* **Anything destructive says what it will do first** and, where it can, is undoable (privacy switches snapshot their previous value; leftovers go to quarantine; the Recycle Bin, not delete).
* **Success is verified, not assumed**: an uninstall must remove the registry entry, an update must change the version, a pip update must change the copy you use.
* **Don't block the UI thread**; scans and checks run on the pool and report through events.
* **Add a test** with the change. Logic belongs in `Core` (testable without WPF); view models stay thin.
* Settings are one JSON file (`CarsSettings`); state that describes *this PC* (history, scan caches, undo snapshots) is in `cars.db`.
* Style: match the file you're in. Comments explain *why*.

## Adding things

* **A cleanup category:** add a `CleanupRule` in `Core\Cleanup\CleanupRules.cs` (measure, list items, run, whether it needs admin, whether it's on by default).
* **A privacy switch:** add a `Tweak` to `Core\Tweaks\TweakCatalog.cs` (registry values with their Windows defaults, scope, build limits). The tests check that nothing touches security settings and every switch round-trips.
* **A command palette entry:** add it to the right provider in `App\Services\Palette\PaletteService.cs`.
* **A settings section:** a `ListBoxItem` plus a `Panel…` in `SettingsPage.xaml` and a `SettingsViewModel.<Name>.cs` partial.
