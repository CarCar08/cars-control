# Home

<img src="../images/home.png" alt="The Home page" width="800">

Home answers one question: **does anything need me right now?** The top of the page lists only things that want a decision; everything below it is a quick look at the state of the PC.

## Needs attention

Each item says what's wrong in a sentence and has one button that takes you to the fix. They appear and disappear by themselves:

* **Admin items waiting for your approval**: parts of scheduled maintenance or other unattended work that needed administrator rights. One button approves them all with a single Windows prompt. See [the approval queue](automation.md#the-approval-queue).
* **A restart is pending** (Windows Update, a driver, a pending file rename) and, if you've turned it on, when the PC will restart by itself. See [Restart when I'm away](automation.md#restart-when-im-away).
* **A new C.A.R.S. Control version is ready** (downloaded and checked; nothing installs until you press *Update now*).
* **A drive is almost full**, or filling fast.
* **A drive reports a health problem** (Health › Drives).
* **A blue screen or an unexpected shutdown** happened since you last looked.
* **Scheduled maintenance had failures** on its last run.
* **A backup hasn't finished cleanly**, or hasn't run in days.

Each item has a second button: **Snooze** (it goes away for a while and returns if the problem is still there) or, for things that can simply be forgotten such as waiting admin items, **Dismiss**. An item also disappears by itself as soon as its cause is gone.

## The header chips

* **Gaming mode**: on/off. See [Gaming mode](automation.md#gaming-mode).
* **Admin session active**: the admin helper is running (it closes by itself after being idle).
* **VPN**: shows whether Proton VPN is connected and carrying your traffic. Click it for details (Health › Network).

## Update everything

The big button at the top refreshes every update source that's gone stale, then installs every update that's ticked by default, with **at most one administrator prompt**. The cards beside it show:

* **Updates ready**: how many were found, across every source.
* **Safe to clean**: how much space Cleanup could free (temp files, caches) without touching anything you'd miss.
* **Installed apps** and **Games**: counts, opening Apps and Storage › Games.
* **Run now / Schedule**: runs your [scheduled maintenance](automation.md#scheduled-maintenance) immediately, and shows the last and the next run.

## Drives

A card per drive with used and free space, in space **on disk** (so online-only cloud files don't count). A drive turns amber when it's almost full. Click one to explore it in Storage.

## Recent activity

The last operations (updates, uninstalls, cleanups, game sessions, backups) with their results. **All history** opens [History](history.md).
