# Testing C.A.R.S. Control

## Automated

```
dotnet test tests\CarsControl.Tests
```

About 750 unit tests cover the parts that decide what happens to your PC. They don't need administrator rights and don't change the PC (registry tests use a scratch key under `HKCU\Software\CarsControlTest` and remove it; file tests use a temp folder; the installer's tests run in a sandbox).

| Area | What's pinned down |
|---|---|
| Updates and uninstall | Routing by owner (winget, pip location, Windows Update outcomes), uninstall planning, leftover safety, "done means gone", file ownership (what can be recycled), WizTree import, scan caching, settings migration |
| The admin helper | Every operation's allow-list and rejection cases (injected pip flags, bad winget ids, HKCU uninstalls, paths, GUIDs…), the protected lists for services, tasks and startup entries, driver deletion rules, the old-Windows target rules against fake drive trees, Windows Update settings and PawnIO's pinned hash |
| Everyday automation | Maintenance scheduling (missed runs, daylight saving, never during a game), game detection, alert content (no paths or names) and quiet hours, the ntfy request, DPAPI, restart-when-away decisions |
| Health | Parsers for Windows events (blue screens, Kernel-Power, Kernel-General time deltas, sleep and wake, boot performance), `powercfg`, `pnputil`, NVML and LibreHardwareMonitor readings, drive health, Tailscale JSON and `netcheck`, VPN carrying-route detection, the report and its redaction, the restore-point routine |
| Apps | winget search ranking and list import, startup entry/service/task rules and their undo, right-click menu entries |
| Storage | Duplicate matching on temp trees, junk rules, the copy-verify-switch movers (including resume after an interrupted copy), the old-Windows rules |
| Backup | The incremental engine (versions, purge, cancel, validation), setup bundles (hostile files, ranges, never the token), setup-sync decisions, fleet status files |
| Installer | Payload hashes, ARP entry, folder swap with rollback, .NET runtime metadata and SHA-512, version ordering, release parsing, checksum verification |
| Privacy | Every switch (scopes, no security settings, apply/undo round trips on a scratch key, helper allow-list) |

## Test copies next to the installed app

Set these before starting a build from `src\CarsControl.App\bin\Debug\...\CarsControl.exe`:

| Variable | Effect |
|---|---|
| `CARS_DEV_INSTANCE=1` | Runs next to the installed copy (separate single-instance names), titled **TEST COPY**, on the secondary monitor, never taking focus. |
| `CARS_DEV_MONITOR` | Which monitor: `tv` (default: the secondary that doesn't touch the primary), `adjacent`, or a device name. |
| `CARS_HELPER_UNELEVATED=1` | The helper starts without UAC and accepts only dry runs, so nothing admin can happen. |
| `CARS_DUMP_VISUALS=1` | Writes the window's visual tree and theme resources to `%TEMP%\CarsControl\visual-tree.txt` (for finding template parts). |
| `CARS_DEV_FAKE_APPROVALS=1` | Shows three sample admin items on Home (never saved), for screenshots. |
| `CARS_DEV_MAINTENANCE=1` / `_ALERTS` / `_HOTKEY` / `_UPDATE` / `_BACKUP` | Let a test copy run scheduled maintenance, send phone alerts, take the global shortcut, check for app updates or run backup jobs. Off by default: test copies share your settings. |

`CarsControl.exe --page palette` opens the command palette (also handy for a Start menu or Stream Deck shortcut).

Test copies share `%LOCALAPPDATA%\CarsControl` (history, scans, settings) with the installed app.

## UI tools (`tools\ui-audit`, Windows PowerShell 5.1)

- **`Capture-All.ps1`** photographs every page, tab, state and dialog in dark/light, translucent/solid and three window sizes, then writes a contact sheet to `tools\ui-audit\screens\<timestamp>\index.html`. `-Quick` does one pass. It only navigates and cancels: nothing is installed, removed or cleaned. `settings.json` is backed up and restored. Anything the app logged to `errors.log` during the run is listed at the end: that's a bug.
- **`Capture-Docs.ps1`** takes the screenshots used in `docs\images` (one consistent size, dark theme) straight into that folder. Review them before publishing: they show your real apps, drives and folder names.
- **`Audit-Ui.ps1`** checks that every list and page scrolls and clicks every safe control, recording what responded (deny-list for anything destructive). Report: `tools\ui-audit\last-report.txt`.
- **`Audit-Keyboard.ps1`** tabs through every page **and every tab** (focus order, accessible names, visible focus). Report: `tools\ui-audit\last-keyboard-report.txt`; the result line must say *clean*.
- **`Shoot.ps1`** takes one screenshot after optional navigation, tab, toggle, button and scroll steps; **`Shoot-Setup.ps1`** does the same for the installer's wizard in a sandbox; **`Stop-Dev.ps1`** closes test copies (they lock the build output).
- **`tools\CarsControl.Probe`** runs Core against the real PC without changing anything: `specs | gpu | sleep | drives | network | activity | reboot | report | crashes | search | startup | menu | junk | arp | msix | winget | inventory | check | plans | leftovers … | games | oldwin | scan <path> | wiztree | cleanup`.

## Needs a person (admin paths and real hardware)

Automated sessions can't elevate or plug in a USB stick, so these are checked by hand after each install. Tick them off on a fresh install and again after an upgrade.

**Install and update**
- [ ] Setup from the Release over the previous version: one administrator prompt; the app starts; settings, history and ignore rules are intact.
- [ ] Repair (same version) and Uninstall (keeping data, then removing it); the Start menu entry and *Settings › Apps* entry appear and disappear.
- [ ] Self-update: from a release candidate to the final release (**Update now**), including the checksum check; a tampered download is refused.

**Updates and uninstall**
- [ ] Settings › **Test admin helper**: exactly one UAC prompt; status `elevated = True`.
- [ ] Refresh › **Update selected**: one prompt for the whole batch; per-user items update without one; finished updates leave the list.
- [ ] Bulk-uninstall two throwaway apps (one per-user, one machine-wide): at most one prompt; the leftover review; quarantine, then History › **Restore**.
- [ ] A driver or feature update from Windows Update makes a restore point first (System Protection on).

**Health**
- [ ] Sensors: **Install PawnIO** (one prompt), then CPU and board temperatures and fan speeds appear.
- [ ] Drives: **Read temperature, wear and errors** (one prompt) shows each drive's numbers.
- [ ] Crashes: **Install WinDbg**, **Analyze** a minidump; the result names a module and is remembered.
- [ ] Sleep & boot: **Check now** and **Read start-up times** (one prompt) show data.
- [ ] Drivers: tick one old copy, **Remove ticked** (restore point first, one prompt); it disappears from the list.
- [ ] Restart when I'm away: with a pending restart and the time set a few minutes ahead, the five-minute countdown appears; **Cancel** stops it.

**Apps**
- [ ] Get apps: install one app **for me** (no prompt) and one **for everyone** (one prompt); save a list and open it again.
- [ ] Startup: switch one app off and on; it matches Task Manager's Startup tab both ways. Change one service and one task, then undo.
- [ ] Right-click menu: hide an entry (it's gone from Explorer's menu), show it again; add and remove the C.A.R.S. entries; check them next to a menu replacement such as Nilesoft Shell.

**Storage**
- [ ] Fast scan of each drive: at most one prompt; sizes match the drive's used space.
- [ ] Free up space on one cloud file: free space grows; the file still opens.
- [ ] Duplicates: find, keep one, recycle the rest; Junk: recycle one item and restore it from the Recycle Bin.
- [ ] Move a small Steam game to another library and start it from Steam; **Move folders**: move a folder and **Move back**.
- [ ] Epic: close the launcher and its tray icon, move Fall Guys to another drive from **Games > Move…**, reopen Epic and launch it. It must start without downloading or verifying; the `.item`, `LauncherInstalled.dat` and install-helper `.egi` all point at the destination, with `.cars-before-move` backups. Then **Remove old copy** (one admin prompt for Program Files). If Epic downloads instead, the row must offer **Remove unused copy** and the old copy must still be there.
- [ ] Old Windows removal: copy anything personal out first; remove one folder; confirm the live drive's own folders are untouched.

**Backup & sync**
- [ ] Back up Documents to the Proton Drive folder, then restore one file by copying it back from the destination; change a file and confirm the old version lands in `.cars-versions`.
- [ ] USB backup: plug the stick in, the backup runs and says it's safe to unplug; plug it into a different port (different letter) and it's still recognised.
- [ ] Your setup: change a setting on one PC and see it arrive on the other; change both and confirm it asks.
- [ ] Your PCs lists this PC with correct numbers.

**Background and helper**
- [ ] Close the window: it stays in the tray; tray › **Check for updates now** shows a toast.
- [ ] After **Exit**, `CarsControl.Helper.exe` is gone; with the app open, the helper exits after the idle timeout.
- [ ] `C:\ProgramData\CarsControl` is owned by Administrators.

**Scheduled maintenance, Gaming mode, alerts**
- [ ] Maintenance set 5 minutes ahead and the PC left alone: it runs, History has a "Maintenance" row; admin items it couldn't do produce one toast and, on **Approve**, one prompt.
- [ ] Gaming mode: sign in (one prompt about 20 seconds later); start a game (chip lights, Windows Update paused, `WSearch` stopped); close it (after about 30 s everything resumes).
- [ ] Phone alerts: install ntfy, subscribe to the topic, **Send test**; quiet hours hold an alert until they end.

**Privacy**
- [ ] An admin switch on and off ("No Widgets"): one prompt; the policy appears and disappears in `HKLM\SOFTWARE\Policies\Microsoft\Dsh`.
- [ ] A your-policy switch ("No Bing results in Start search") lands in your own `HKCU\Software\Policies`, not the admin account's.
