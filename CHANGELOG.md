# Changelog

## 1.1.2 — removing built-in apps no longer hangs (2026-10-19)

**Fixes**
- Privacy › Apps that came with Windows: Remove could keep spinning after Windows had already removed the app. C.A.R.S. now
  checks every couple of seconds whether the app is gone and finishes as soon as it is, stops waiting after two minutes with
  a plain explanation, and says "Already removed" for an app that's no longer there instead of trying again.

## 1.1.1 — no more unwanted driver updates (2026-10-19)

**Fixes**
- Refresh no longer lists driver updates that Windows itself wouldn't install as ordinary updates. Windows Update also hands out
  optional and not-recommended items (often very old generic Intel and Realtek drivers that can replace your PC maker's own), and
  on a PC set to "Don't include drivers with Windows updates" every driver is in that group. They're now in a collapsed
  **Optional Windows updates** section with an explanation, unticked, and never counted on Home, in the tray or in alerts, or
  included in "Update everything" and maintenance.

## 1.1.0 — Optimization page, faster and lighter (2026-10-17)

**Optimization page** (new, Ctrl+8; Settings moves to Ctrl+,)
- Power mode (Best power efficiency / Balanced / Best performance), with a one-click switch to Windows' Balanced plan and back when another plan hides power modes; hibernation; full shutdown instead of fast startup.
- Game Mode, background game recording, hardware-accelerated GPU scheduling, mouse acceleration, animations, transparency, start-up delay, and sharing Windows updates only on your own network.
- Every switch says **how sure we are it helps** (Documented, Measured, Depends on your PC, or Your preference), with the reason and a source, and switches back exactly. **Apply recommended** for your kind of PC lists every change first. No placebo tweaks and nothing that weakens security; what's left out on purpose is listed on the page.
- **Test it on this PC**: records a minute of a game, makes the change, records another minute, and only calls it better or worse when the difference beats the game's own variation. Keep or undo it right there. Uses Intel PresentMon, fetched once from its official release and checked before every use.

**Faster and lighter**
- Pages you haven't used for a while are released while the window is open too, and freed memory goes back to Windows: about 105 MB at Home and 140 MB after browsing every page (what Task Manager shows), 15–40 MB in the tray.
- The window appears before Home starts reading drives and the network; a backup hard disk that has spun down can no longer freeze Home, Storage or the command palette.
- The first start after boot is about 0.2 s faster (precompiled code).
- Opening Privacy no longer briefly takes about 250 MB.

**Fixes**
- Command palette shortcuts to a tab (Free up space, Scan drive, Settings sections…) open that tab again.
- Bug reports also hide other accounts' folder names, and Report it no longer fails on an error containing an emoji.
- Salad and Proton VPN mentions follow apps you install or remove while C.A.R.S. runs.

## 1.0.0 — the public release (2026-10-17)

The first public release: source, issues and releases on GitHub, with the user guide and FAQ.

- **Report a problem** opens a bug report in your browser with the details filled in, and error messages offer **Report it** with the error included. Names, paths and e-mail addresses are removed first; you read it and decide whether to submit. No telemetry.
- Clearer wording in several places, helpful messages on empty lists (History, Ignored updates, Junk), and easier-to-see (?) buttons.
- Salad, Proton VPN and Tailscale features only appear on PCs that have them.
- **C.A.R.S. gives its memory back while it sits in the tray**: after three minutes hidden it releases the pages you visited, pauses graphics rendering and returns the idle memory to Windows, dropping from about 300 MB to about 30 MB. Pages rebuild instantly when you open the window again.
- **Public**: the source, issues and releases are now on the public GitHub repository, with a user guide, an FAQ (speed and games, what changes on its own, Windows 10, reporting problems) and a project website.
- Fixed: Health › Overview no longer says "Proton VPN isn't connected" on PCs that don't have Proton VPN, and the Tailscale exit-node text no longer assumes a VPN.
- Fixed: the first-time setup's Privacy step showed a technical name instead of its checkbox text.

## 0.9.0 — explanations, accurate health, Epic moves and solid updates (2026-10-16)

Everything since 0.8.0, tested on the owner's PC through eight release candidates.

**Understand what everything does**
- **(?) explainers** next to sections: what it is, what C.A.R.S. does, what you should do, and whether it's safe. **Ctrl+K** then "what is…" finds them too.
- **Tooltips** on every button, switch, value and table header, and a plain sentence under each section.
- **Recommended** tags for your kind of PC (Desktop, Laptop or Gaming PC, detected and changeable in Settings › This PC), with the reason. **Apply recommended settings…** previews every change and can be undone.
- **First-time setup** in seven skippable steps (PC profile, automation, privacy, backups and sync, sensors, alerts) with a final review and exact undo.

**Health that tells the truth**
- **Drives** read their own health log: NVMe wear, power-on hours, data written, unsafe shutdowns, errors and spare area without admin; SATA disks with one approval. Temperatures show the drive's limit, and USB sticks say they don't report instead of a fake 0%.
- **Crash analysis works**: the admin helper copies the protected minidump for the debugger, the debugger's real error is shown if it fails, and the result explains what the blamed driver means and what to try.
- **Sensors** summary (CPU temperature, power and load, motherboard, spinning fans) with units on every reading; impossible readings and empty fan headers are hidden.

**Storage and updates**
- **Move Epic games** to another drive: every file is checked, all of Epic's records (including the 2026 launcher's install helper) are switched, and the original stays until you've played from the new place and press **Remove old copy**.
- **Stuck winget updates explained**: when winget's catalog offers a version its installer doesn't deliver (VS Code), C.A.R.S. says so, offers Skip this version and the app's own updater, and maintenance stops retrying it.
- **Salad is permanently protected** everywhere, including renamed startup entries and tasks.

**Fixes**
- Banners close with ✕; checkbox text wraps; transparency works with Follow Windows (Windows draws it solid while the window isn't focused, and Settings says so).
- **Updating is solid**: Setup no longer stays running after an update, no longer blocks the folder it replaces, rides out a briefly busy file, and ends on a clear message instead of hanging.
- Successful moves show in green; the Home VPN chip turns into a green check when the VPN carries traffic; right-clicking a game no longer errors.
- Third-party licences ship with the app (Settings › About & updates › Licenses).

## 0.9.0-rc.8 — Epic's empty legacy list (2026-10-16)

- The rc.7 update worked. The Fall Guys move then stopped with "The Epic installation list must contain exactly one record of this game": the 2026 launcher keeps `LauncherInstalled.dat` empty even for installed games, because its install-helper (`.egi`) records replace it. When such a record exists, a missing list entry is now left alone (two entries are still refused, and old launchers without .egi records still need theirs). The admin helper's Remove old copy check follows the same rule.

## 0.9.0-rc.7 — the real reason updates couldn't swap the folder (2026-10-16)

- rc.6's retries showed the "file in use" never cleared: the user of the folder was Setup itself. Started by the app, Setup inherited the app's working folder, `C:\Program Files\CarsControl` (when opened from the Start menu), and Windows won't rename a folder a running program is standing in. Setup now moves its working folder to Temp as it starts, the app starts it from the download folder, and Setup relaunches from a Temp copy whenever it runs from inside the install folder (Apps › Modify, not just Uninstall). Reproduced in a sandbox: rc.6 fails, rc.7 updates.

## 0.9.0-rc.6 — updates ride out a busy file (2026-10-16)

- The rc.3 → rc.5 update failed at "Installing" with "The process cannot access the file because it is being used by another process". Setup swaps the program folder in two renames, and one moment of a file being open (antivirus checking the new files, the search indexer, or the app still letting go of its files) failed the whole update. Setup now retries those renames for about 15 seconds and logs each wait. If a file stays in use, the message says why, and nothing changes: the installed version stays.

## 0.9.0-rc.5 — Setup can't hang on "Closing C.A.R.S. Control" (2026-10-16)

- The rc.3 → rc.4 update stopped at "Closing C.A.R.S. Control" and couldn't be closed. Setup checks which copies of C.A.R.S. are running; one exited at the moment it was checked, and .NET Framework threw an error there that wasn't handled. The error was lost because the step ran in the background, so the window kept waiting. Found by reading the stuck Setup's memory with the debugger. Setup now tolerates processes exiting while it looks at them, and any unexpected failure ends on "Setup couldn't finish" with the reason, logged to %TEMP%\CarsControl-Setup.log, instead of hanging.

## 0.9.0-rc.4 — Epic retest fixes (2026-10-16)

- Moving an Epic game onto an earlier copy of it (for example the one rc.1 left on D:) is no longer refused as "holds something else". When nothing conflicts and at least 90% of its files are the game's, the copy is reused and only the files the game created there (crash reports, logs) are removed first; the move window says how many.
- **Remove old copy** works after you've played from the new place: files the game writes into its new folder no longer make the copies "differ". Every file of the old copy must still be in the new one with identical contents (the admin helper checks the same way).

## 0.9.0-rc.3 — the update itself (2026-10-15)

- **Setup exits when its window closes.** After an update, Setup kept running invisibly (a hidden helper window kept it alive), which locked its own file. The next self-update then stopped with "Access to the path 'CarsControl-Setup-….exe' is denied". Reproduced with rc.1's Setup in a sandbox; rc.3's exits with code 0.
- Self-update skips old downloads it can't delete yet instead of failing, and reports any file-access problem as a status line rather than an error dialog.

## 0.9.0-rc.2 — fixes from the owner's rc.1 test (2026-10-15)

- **Epic moves updated for the 2026 launcher** (to be confirmed by the owner's retest). Moving Fall Guys made Epic download it again into the old folder: current launchers keep a third record (Epic Online Services' install helper, `.egi`) and rebuild the other two from it. Moves now switch all three records (backed up, all-or-nothing), treat Epic's background services as the launcher being open, and refuse records in an unknown format.
- **The original is no longer deleted during a move.** It stays until you've played the game from its new place; then **Remove old copy** on the Games row compares both copies and removes it (admin approval for Program Files). If Epic switches a game back, the row offers **Remove unused copy** instead.
- Epic games show last played as *not reported* rather than *never*, and no longer count as unplayed.
- Crash analysis explains what the blamed module means and what to try (for example dxgkrnl.sys: Windows' graphics kernel, usually the graphics driver or a GPU overclock).
- Sensors: every reading has a unit (fan power %, multipliers, memory GB, RAM timings ns), lists sort Core #2 before Core #10, fan power settings and core multipliers are named as such.
- Fixed an error when right-clicking a game on the Games tab.

## 0.9.0-rc.1 — safer moves, guided setup and reliability (2026-10-15)

Private release candidate for the owner test checklist; pre-releases are opt-in. The installed copy is not changed by building or publishing this candidate.

**Storage and safety**
- Epic games can be copied to another library with per-file SHA-256 verification, transactional launcher records and validated helper cleanup. Resume repairs corrupt partial files and budgets only remaining space. A cancelled or failed move reports where the usable copy remains.
- Salad is permanently protected across app installation, updates, uninstall, startup, menu changes, graceful-close requests and folder moves. Protection cannot be removed by an empty or synced protection list.
- Backups reject destination junctions and symbolic links, keep existing files if a source subtree is unreadable, and refuse linked retention folders. Malformed synced preference values are skipped individually.

**Setup and explanations**
- Seven-step first-run setup stages PC-profile recommendations, automation, optional privacy choices, backup/sync, sensors and alerts; Finish previews the choices and Settings offers exact setup-settings undo (the Undo buttons only appear when there's something to undo). Skip keeps existing preferences.
- Startup changes re-check what a shortcut or scheduled task really runs before changing it, so a renamed Salad entry is still refused; tasks whose actions can't be read are left alone.
- More section descriptions, table-header help, Settings/Home/treemap tooltips, Privacy/Cleanup recommendation badges and accessible control names. Settings reading width is capped on wide windows.
- Notice close buttons clear their bound messages, checkbox/radio labels wrap, release notes display readable text, and Follow Windows reapplies the requested backdrop after theme changes.

**Health and update fixes**
- Crash analysis can request a validated, user-readable copy of a minidump through the helper and shows the debugger's actual error.
- NVMe/ATA health adds accurate units, wear, power-on hours, written data and unsafe shutdowns; each drive card says where its numbers came from, or why there are none (USB drives that don't report, disks that need one admin approval). CPU/board summaries filter implausible probes and identify unknown fan connections without guessing their wiring.
- Broken winget catalog versions explain the mismatch, support skipping that version and offer the app's own updater.
- Windows Update COM stays on one persistent STA with explicit interface release; cancellation and shutdown stop queued/delayed work and observe background failures. Hidden Health stops polling, GPU reads leave the UI thread, and WMI results are detached before native resources are disposed.

**Development and packaging**
- UI/performance audits use isolated preferences, history, caches and downloads, simulated sign-in registration, a dry-run helper, and no real phone alerts. They run on the TV without activation.
- Reproducible Release startup/memory/CPU measurements and optional ReadyToRun comparisons; release output deletion is restricted to named repository artifact folders. Third-party notices travel with the payload; unused Mono.Posix native helpers are excluded.
- Follow Windows coalesces color notifications and reapplies a theme only when it changes, avoiding repeated tray layout/accessibility rebuilding. Performance commands require UI acknowledgement so a stalled page cannot produce a misleading measurement.

**Still to verify before 0.9.0 stable**
- Owner: self-update from 0.8.0, real Epic move and launcher test, elevated crash/sensor/drive checks, and focused Follow Windows transparency.
- The 250 MB browsing-memory target remains open; measured working set exceeds it. See `docs/dev/PERFORMANCE.md` for measured startup and CPU results.
- Repository remains private; existing screenshots and history require sanitizing before any public release.

## 0.8.0 — installer, Health, apps, storage and backups (2026-10-14)

The first release with a real installer, and the biggest since round 1: three new pages, four new tabs on the Apps and Storage pages, and everything needed to install, update and understand it.

**Install and update**
- **`CarsControl-Setup-<version>.exe`**: one themed file that carries the app, the admin helper and their libraries. It installs for the whole PC with one administrator prompt (the rest runs as you), installs the .NET 10 runtime from Microsoft if it's missing (SHA-512 checked), verifies every file, swaps folders with rollback, adds the Start menu entry and an *Apps* uninstall entry, and offers Update, Repair and Uninstall (keeping or removing your data). Command-line switches for scripts: `--install`, `--update`, `--repair`, `--uninstall`, `--silent`, `--keep-data`, `--remove-data`.
- **Self-update** (Settings › About & updates): checks GitHub Releases daily (never while you play), downloads the new installer, checks it against `SHA256SUMS.txt`, shows the release notes and waits for **Update now**. Works through your `gh` login while the repository is private; pre-releases are opt-in.
- **First-time setup** inside the app (this PC's holds, what runs by itself, privacy quick picks, phone alerts); re-run from Settings.
- **GitHub Actions**: CI builds and tests every push; a `v*` tag builds the installer and publishes the Release.
- Full [install guide](docs/install.md), [user guide](docs/user-guide/README.md), [security guide](docs/security.md), [building](docs/building.md) and [contributing](CONTRIBUTING.md) docs.

**Health** (new page)
- **Overview** with specs (CPU, the real GPU, memory modules, board, BIOS, Windows), battery on laptops, a **shareable web-page report** (user name, PC name and paths can be removed), and the **safety net**: a restore point before removing old drivers, driver and feature updates and privacy batches (turn System Protection on from here).
- **Sensors**: the graphics card through NVML (no admin), and CPU, board and fans through LibreHardwareMonitor and the optional **PawnIO** driver (installed on request from a SHA-256-pinned release).
- **Drives**: Windows' health status plus temperature, wear, power-on hours and error counts.
- **Crashes**: the blue-screen timeline with stop codes and minidumps, **WinDbg analysis** (installed on request) of what each crash blames, unexpected shutdowns, and app crashes grouped by app.
- **Sleep & boot**: exact **freeze** detection from the clock delta, what stopped sleep or woke the PC, drivers that blocked power transitions, **what's keeping the PC awake**, start-up, sign-in and shut-down times with the slowest apps and drivers.
- **Drivers**: every driver with version, date and signature; NVIDIA versions beside the crash dates; removal of **old copies in the driver store** (never one in use or the newest).
- **Activity** (what's using my PC): top processes by processor, graphics, memory or disk with the owning app, Open folder and End; Windows, security software, remote access and **Salad** are labelled and never offered to end.
- **Network**: Proton VPN connected *and carrying traffic*, Tailscale peers (direct or relayed), strict-NAT warning, TCP probes to your devices, the exit-node safeguard, and every adapter with the one internet traffic leaves through. A VPN chip on Home.
- **Restart when I'm away**: finishes a pending restart at a quiet time (04:00+, 30 minutes idle, not gaming), with a five-minute countdown and Cancel.
- **Phone alerts** for temperatures and failed backups; a 30-minute health watch feeds Home.

**Apps**
- **Get apps**: search winget and the Microsoft Store, install **for you** or **for everyone**, save/restore **app lists** for a new PC.
- **Startup**: apps, services and scheduled tasks, using the same switches as Task Manager, with exact undo and a protected list (VPNs, security, remote access, Salad…).
- **Right-click menu**: see and hide what apps added (for your account only, undoable); optional **Folder size in C.A.R.S.** and **Scan this drive in C.A.R.S.** entries.
- **Refresh › Windows Update**: pause, keep drivers out of Windows Update, delay feature updates, active hours, hide and show updates, recent history.

**Storage**
- **Duplicates** (size → partial → full hash; keeps your choice, recycles the rest; never touches app, game, Windows, vault or online-only files), **Junk** (old installers, empty folders, huge logs, game setup leftovers), **Move folders** (copy, verify, replace with a link, undoable), **move a Steam game** to another library, and **guided removal of an old Windows install** on another drive.

**Backup & sync** (new page)
- **Backups** to a folder (e.g. Proton Drive), a **USB drive recognised by its volume ID** (runs when plugged in, says when it's safe to unplug) or another disk, on a schedule while you're away. Incremental; changed and deleted files kept for 30 days; warns when the backup shares a disk with its source.
- **Your setup**: your choices, ignored updates, protected apps and app list synced through a folder (newest wins per part, asks on conflict) or moved by file.
- **Your PCs**: a status board of every PC running C.A.R.S. (version, updates, free space, health, last backup).

**Everyday**
- **Home** puts **Needs attention** first (approvals, restart pending, new version, drive health, crashes, failed backups…) with Gaming, admin and VPN chips.
- **Settings** is two panes (a section list and one section at a time) with new Health and About & updates sections.
- **History** has kind chips, a date range and **Export CSV**; **Refresh** shows app icons and release-note links.
- Navigation is Home, Apps, Refresh, Storage, Cleanup, Health, Privacy, Backup & sync, History, Settings (Ctrl+1 … Ctrl+9, Ctrl+0).
- The Privacy page's **Turn all on** leaves the Taskbar group (preferences, not privacy) alone.

**Under the hood**
- 33 typed, validated admin-helper operations (14 new); every one has a dry run and unit tests, and the full list with what each accepts is in [docs/security.md](docs/security.md).
- 750+ automated tests; a keyboard audit walks every page and tab, and an error log that de-duplicates and rotates at 1 MB.

**Known limits**
- The installer isn't code-signed yet, so SmartScreen warns on first run (check the SHA-256).
- Epic games can't be moved from Storage (Steam games can); use the launcher.

## Round 5 — everyday convenience (2026-10-13)

**New**
- **Scheduled maintenance** (Settings › Scheduled maintenance; default Sunday 03:00): chosen update sources and cleanup categories run as you while you're away. Missed runs catch up once after you've been idle; never during a game or while something is full screen. Home shows the last and next run; History logs each run.
- **Approval queue:** admin parts of unattended runs never put a UAC prompt on an empty screen. They wait until you're back: one toast with **Approve**, a card on Home or the tray, and one prompt runs them all.
- **Gaming mode:** turns on when a game from your Steam, Epic or other libraries starts (5-second check, off 30 s after it closes) or by hand (Home, tray, palette). Pauses C.A.R.S.'s background checks and maintenance, Windows Update and Windows Search indexing, and frees standby memory; resumes everything afterwards and logs the session. Its admin parts use a session approved once at sign-in. Always-on Steam tools (Wallpaper Engine, SteamVR, …) don't count, and Settings has "Not a game" for anything else.
- **Phone alerts** through ntfy: maintenance results and failures, admin items waiting, low disk space, a drive filling fast, blue screens and unexpected shutdowns, drive health (ready for round 6) and a weekly updates-waiting reminder. Random topic with a QR code, optional token stored encrypted, quiet hours, a switch per event. Messages carry counts, drive letters and times only.
- **Command palette:** Ctrl+K, or Ctrl+Alt+Space from any app (changeable, with a shortcut recorder). Fuzzy search over actions, pages, apps, games, big files and settings; → for more actions; frequent picks move up.
- **Privacy page:** 37 switches in seven groups with live state, exact undo and "Windows default"; remove Store add-ons for your account; who used your camera, microphone and location.
- New allow-listed helper operations: Windows Update pause/resume, standby list purge, Windows Search stop/start, privacy switches by id.
- Settings has a jump bar for its sections; Ctrl+1 … Ctrl+8.

**Under the hood**
- Update and cleanup runs moved out of the Refresh and Cleanup pages into shared runners, so every feature uses the same verification, History records and "done means gone".

## Round 4 — polish and foundations (2026-10-12)

**Fixed**
- **Black text on some pages.** Pages inside the sidebar's frame don't inherit the window's text color, so plain text fell back to black. Every page now sets it.
- **pip updates that never took effect.** Packages installed with `--user` were "updated" through the admin helper, which upgraded the shared copy instead. They now update as you, and every pip update re-checks the version you actually use.
- **Refresh: done means gone.** Finished updates leave the list (they're in History) and only come back if a check finds them again. Microsoft Store / Microsoft 365 rows untick and say when they ran.
- **"Already up to date" isn't a failure** (Windows Update "nothing left to install", winget "no newer version"). WSL is offered by one source only.
- **Sidebar:** the gradient separator bars are gone, and the selection marker follows the accent.
- History columns fit, with tooltips, readable "Via" names, search and an output pane that appears when you pick a row.
- Empty tooltips, focus outlines around whole pages, "Close zen first", "Measured with admin rights" before measuring, an empty Version in app details, double sort arrows in Explore, and a red first treemap tile.
- Unexpected errors are logged and shown instead of closing the app.

**New**
- **Settings › Appearance:** dark / light / follow Windows, translucent or solid, C.A.R.S. teal / Windows / custom accent.
- **Settings › This PC:** "hold back driver updates" and "keep crash evidence", each with a reason (were hard-coded).
- **settings.json** for all preferences (migrated from cars.db once).
- Themed dialogs instead of Windows message boxes.
- Generic defaults for a fresh install (protected apps, PC name, Old Windows drive); MIT license; docs split into README, `docs/testing.md` and `docs/my-setup.md`.
- `tools\ui-audit\Capture-All.ps1`: every screen in every look and size, with a contact sheet.

## Round 3 — Storage overhaul and leftovers (2026-10-12)

- Drive sizes use space on disk (Fast scan read WizTree's *Size*, which counts online-only iCloud files; the folder scan skipped cloud folders entirely).
- Cloud files: "Free up space" instead of Recycle (which would delete from the cloud); Cryptomator vault files protected.
- Storage: multi-drive Big & old files with type chips, filters, search and one remembered sort; Games filters; Explore Back/Forward with the mouse side buttons.
- Steam uninstalls wait for Steam, then check leftovers (shader cache, Workshop, `LocalLow\{company}\{game}`, saves); History › Find leftovers.
- Fixes: `pagefile.sys` offered for Recycle, empty tooltips, "46.8 y" for 1980 timestamps, admin count mismatch, accessibility of drive cards.

## Round 2 — speed and fixes (2026-10-12)

- Scrolling everywhere (WPF-UI wrapped pages in an unbounded scroll viewer), pages kept alive, first paint ~1.5 s, Apps list merges in place, folder sizes reused for 7 days.
- One source of truth for update counts (UpdateCenter) with per-source cache lifetimes.
- Fast scan through WizTree; spinning drives scanned with one thread; scans remembered; file ownership before Recycle; Steam last-played and hours.
- Per-app actions and details panel; Home dashboard; click and scroll audits.

## Round 1 — first version (2026-10-11)

- Apps (inventory from registry, MSIX and winget), Refresh (winget, Windows Update, Store, Office, WSL, PowerShell modules, npm, pip), bulk uninstall with leftover quarantine, Storage, Cleanup, History, Settings, tray and background checks.
- Elevated helper: one UAC prompt per session, typed operations only, verified parent, locked data folder.
