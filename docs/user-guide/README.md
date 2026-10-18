# C.A.R.S. Control user guide

One page per area of the app, in the order of the sidebar. If you haven't installed it yet, start with the [install guide](../install.md).

| Page | Shortcut | What it's for |
|---|---|---|
| [Home](home.md) | Ctrl+1 | What needs you right now, then updates, space, drives and recent activity. |
| [Apps](apps.md) | Ctrl+2 | Everything installed, **Get apps** (search and install), what **starts with Windows**, the **right-click menu**. |
| [Refresh](refresh.md) | Ctrl+3 | Updates from winget, Windows Update, WSL, PowerShell modules, npm and pip; **Windows Update controls**. |
| [Storage](storage.md) | Ctrl+4 | Drive explorer, big and old files, duplicates, junk, games (and moving them), moving folders, the **old Windows** install. |
| [Cleanup](cleanup.md) | Ctrl+5 | Temp files, caches and other safe-to-clear leftovers. |
| [Health](health.md) | Ctrl+6 | Specs, temperatures, drive health, blue screens, freezes, boot times, drivers, what's using the PC, network and VPN. |
| [Privacy](privacy.md) | Ctrl+7 | 38 switches for telemetry, ads and AI extras, each with exact undo. |
| [Optimization](optimize.md) | Ctrl+8 | Power mode, hibernation, gaming features, visual effects and start-up, each labelled with how sure we are it helps, with exact undo. |
| [Backup & sync](backup.md) | Ctrl+9 | Backups to Proton Drive, a USB drive or another disk; your setup on every PC; your PCs' status. |
| [History](history.md) | Ctrl+0 | Every operation with its full output; restore leftovers. |
| [Settings](settings.md) | Ctrl+, | Appearance, schedules, Gaming mode, phone alerts, this PC's holds and more. |
| [Automation](automation.md) | | The things that happen by themselves: scheduled maintenance, the approval queue, Gaming mode, phone alerts, restart-when-away, the command palette. |

## Ideas that apply everywhere

### Help where you need it

* Hover over any button, switch or number for a **tooltip** that says what it does or means, and what's good or bad.
* Sections with a **(?)** button open a short explainer: *what it is · what C.A.R.S. does · what you should do · whether it's safe and can be undone*. **Ctrl+K** then "what is…" (for example *what is PawnIO*) finds the same explainers.
* A **Recommended** tag marks the suggested choice for your kind of PC (Desktop, Laptop or Gaming PC, picked in Settings › This PC), with the reason in its tooltip. **Apply recommended settings…** in Settings shows every change before making it.

### Nothing happens until you say so

Every page that can change something measures first and shows you the list. Ticked boxes are the safe defaults; risky ones (emptying the Recycle Bin, shader caches, pip upgrades) start unticked. Most pages have a **Dry run** switch that shows exactly what *would* run, command by command, without running it.

### Who does the work: you, or the admin helper

The app always runs as **you**. Things that belong to your account (your apps, your files, your registry, your settings) are done as you with no prompt. Machine-wide work (Windows Update, apps installed for everyone, system cleanup, drivers, services) goes to a small separate **admin helper**. The first time it's needed in a session, Windows shows its normal administrator prompt; the helper then stays until you quit, it has been idle for 15 minutes (configurable in Settings › Admin helper), or the app closes. Rows that need it say **Admin**. Details: [security.md](../security.md).

**Unattended work never shows a prompt.** Scheduled maintenance, Gaming mode and background checks use an admin session only if one is already open. Otherwise their admin parts wait in an **approval queue** on Home until you're back and approve them all at once.

### Protected things

Some things are deliberately hard to hurt by accident:

* **Protect** an app (Apps › right-click › Protect) and it's guarded against uninstalling and bulk actions. A short built-in list (runtimes, drivers and C.A.R.S. itself) is protected from the start; **Settings › Protected apps** is yours to edit.
* **Holds** (Settings › This PC): "hold back driver updates" and "keep crash evidence" for when you're chasing a problem.
* **Salad** (if you use it) is recognised everywhere: never paused, never offered to end, never changed.
* Some services, tasks and startup entries are shown but **can't be changed** (VPN, remote access, graphics driver controls, security).

### Undo

* Cleanup and leftovers go to the Recycle Bin or a **quarantine** you can restore from History for 30 days.
* Every privacy switch, startup change and right-click change remembers what was there first; switching it back restores exactly that.
* Backups keep older versions of changed and deleted files for 30 days.
* A **restore point** can be made before risky changes (Settings › Health, on by default when System Protection is on).

### Keyboard

* **Ctrl+K** opens the command palette from inside the app; **Ctrl+Alt+Space** (changeable) opens it from any program. Type part of anything: an action, a page, an app, a game, a big file, a setting. **Enter** runs the top result; **→** shows more actions; what you use most moves up.
* **Ctrl+1 … Ctrl+9, Ctrl+0** switch pages (in sidebar order); **Ctrl+,** opens Settings. **Tab** and **Shift+Tab** move through everything; every control has a name for screen readers.
* Storage › Explore: mouse side buttons or **Alt+←/→** for Back/Forward, **Backspace** or **Alt+↑** for the parent folder, **Enter** to open.

### The tray icon

C.A.R.S. can live in the notification area: it checks for updates in the background, runs scheduled work and shows toasts. Closing the window leaves it there; **Exit** in the tray menu quits for real. "Start with Windows" (Settings › Background work) starts it hidden.
