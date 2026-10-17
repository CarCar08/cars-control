# How C.A.R.S. Control keeps your PC safe

C.A.R.S. Control can update, uninstall, clean and (a little) configure Windows, so it matters *how* it gets the rights to do that. This page is the full account: the design, every operation the administrator helper accepts and what each one checks, everything the app connects to, and what it stores where. Report a problem with any of it through the private route in [SECURITY.md](../SECURITY.md).

**Contents**

1. [The short version](#1-the-short-version)
2. [Two programs, two jobs](#2-two-programs-two-jobs)
3. [The helper's guardrails](#3-the-helpers-guardrails)
4. [Every helper operation](#4-every-helper-operation)
5. [Things it never does](#5-things-it-never-does)
6. [Files from outside are untrusted](#6-files-from-outside-are-untrusted)
7. [What it connects to](#7-what-it-connects-to)
8. [What it stores, and where](#8-what-it-stores-and-where)
9. [The installer and updates](#9-the-installer-and-updates)
10. [Third-party components](#10-third-party-components)
11. [Honest limits](#11-honest-limits)

---

## 1. The short version

* The app **always runs as you**, never as administrator. It refuses to start elevated.
* Everything that needs administrator rights goes to one small program, **`CarsControl.Helper.exe`**, which Windows starts through the normal UAC prompt.
* The helper does **not** run commands or touch arbitrary paths it is handed. It accepts a fixed list of **35 typed operations** that carry bounded identifiers (a winget id, a cleanup-rule id, a privacy-switch id, a Windows Update GUID, a drive letter, an Epic manifest id), validates each, and works out what to run by itself. Old-Windows removal is the narrowly checked folder exception described below.
* Nothing runs elevated in the background. No service, no scheduled task, no startup entry. **Unattended work never shows a UAC prompt**; if it needs admin and no session is open, it waits for you in the approval queue.
* No telemetry. The only connections are listed in [section 7](#7-what-it-connects-to).
* **Salad is permanently protected**, including when your configurable protection list is empty: app installation, updates, removal, startup and menu changes, folder moves and graceful-close requests refuse it. The helper independently rejects its package identifiers, startup names/commands, shortcut targets, task definitions and uninstall entries. Task commands are re-read before a real change; unreadable definitions are left unchanged. Earnings miners and WSL are also refused by the close-apps path.
* File backups reject junctions and symbolic links in their destination (including retention folders). If a source folder cannot be read, the run reports a failure and keeps existing backup files in place rather than treating them as deleted.

### Isolated developer audits

The repository's audit and performance scripts seed a fresh folder under ignored `artifacts-ui`, then set `CARS_DEV_DATA_ROOT` on a TV test copy. Preferences and the inventory cache are copied read-only from the normal profile; history, alerts, backups, sync credentials and jobs start empty. Logs, caches, downloads, quarantine and the database stay in that test folder. Start-at-sign-in changes are simulated there. A production copy ignores the override. Test copies cannot send real ntfy messages unless explicitly enabled with `CARS_DEV_ALERTS=1`.

## 2. Two programs, two jobs

| | `CarsControl.exe` (the app) | `CarsControl.Helper.exe` (the helper) |
|---|---|---|
| Runs as | **You** (your normal account) | An administrator, after a UAC prompt |
| Does | Everything you see, plus all per-user work: your apps, your registry, your files | Only the 35 operations in [section 4](#4-every-helper-operation) |
| Lives in | `C:\Program Files\CarsControl` | The same folder (administrators-only) |
| Lifetime | While you use it, or in the tray | Started on first need, closed after 15 idle minutes (configurable), when the app exits, or when the pipe closes |

Why split the work this way, instead of running the whole app as administrator? Because **per-user things belong to the user**. On a PC where you sign in as a standard user and approve admin prompts with a separate administrator account, an elevated updater is blind to your per-user apps (winget answers "no installed package found", Store apps fail with `0x80073D19`) and would write your policies into the *administrator's* registry hive. So per-user work (HKCU apps, Store/MSIX packages, winget `--scope user`, pip `--user`, your privacy switches, your startup entries, your right-click menu) runs as you with no prompt, and only machine-wide work crosses to the helper.

The same split is why an update is safe to run unattended: the part that runs while you're away can only touch what *you* could touch.

## 3. The helper's guardrails

### Who can talk to it

1. **The pipe is private.** The app creates a named pipe with a random name (`CarsControl.<GUID>`), allowing only your own account and the Administrators group, with a single instance.
2. **The app checks the client.** The process that connects must be the exact helper process the app just launched (pipe client PID = launched PID); the helper must also report that it is elevated.
3. **The helper checks the server.** Before it accepts anything, it checks that the pipe's server PID is the PID it was told is its parent, **and** that this parent's image is `CarsControl.exe` in the helper's *own* folder (`C:\Program Files\CarsControl`, writable only by administrators). A different program, or a copy of the app run from somewhere you can write to, is refused (exit codes 5 and 6 in the helper's log).
4. **It dies with the app.** It watches the parent process and exits when it ends.
5. **The helper refuses to run unelevated** (except the `--dev-unelevated` test mode, in which every operation is a dry run).

### What it will run

* **Identifiers, not commands.** Every operation in the protocol is a typed record (`CarsControl.Contracts\HelperProtocol.cs`). There is no "run this command line" and no operation takes a path to act on (the old-Windows removal takes a folder, but only a top-level folder on a drive that passes the checks in [section 4](#4-every-helper-operation)).
* **Fixed programs, fixed places.** The helper resolves what to run itself: System32 tools by full path (`pnputil`, `powercfg`, `sc`, `schtasks`, `takeown`, `icacls`, `robocopy`, `wsl`, Windows PowerShell), winget through its location for the account, WizTree from its fixed install path. Arguments are built from validated pieces; user text never lands inside a script (the restore-point description, for example, travels in an environment variable, not in the script text).
* **Validated every time.** Each identifier is checked against a pattern or an allow-list on the helper side again, whatever the app did first. The unit tests (`tests\`) cover the rejections; `CarsControl.exe --selftest-helper` replays injection attempts against a live (dry-run) helper.
* **Dry run.** Every operation supports a dry run that returns the exact command it would have run. The app shows this in the "what will happen" views, and the test suite uses it.
* **Everything is logged** to `C:\ProgramData\CarsControl\logs` (see [section 8](#8-what-it-stores-and-where)).

### Where it writes

`C:\ProgramData\CarsControl` is created by the helper only. A standard user may create folders in ProgramData, so before using it the helper **removes planted junctions** (the link, never the target), **moves aside any folder not owned by Administrators or SYSTEM** (as `*.untrusted-<time>`, untouched), and creates a fresh one owned by Administrators with an ACL of *SYSTEM and Administrators: full, Users: read*. The unelevated app never creates that folder. The drive-listing exports of Fast scan sit in a subfolder readable only by SYSTEM, Administrators and you (they include other accounts' file names), and are deleted when the helper exits.

### Things you'd expect to be risky, and aren't

* **Leftovers are quarantined, not deleted.** Files and registry keys an uninstalled app left behind are moved into a quarantine and can be restored from History for 30 days. The helper hands out *opaque tokens* for what it found and only accepts tokens it produced in this session; it can't be asked to quarantine an arbitrary path.
* **Uninstalls are of what's installed.** `ArpUninstallOp` takes a hive and key name, refuses HKCU, reads the registry entry itself and plans the method (MSI, the vendor's quiet string, winget). The app cannot supply a command.
* **Unelevated actions on your own files** (recycling, moving, duplicates, backups) happen in the app, as you. The helper has no "delete this file" operation.

## 4. Every helper operation

The *wire name* is the `type` in the protocol. "Accepts" is what the helper checks before doing anything.

### Apps and updates

| Operation (wire name) | What it does | Accepts / validates |
|---|---|---|
| `ping` | Says hello: version, account name, elevated or not. | Nothing to validate. |
| `winget-upgrade` | `winget upgrade --id <id> --exact` as the administrator, for machine-wide apps. | `id`: 1–128 characters of a winget id pattern; optional `version`: 1–64 characters of a version pattern. winget must be found for the account. |
| `winget-install` | `winget install --id <id> --exact --scope machine`, an app for every user. | `id` and `version` as above; `source` is exactly `winget` or `msstore`. Scope is fixed to machine. |
| `winget-uninstall` | `winget uninstall --id <id>` (silent, non-interactive). | `id` as above. The optional hive and key name only name the HKLM entry to snapshot first. |
| `arp-uninstall` | Uninstalls a machine-wide program. | Hive must be a machine hive (`LocalMachine64` / `LocalMachine32`); **HKCU is refused**. Key name 1–260 characters without a backslash. The entry must exist; the helper plans the method; apps with no silent uninstaller run only if the app confirmed it as interactive; Store packages and Steam are not helper uninstalls. |
| `store-update` | Asks the Store to scan for and install app updates, through the MDM bridge (not allowed from a standard account). | Takes nothing; a fixed PowerShell script. |
| `wu-install` | Downloads and installs the listed Windows updates through the Windows Update Agent. | 1 or more update ids, each a GUID. |
| `wsl-update` | `wsl --update`. | Takes nothing. |
| `pip-upgrade` | `python -m pip install --upgrade <package>` with the *machine-wide* Python. | Package name pattern (letters, digits, `.`, `_`, `-`; an optional scope is not accepted), max 120 characters; anything starting with `-` or `@` is refused, so no pip flag can ride along (`--index-url=…` is a built-in self-test). |
| `restore-point` | Makes a System Restore point. | Description matches `^[A-Za-z0-9 .,:()_-]{1,64}$` and is passed in an environment variable. |
| `restore-enable` | Turns System Protection on for **C: only**. | Takes nothing; a fixed script. |
| `wu-pause` | Gaming mode: pauses Windows Update the way Settings does, with a 12-hour safety expiry; resume undoes only a pause C.A.R.S. made. | A boolean. A pause you set yourself is detected and left alone. |

### Cleanup, leftovers and storage

| Operation | What it does | Accepts / validates |
|---|---|---|
| `cleanup-estimate`, `cleanup-run` | Measures or deletes the admin-only cleanup categories (Windows temp, Delivery Optimization cache, component store, error reports, minidumps…). | A list of **rule ids**. The rules (what each may touch) are defined in the helper's own code; unknown ids are rejected. |
| `leftover-scan` | Looks for what an app *uninstalled in this helper session* left in machine-wide locations. | Hive and key name; it works from its own pre-uninstall snapshot, not data from the app. Hands back opaque tokens. |
| `quarantine` | Moves found leftovers into quarantine. | Only tokens this helper session produced. |
| `quarantine-restore` | Puts a quarantined set back. | A quarantine id the helper recorded. |
| `storage-scan` | Fast scan of a drive's master file table with WizTree. | A single ASCII letter that names a **fixed NTFS** drive; WizTree must be installed at its fixed path. The CSV goes to a locked-down folder. |
| `remove-old-windows` | Removes one top-level folder of a leftover Windows installation. | See [below](#remove-old-windows-in-detail). |
| `moved-game-remove` | Permanently removes the original of an Epic game after a verified move, when the owner presses **Remove old copy** (never automatically). | Only a bare manifest id (`A–Z a–z 0–9 _ -`, 1–120 characters). The helper reads the current `.item` and its `.cars-before-move` backup from Epic's fixed manifest directory itself. The original must be one game directly inside `<drive>:\Epic Games`, `<drive>:\Program Files\Epic Games` or `<drive>:\Program Files (x86)\Epic Games`; the new copy must have the same folder name inside another drive's root `Epic Games` folder. Both must exist; links in the records, folders, ancestors or contents are refused; Salad is protected. All launcher records must point to the new copy, and no manifest, installation-list or install-helper (`.egi`) entry may still point to the original. Unreadable records stop deletion. The launcher and game must be closed. Every relative file name, size and SHA-256 is compared before deletion; failure leaves the old copy or its remaining files for manual cleanup. |
| `driver-delete` | Removes one *old copy* of a driver from the driver store (`pnputil /delete-driver`, no `/force`, no `/uninstall`). | The name must look like `oem<N>.inf`. The helper re-reads the driver store itself and refuses unless that package is **not in use by any device** and **a newer copy of the same driver exists**. |

### Gaming mode and Windows Update settings

| Operation | What it does | Accepts / validates |
|---|---|---|
| `purge-standby` | Empties the standby memory list (what ISLC does) with the documented system call. | Takes nothing. |
| `service-control` | Stops or starts a service. | **Only `WSearch`** (Windows Search). Anything else is rejected. |
| `wu-policy` | Sets one Windows Update setting. | `setting` is one of `pause-until` (a date up to 35 days ahead, or `resume`), `exclude-drivers` (`on` / `default`), `defer-feature-days` (`0`–`365` or `default`), `active-hours` (like `8-23`, or `default`). It maps to fixed registry values under HKLM's WindowsUpdate keys. Refused while Gaming mode's pause is active. |
| `wu-hide` | Hides or shows one Windows update so it isn't offered. | An update id that is a GUID, and it must still be listed by Windows Update. |

### Privacy

| Operation | What it does | Accepts / validates |
|---|---|---|
| `tweak` | Applies, undoes or resets one privacy switch that needs admin. | The id must be in the helper's own **catalog**, scope machine or user-policy (switches that only touch your own account are applied by the app, not here); action is `apply`, `undo` or `default`. Only that switch's own registry values are written. Your policy values go into **your own** registry hive (`HKEY_USERS\<your SID>`), and the helper refuses if it can't tell whose SID that is. The state before the first change is saved in `ProgramData\CarsControl\tweaks`, so Undo restores exactly that. The catalog never contains Defender, SmartScreen, the firewall, UAC, Windows Update or Windows Error Reporting. |

### Apps › Startup

| Operation | What it does | Accepts / validates |
|---|---|---|
| `startup-entry` | Switches a machine-wide Run entry or common Startup-folder shortcut on or off *the way Task Manager does* (a `StartupApproved` value; the entry itself is not deleted). | `where` is `run`, `run32` or `folder`; the name is ≤ 260 characters with no `\ / : NUL`; the entry **must already exist** there. |
| `service-start-type` | Sets a service to automatic, on demand or disabled with `sc config`. | A valid service name (`A–Z a–z 0–9 _ . - $`); `auto` / `manual` / `disabled` only; the service must exist, be a real service process (not a kernel driver), **not belong to Microsoft or run from the Windows folder**, and **not be on the protected list**: Salad, Tailscale, Proton VPN, WireGuard, NVIDIA display services, Defender and the security center, C.A.R.S. itself, Cryptomator, PawnIO, ExitNodeGuard, LibreHardwareMonitor, OpenSSH, AnyDesk and Parsec. |
| `task-toggle` | Enables or disables a scheduled task with `schtasks /Change`. | A plain task path; **not under `\Microsoft\`**, not one of C.A.R.S.'s own, and not a task whose path mentions ExitNodeGuard, Salad, Tailscale or Proton. |

### Health

| Operation | What it does | Accepts / validates |
|---|---|---|
| `drive-health` | Reads each drive's temperature, wear, power-on hours and error counters (Windows gives these only to administrators). Read-only. | Takes nothing. |
| `power-requests` | `powercfg /requests`: what is stopping sleep. Read-only. | Takes nothing. |
| `boot-performance` | Reads boot and shutdown times from the Diagnostics-Performance event log. Read-only. | `days`, clamped to 1–90. |
| `crashdump-copy` | Copies one blue-screen minidump so the debugger, running as you, can read it (Windows lets only administrators read `C:\Windows\Minidump`). Read-only on the original. | A bare Windows minidump name (`091126-13359-01.dmp` pattern), which must exist in `%SystemRoot%\Minidump`, must not be a link and must be under 256 MB. The copy goes to `C:\ProgramData\CarsControl\dumps`, readable only by SYSTEM, Administrators and you; copies older than 30 days are removed. |
| `sensors-read` | Reads CPU, board and fan sensors with LibreHardwareMonitor and the PawnIO driver. Read-only. | Takes nothing; reports `pawnio-missing` if the driver isn't installed. |
| `pawnio-install` | Installs the PawnIO driver (needed for CPU and motherboard sensors). | Takes nothing. The URL and the **SHA-256 of the one release it accepts (PawnIO 2.2.0)** are compiled into the helper. It downloads into its own locked folder, checks the size and the hash, refuses on any mismatch, and only then runs the installer silently. |

### Remove old Windows, in detail

`remove-old-windows` is the one operation that deletes a lot, so its rules are the strictest. The helper repeats all of them itself; none is left to the app:

1. The path must be absolute and **exactly one level below a drive root**, and its name must be one of **`Windows`, `Users`, `Program Files`, `Program Files (x86)`, `ProgramData`**. Nothing deeper, nothing else (so `D:\SteamLibrary`, `D:\Games` and friends can't be named).
2. The drive can **not** be the running Windows' drive.
3. The drive can **not** hold a boot manager (`bootmgr` or `EFI\Microsoft\Boot\bootmgfw.efi`).
4. The folder can not be a junction or symbolic link.
5. The drive must really hold an offline Windows: **`Windows\System32\config\SYSTEM` must exist** on it.
6. **No installed program may still point into the folder.** The helper reads the Uninstall registry keys and refuses if any install location, icon, uninstaller or install source lies inside it, and names the programs.
7. **No junction or symbolic link on the running system may lead into it** (checked in the system drive's root, Program Files, ProgramData and each user's profile, AppData and Documents).

Only then does it take ownership (`takeown`), grant Administrators access (`icacls`), empty the folder (`robocopy` of an empty folder, `/MIR /XJ`, never following junctions) and remove the empty shell. The app shows the full list of what will go, and offers to copy your own files out of `Users` first.

## 5. Things it never does

* Run the whole app, or any scheduled job, **elevated in the background**.
* Show a UAC prompt on its own while you're away or in a game. Admin steps of scheduled maintenance wait in the approval queue; Gaming mode uses a session you approved at sign-in, or skips those steps.
* Pause, stop, end or change **Salad**. Its processes are recognised and labelled, never offered to End, and its services and tasks are on the protected list.
* Weaken Windows' protection (see the privacy catalog above), or touch Defender, SmartScreen, the firewall, UAC, Windows Update's security behaviour or error reporting. Windows Update can be *paused* or have drivers excluded, which are Settings-level choices.
* Install or run anything silently from the internet. The only things downloaded and executed are *Setup* (checksum-verified, started by you), the .NET runtime (Microsoft's installer, SHA-512-verified) and PawnIO (SHA-256 pinned in the code), each at your request.
* Add itself to startup unless you flip the toggle (it's your own `HKCU\...\Run` value).
* Delete anything it can't put back without being asked: file operations go to the Recycle Bin, quarantine or `.cars-versions` unless you chose a permanent action and confirmed it. Emptying the Recycle Bin (opt-in), removing the old Windows folders (explicit, with the full list), and removing an original game after a verified move each say what will be removed before confirmation.
* Collect or send telemetry, or phone home about your machine.

## 6. Files from outside are untrusted

C.A.R.S. reads files other people (or other PCs, or a sync service) could have written, and treats all of them as hostile:

* **A setup bundle** from the sync folder or an import: only a whitelist of known settings is read, numbers are clamped, text lengths are capped, and it can never carry your ntfy token, the update repository, or your profile/paths for this PC. An unrecognised or malformed file is ignored. (Covered by `BackupTests`.)
* **An app list** (`.json` export): ids go through the same winget id validation before anything is installed, and installing is always something you press.
* **Fleet status files** from other PCs are displayed as plain text, with lengths and counts clamped.
* **Release metadata** from GitHub is checked: a release must carry both the installer and `SHA256SUMS.txt`, the installer's SHA-256 must match, and the check is repeated just before it's run (the file sits in a folder you can write to).
* **A minidump**: parsed by Microsoft's debugger (`cdb`), never executed.
* **The `--explore <path>` argument** (right-click › Folder size): validated as an existing, fully-qualified folder before it's used.

## 7. What it connects to

No analytics, no accounts, no advertising. The complete list:

| To | When | What is sent | Why |
|---|---|---|---|
| **GitHub** (`api.github.com`, then the release download on `github.com`) | At start and once a day, never during a game (switch off in Settings › About & updates) | An unauthenticated read of this project's releases; nothing about you. While the repository is private, it asks the **GitHub CLI** (`gh`, if you have it signed in) instead. | Self-update. The download is checked against `SHA256SUMS.txt`. |
| **ntfy** (default `https://ntfy.sh`, or your own server; https only) | Only if you switch on phone alerts, when an alert fires | Short messages with counts, drive letters and times only, never file names, app names or paths, to a random topic you can change. | Alerts on your phone. |
| **winget sources and the Microsoft Store** | When you check for updates, search Get apps or install | What winget itself sends (via the Windows Package Manager) | Updates and installs. C.A.R.S. only drives winget. |
| **Windows Update** | When you check or install | Through the Windows Update Agent, as Windows does | Windows updates, Windows Update tab. |
| **Microsoft's symbol server** (`msdl.microsoft.com`) | Only when you analyse a minidump on Health › Crashes | Module names and identifiers from the dump, as any debugger does | Readable crash analysis. Symbols are cached in `%LOCALAPPDATA%\CarsControl\dumps\symbols`. |
| **Microsoft's .NET download** (`builds.dotnet.microsoft.com`) | Only in *Setup*, only if the .NET 10 Desktop Runtime is missing | A request for the release list and the installer | Installing the runtime; SHA-512 verified. |
| **GitHub, namazso/PawnIO.Setup** | Only when you press *Install PawnIO* on Health › Sensors | A download of one fixed URL | The PawnIO driver for CPU and motherboard sensors; SHA-256 pinned. |
| **npm, pip, PowerShell Gallery, the Store, Office** | When you check or update those sources | Whatever those tools send | Updates. C.A.R.S. only runs them. |
| **Tailscale** | Health › Network, when open | The local `tailscale` command; its `netcheck` talks to Tailscale's relay servers as Tailscale does | Showing VPN and tailnet status. |
| **Your browser** | Only when you click a link (project page, release notes, "Look up this board's BIOS and drivers" opens a DuckDuckGo search for the board name) | Done by your browser, not by C.A.R.S. | Convenience. |

Proton Drive, OneDrive and similar are **not** contacted by C.A.R.S.: it reads and writes an ordinary folder, and the sync app does the rest.

## 8. What it stores, and where

| What | Where | Notes |
|---|---|---|
| Your settings | `%LOCALAPPDATA%\CarsControl\settings.json` | Plain JSON. An ntfy access token, if you add one, is stored encrypted for your Windows account (DPAPI). |
| History, scans, update results, undo snapshots, crash analyses | `%LOCALAPPDATA%\CarsControl\cars.db` | SQLite. |
| Quarantine (yours) | `%LOCALAPPDATA%\CarsControl\Quarantine` | Restorable for 30 days. |
| Logs | `%LOCALAPPDATA%\CarsControl\logs` | `errors.log` (rotates at 1 MB), `perf.log`. No personal data beyond file names in an error. |
| Downloaded updates | `%LOCALAPPDATA%\CarsControl\updates` | Only the version on offer. |
| Copied crash dumps | `C:\ProgramData\CarsControl\dumps` (readable by you and administrators only; removed after 30 days) | Only if you use crash analysis. |
| The debugger's symbol cache | `%LOCALAPPDATA%\CarsControl\dumps\symbols` | Only if you use crash analysis. |
| Backup copies | The destination you chose, in `<folder>\<backup name>` (on a USB drive, `C.A.R.S. Backup\<backup name>`) | Changed and deleted files kept 30 days in `.cars-versions`. |
| Helper logs, scans, machine-wide quarantine, privacy undo snapshots | `C:\ProgramData\CarsControl` | Administrators write, users read. |
| Program files | `C:\Program Files\CarsControl` | Administrators write. Checked by Setup on every install. |

## 9. The installer and updates

* **`CarsControl-Setup-<version>.exe`** carries the app, the helper and its libraries as a payload with per-file SHA-256 hashes. Before anything on disk changes, Setup unpacks to `CarsControl.new` and verifies every file. It installs by swapping folders (the old one renamed aside, kept to roll back if anything fails) and **verifies that standard users cannot write to the install folder**, refusing to continue if they could.
* Setup runs **as you**; only its install step is elevated (the same exe, one UAC prompt). It adds no service, scheduled task or startup entry.
* **It is not code-signed yet.** Windows SmartScreen therefore warns on the first run. Every release lists the installer's SHA-256 in `SHA256SUMS.txt`; you can check it yourself ([install guide](install.md#2-download-it)) or [build from source](building.md). The release pipeline (`.github\workflows\release.yml`) has a signing step that turns on when a certificate is available.
* **Self-update** downloads the installer, checks it against the release's `SHA256SUMS.txt`, shows you the release notes and waits for you to press *Update now*. It re-verifies the file immediately before starting it.

## 10. Third-party components

| Component | Used for | Licence |
|---|---|---|
| [WPF-UI](https://github.com/lepoco/wpfui) (+ Tray) | The interface | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Application structure | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | The database | MIT |
| [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) | Wiring | MIT |
| [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) | Toast notifications | MIT |
| [CsWinRT](https://github.com/microsoft/CsWinRT) | WinRT projections | MIT |
| [QRCoder](https://github.com/codebude/QRCoder) | The phone-alert QR code | MIT |
| System.Management, System.Text.Json | WMI, JSON | MIT |
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and its libraries (DiskInfoToolkit, HidSharp, RAMSPDToolkit) | CPU, board and fan sensors | MPL-2.0 (its own DLLs ship unmodified) |
| `Microsoft.Management.Deployment.winmd` from [winget-cli](https://github.com/microsoft/winget-cli) | Talking to winget from an unpackaged app | MIT |

Optional tools C.A.R.S. uses if **you** install them (it ships none of them): [WizTree](https://diskanalyzer.com/) (fast scans), **WinDbg** (installed from winget on request, for crash analysis), **PawnIO** (the sensor driver, installed on request from its own release; see [its project](https://github.com/namazso/PawnIO) for its licence), and the `gh`, `tailscale`, `winget`, `pip` and `npm` command-line tools.

## 11. Honest limits

* **Once you approve the helper, anything running as *you* on this PC can ask it to do the operations above.** The pipe is yours and the helper can't tell your own app from a program that has been injected into it or runs as you. That is why the list is short, typed and validated, why each operation is bounded (a folder name from a fixed list, an update GUID, a catalog id) and why the helper closes when idle. It does not make a compromised user account harmless; no allow-list can. It does mean such a program gains *only* these operations, not a shell.
* **Unsigned binaries.** Until a code-signing certificate is in place, you can only trust a download by its checksum or by building it yourself.
* **Third-party tools.** winget, the Windows Update Agent, pip and npm do the downloading they always do; C.A.R.S. adds validation around *what it asks them to do*, not around what a package contains.
* **PawnIO is a kernel driver.** It is what lets sensors be read on current Windows, and it is optional. If you'd rather not install it, the GPU sensors, drive temperatures and everything else still work.

Found a hole in any of this? Please tell us privately: [SECURITY.md](../SECURITY.md).
