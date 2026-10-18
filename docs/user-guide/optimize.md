# Optimization

![Optimization](../images/optimize.png)

Windows settings that change how fast or smooth the PC feels. Every switch says **how sure we are it helps**, can be switched back to exactly what was there before, and leaves security alone.

## How sure is each one?

Every row carries a label (hover it for the reason and the source):

| Label | Means |
|---|---|
| **Documented** | Microsoft describes the effect. |
| **Measured** | Independent tests show a consistent effect. |
| **Depends on your PC** | Tests disagree: it helps some PCs and slows others. Only worth changing if you can measure the difference. |
| **Your preference** | No speed difference worth measuring; it's about how Windows looks or feels. |

**Recommended** tags follow the kind of PC chosen in **Settings › This PC** (Desktop, Laptop or Gaming PC). "Depends on your PC" switches are never recommended.

## Apply recommended…

Lists every change recommended for your kind of PC first, then applies them when you confirm: your-account changes right away, admin ones in one helper session (one prompt), after a restore point when System Protection is on. Each change can still be switched back on its own row.

## What's on the page

**Power**

* **Power mode**: Windows' own Best power efficiency / Balanced / Best performance. It only exists on Windows' **Balanced** plan; on another plan (High or Ultimate Performance, a maker's plan) the row offers **Use the Balanced plan** and, afterwards, **Back to the previous plan**.
* **Hibernation**: off deletes the hibernation file (it reserves space based on your RAM) and also turns off fast startup. Recommended off on desktops, kept on laptops. Admin.
* **Full shutdown (no fast startup)**: shut down really shuts down instead of half-hibernating; Microsoft lists fast startup as a cause of update and driver problems. Admin.

**Gaming**

* **Game Mode**: on by Windows' default; the row only turns it back on if something switched it off.
* **No background game recording**: stops the Game Bar recording all the time for "what just happened" clips. Win+Alt+R still records on demand.
* **Hardware-accelerated GPU scheduling**: depends on your PC; takes effect after a restart. Admin.
* **No mouse acceleration**: turns off "Enhance pointer precision" for consistent aim.

**Visual effects**: window and menu animations, transparency effects. A matter of taste on a modern PC.

**Background and start-up**: start your startup apps right after sign-in instead of after Windows' short delay (depends on your PC). Which apps start is on [Apps › Startup](apps.md).

**Network**: share Windows updates only with PCs on your own network, not strangers' PCs over the internet. Admin; Settings will say it's managed by your organization.

## Test it on this PC

Switches that could change frame rates (GPU scheduling, background recording, transparency) have **Test it on this PC**. Start your game, go to a spot you can repeat, and press **Record the first minute**: after an admin approval and a 10-second countdown, C.A.R.S. records a minute of frame times, makes the change, and you record a second minute in the same spot. The result compares average FPS and 1% lows (what stutter feels like) and only says **Better** or **Worse** when the difference is clearly bigger than how much the game varies by itself, and at least 2%. Otherwise it says **No clear difference**, and the safe choice is to undo. **Keep** or **Undo** right there.

Changes that take effect later (GPU scheduling after a restart, background recording the next time a game starts) pause after the first minute: once the change is in effect, start the same game and press **Continue the test** on the Optimization page.

Recording uses Intel PresentMon (free, open source), downloaded once from its official release and checked before every use. Windows only lets administrators read frame times, so it runs in the admin helper; only the frame-rate summary comes back.

## Not here, on purpose

* Network "optimizers" (Nagle, TCP autotuning), RAM cleaners, disabling Windows services wholesale: measurements show no gain, or they cause problems.
* Memory integrity (VBS), Defender, SmartScreen, the firewall, UAC and Windows Update security: they protect the PC. Memory integrity can cost a few percent in some games, but C.A.R.S. won't switch it off.
* The Ultimate Performance plan: no measured gain over Balanced with Best performance.

## Undo

Switch any row off to put back exactly what was there before C.A.R.S. changed it. After **Use the Balanced plan**, **Back to the previous plan** returns to the plan you had; power mode and animations are simple choices you can set back yourself. Every change is in [History](history.md).
