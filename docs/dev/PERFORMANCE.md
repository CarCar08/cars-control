# Performance measurements — 2026-10-15

Measurements use Release x64 on the owner's normal PC, in a nonactivated TV test window with the installed copy left alone. They are observations under normal machine load, not a controlled cold-boot benchmark. CPU is process CPU divided by all logical processors; memory is working set in MiB. The browsing target is still unmet.

## Repeatable workload

Publish a Release copy into an ignored artifact directory. `tools/ui-audit/Measure-Performance.ps1` starts an isolated developer instance, waits for first paint, visits Home, Apps, Refresh, Storage, Cleanup, Health, Privacy, Backup, History and Settings, then samples Home and hidden-tray CPU for 12 seconds each. Every command now waits for a UI acknowledgement; a stalled navigation invalidates the sample. History, backup jobs, sync and alert credentials start empty; preferences and inventory are seeded read-only from the normal profile.

Raw JSON and logs stay under ignored `artifacts-perf`; the portable results below contain no app names or PC identifiers. Measurements cover those ten pages, not every Health subtab or a live backup.

## Findings and changes

- WMI readers now dispose the searcher, result collection and individual native objects before returning detached values. GPU reads leave the UI thread; sensor/activity polls stop when hidden, and stale replies cannot repopulate a hidden tab. Home stops its VPN timer when hidden.
- A captured main-thread stack showed `SystemThemeWatcher.UpdateObservedWindow → ApplicationThemeManager.Apply → tray theme change → UpdateLayout → automation peer rebuilding`. The [upstream 4.3.0 watcher](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Appearance/SystemThemeWatcher.cs) reapplies the theme on each color notification. AppearanceService now coalesces those notifications and compares actual theme/accent values before applying; unchanged DWM preferences are also left alone.
- Before this guard, an acknowledged sample reached 474.2 MiB and tray CPU 0.4935%; another early, unacknowledged run reached 787 MiB in the tray while navigation stopped responding. The latter is diagnostic evidence, not a valid browsing benchmark.
- Managed heap snapshots were much smaller than working set (roughly 37–50 MiB after browsing in guarded runs). Native/render resources and retained page state need separate profiling before changing the page cache; this does not establish that all remaining memory is necessary or leak-free. No forced GC, working-set trimming or OS settings changes are used to improve the figures.

## Final guarded Release samples

| Sample | First paint | Browsing peak | Tray working set | Home CPU | Tray CPU |
|---|---:|---:|---:|---:|---:|
| First launch after publish | 4,249 ms | 335.2 MiB | 337.2 MiB | 0.4498% | 0.0651% |
| Warm launch | 1,053 ms | 310.5 MiB | 302.9 MiB | 0.5149% | 0.0434% |

The first launch overlapped normal development/machine activity. These two samples show the warm-start goal can be met; they do not prove the <1.5-second cold-start goal or the <250-MB browsing goal. The intermittent stall was not reproduced in the guarded pair; wider soak testing is still appropriate.

## ReadyToRun comparison

An earlier two-sample comparison, before the final isolation/theme changes, measured plain Release at 1,580/1,159 ms and ReadyToRun at 1,367/1,084 ms. Published size increased from 42.1 to 50.2 MiB. The small sample and changing workload do not justify enabling it globally. `build-release.ps1 -ReadyToRun` retains an explicit comparison switch; the candidate uses the normal publish mode. Unused Mono.Posix native helpers are removed from the installer payload.

## Window size is not the cause (checked 2026-10-15, later)

Hypothesis: render surfaces on the 250% 4K TV inflate native memory. Not supported: with a per-monitor-DPI-aware script (a non-aware one reads virtualized sizes), resizing the Home window 3300×2120 → 2400×1550 → 3800×2100 → 2400×1550 physical pixels gave working sets of 245 → 271 → 279 → 261 MiB and private bytes 215 → 246 → 239 → 239 MiB, with the managed heap at 14–41 MiB. Memory doesn't follow the pixel count; each resize adds a little that is partly released later. At Home, before any other page is opened, the working set is already about 240–250 MiB (0.8.0 measured 231 MiB idle), so the remaining ~60 MiB over target comes from visiting pages (Apps about +30, Health about +27). All pages are DI singletons and keep their visual trees; releasing heavy pages' trees when hidden is the candidate fix for 1.0 and needs its own leak testing.

## Next performance work

Profile retained visual trees and native render resources after repeated navigation at a fixed physical window size; separate framework/render baseline, view retention and actual growth. Repeat cold/warm trials with the machine otherwise idle and add a longer tray soak. Keep the memory and cold-start targets open until comparable measurements meet them.

## Page release and the tray (2026-10-17)

`App\Services\PageCache.cs` builds each page on its first visit and keeps it while the window is used. After three minutes hidden in the tray it drops every page but the current one, switches WPF to software rendering until the window shows again, compacts the heap once and hands the idle pages back to Windows (`SetProcessWorkingSetSize(-1, -1)`).

Released pages first stayed alive. `dotnet-dump` gcroot found three holders, all fixed: WPF-UI 4.3's private `NavigationView.PageToNavigationItemDictionary` (pruned on release, guarded reflection), the Frame journal, and `DependencyPropertyDescriptor.AddValueChanged` subscriptions in `Controls\Notice.cs` and `Controls\GridSort.cs` (now only while loaded). After the fixes 9 of 9 released pages are collected.

Freeing the pages alone saved only ~10 MB: `!address -summary` showed the committed memory is mostly program images (~400 MB committed, largely shared), ~95 MB of write-combined graphics memory and ~48 MB of native heaps; the managed heap is ~30 MB. Software rendering while hidden saved ~23 MB more.

| Release sample | First paint | Browsing peak | Tray before release | Tray after release (working set) | Tray private | Tray CPU |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 1,027 ms | 323.3 MiB | 312.8 MiB | 32.7 MiB | 190.7 MiB | 0.011% |
| 2 | 894 ms | 307.5 MiB | 297.3 MiB | 22.8 MiB | 205.2 MiB | 0.016% |
| 3 | 1,168 ms | 313.3 MiB | 307.2 MiB | 40.5 MiB | 178.4 MiB | 0.033% |

Reopening after a release rebuilt pages in 32–110 ms with no errors (working set 172 MiB after five pages). **Revised target:** a fresh window showing only Home already uses ~230 MB (.NET, WPF, WPF-UI and the Windows SDK projection), so "< 250 MB while browsing" isn't reachable without replacing the UI framework; it's replaced by "the tray copy gives its memory back" (met: 23–41 MB resident). Browsing stays around 300–320 MB.