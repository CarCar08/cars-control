using System.Text.RegularExpressions;
using CarsControl.Core.Util;

namespace CarsControl.Core.Updates;

/// <param name="Optional">Windows Update's BrowseOnly: listed under Settings › Windows Update › Advanced options › Optional
/// updates and never installed by Windows on its own (mostly old or generic drivers offered "in case a device needs it").</param>
/// <param name="Recommended">AutoSelectOnWebSites: Windows Update would pick it itself. Old generic drivers come back as
/// neither optional nor recommended (seen on a PC offered 14 Intel/Realtek drivers from 1968–2016, all with this false).</param>
public sealed record WuUpdate(string UpdateId, string Title, bool IsDriver, long MaxDownloadBytes, string? KbArticle, bool Optional = false, bool Recommended = true);

/// <summary>One line of Windows Update's own history. Result: 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted.</summary>
public sealed record WuHistoryEntry(DateTime Date, string Title, bool IsUninstall, int Result, int HResult)
{
    public string ResultText => Result switch { 2 => "Installed", 3 => "Installed with errors", 4 => "Failed", 5 => "Aborted", 1 => "In progress", _ => "Unknown" };
}

/// <summary>Windows Update Agent (WUA) COM API. Searching works unelevated as you (verified 2026-10-11,
/// ~10 s); downloading/installing needs admin and runs in the helper.</summary>
public static partial class WindowsUpdateApi
{
    public static void Stop() => StaWorker.Stop();
    public const string Criteria = "IsInstalled=0 and IsHidden=0";

    public static Task<IReadOnlyList<WuUpdate>> SearchAsync(CancellationToken ct = default) =>
        RunSta(() =>
        {
            using var com = new ComScope();
            dynamic session = com.Own(CreateCom("Microsoft.Update.Session"));
            session.ClientApplicationID = "C.A.R.S. Control";
            dynamic searcher = com.Own(session.CreateUpdateSearcher());
            dynamic result = com.Own(searcher.Search(Criteria));
            dynamic updates = com.Own(result.Updates);
            var list = new List<WuUpdate>();
            int count = updates.Count;
            for (var i = 0; i < count; i++)
            {
                list.Add(ToUpdate(com.Own(updates.Item(i)), com));
            }
            return (IReadOnlyList<WuUpdate>)list;
        }, ct);

    /// <summary>Updates that were hidden so Windows stops offering them (no admin needed to list).</summary>
    public static Task<IReadOnlyList<WuUpdate>> SearchHiddenAsync(CancellationToken ct = default) =>
        RunSta(() =>
        {
            using var com = new ComScope();
            dynamic session = com.Own(CreateCom("Microsoft.Update.Session"));
            session.ClientApplicationID = "C.A.R.S. Control";
            dynamic searcher = com.Own(session.CreateUpdateSearcher());
            dynamic result = com.Own(searcher.Search("IsInstalled=0 and IsHidden=1"));
            dynamic updates = com.Own(result.Updates);
            var list = new List<WuUpdate>();
            int count = updates.Count;
            for (var i = 0; i < count; i++) list.Add(ToUpdate(com.Own(updates.Item(i)), com));
            return (IReadOnlyList<WuUpdate>)list;
        }, ct);

    /// <summary>Hides or shows one update (helper only: changing it needs admin).</summary>
    public static Task<bool> SetHiddenAsync(string updateId, bool hidden, CancellationToken ct = default) =>
        RunSta(() =>
        {
            using var com = new ComScope();
            dynamic session = com.Own(CreateCom("Microsoft.Update.Session"));
            session.ClientApplicationID = "C.A.R.S. Control";
            dynamic searcher = com.Own(session.CreateUpdateSearcher());
            dynamic result = com.Own(searcher.Search(hidden ? "IsInstalled=0 and IsHidden=0" : "IsInstalled=0 and IsHidden=1"));
            dynamic updates = com.Own(result.Updates);
            int count = updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic u = com.Own(updates.Item(i));
                dynamic identity = com.Own(u.Identity);
                if (!string.Equals((string)identity.UpdateID, updateId, StringComparison.OrdinalIgnoreCase)) continue;
                u.IsHidden = hidden;
                return true;
            }
            return false;
        }, ct);

    /// <summary>Windows Update's own history, newest first. Works without admin.</summary>
    public static Task<IReadOnlyList<WuHistoryEntry>> HistoryAsync(int max = 60, CancellationToken ct = default) =>
        RunSta(() =>
        {
            using var com = new ComScope();
            dynamic session = com.Own(CreateCom("Microsoft.Update.Session"));
            dynamic searcher = com.Own(session.CreateUpdateSearcher());
            int total = searcher.GetTotalHistoryCount();
            var list = new List<WuHistoryEntry>();
            if (total == 0 || max <= 0) return (IReadOnlyList<WuHistoryEntry>)list;
            dynamic entries = com.Own(searcher.QueryHistory(0, Math.Clamp(max, 0, total)));
            int count = entries.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic e = com.Own(entries.Item(i));
                var title = (string?)e.Title;
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new WuHistoryEntry(((DateTime)e.Date).ToLocalTime(), title, (int)e.Operation == 2, (int)e.ResultCode, (int)e.HResult));
            }
            return (IReadOnlyList<WuHistoryEntry>)list.OrderByDescending(h => h.Date).ToList();
        }, ct);

    private static WuUpdate ToUpdate(dynamic u, ComScope com)
    {
        string? kb = null;
        dynamic articles = com.Own(u.KBArticleIDs);
        dynamic identity = com.Own(u.Identity);
        if (articles.Count > 0) kb = "KB" + (string)articles.Item(0);
        bool optional, recommended;
        try { optional = (bool)u.BrowseOnly; }
        catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException) { optional = false; }
        try { recommended = (bool)u.AutoSelectOnWebSites; }
        catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException) { recommended = true; }
        return new WuUpdate((string)identity.UpdateID, (string)u.Title, (int)u.Type == 2, Convert.ToInt64(u.MaxDownloadSize), kb, optional, recommended);
    }

    public sealed record InstallOutcome(bool Ok, int ResultCode, bool RebootRequired, string Message);

    /// <summary>Downloads and installs the given update ids (helper only). ResultCode: 2 = succeeded,
    /// 3 = succeeded with errors, 4 = failed, 5 = aborted.</summary>
    public static Task<InstallOutcome> InstallAsync(IReadOnlyCollection<string> updateIds, Action<string> log, CancellationToken ct = default) =>
        RunSta(() =>
        {
            using var com = new ComScope();
            dynamic session = com.Own(CreateCom("Microsoft.Update.Session"));
            session.ClientApplicationID = "C.A.R.S. Control";
            dynamic searcher = com.Own(session.CreateUpdateSearcher());
            dynamic result = com.Own(searcher.Search(Criteria));
            dynamic updates = com.Own(result.Updates);
            dynamic coll = com.Own(CreateCom("Microsoft.Update.UpdateColl"));
            int count = updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic u = com.Own(updates.Item(i));
                dynamic identity = com.Own(u.Identity);
                if (!updateIds.Contains((string)identity.UpdateID, StringComparer.OrdinalIgnoreCase)) continue;
                if (!(bool)u.EulaAccepted) u.AcceptEula();
                coll.Add(u);
                log($"queued: {(string)u.Title}");
            }
            // Nothing left to install means it got there another way (WSL updating itself on 2026-10-12
            // was reported as "Failed"): that's up to date, not a failure.
            var alreadyDone = updateIds.Count - (int)coll.Count;
            if ((int)coll.Count == 0) return new InstallOutcome(true, 2, false, "Already installed — nothing left to do.");

            log("downloading…");
            dynamic downloader = com.Own(session.CreateUpdateDownloader());
            downloader.Updates = coll;
            com.Own(downloader.Download());

            log("installing…");
            dynamic installer = com.Own(session.CreateUpdateInstaller());
            installer.Updates = coll;
            dynamic r = com.Own(installer.Install());
            int code = r.ResultCode;
            bool reboot = r.RebootRequired;
            var ok = code is 2 or 3;
            return new InstallOutcome(ok, code, reboot, code switch
            {
                2 => reboot ? "Installed — restart needed to finish." : "Installed.",
                3 => "Installed with some errors.",
                5 => "Aborted.",
                _ => "Windows Update reported a failure.",
            } + (ok && alreadyDone > 0 ? $" ({alreadyDone} were already installed.)" : ""));
        }, ct);

    public static bool IsValidUpdateId(string id) => GuidPattern().IsMatch(id);

    private static object CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    /// <summary>WUA is happiest on an STA thread.</summary>
    private static Task<T> RunSta<T>(Func<T> work, CancellationToken ct)
        => StaWorker.Run(work, ct);

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();
}
