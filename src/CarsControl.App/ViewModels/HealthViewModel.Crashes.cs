using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Health;
using CarsControl.Core.Util;
using CarsControl.Core.Winget;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class HealthViewModel
{
    public ObservableCollection<BlueScreenRow> BlueScreens { get; } = [];
    public ObservableCollection<string> ShutdownTimes { get; } = [];
    public ObservableCollection<AppCrashRow> AppCrashList { get; } = [];

    [ObservableProperty] private string _crashSummary = "";
    [ObservableProperty] private string _dumpSummary = "";
    [ObservableProperty] private bool _hasDebugger;
    [ObservableProperty] private string _debuggerStatus = "";
    [ObservableProperty] private bool _debuggerBusy;

    private static string DumpCacheFolder => Path.Combine(Core.Util.UserData.Root, "dumps");
    private static string SymbolCacheFolder => Path.Combine(DumpCacheFolder, "symbols");

    private void LoadAnalyses()
    {
        try
        {
            if (!Directory.Exists(DumpCacheFolder)) return;
            foreach (var file in Directory.EnumerateFiles(DumpCacheFolder, "*.json"))
                if (JsonSerializer.Deserialize<DumpAnalysis>(File.ReadAllText(file)) is { } a) _analyses[a.Dump] = a;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged cache is just re-analysed.
        }
    }

    private void SaveAnalysis(DumpAnalysis a)
    {
        _analyses[a.Dump] = a;
        try
        {
            Directory.CreateDirectory(DumpCacheFolder);
            File.WriteAllText(Path.Combine(DumpCacheFolder, Path.GetFileNameWithoutExtension(a.Dump) + ".json"), JsonSerializer.Serialize(a));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorReporter.Log("dump cache", ex);
        }
    }

    private static string Describe(DumpAnalysis a)
    {
        var blamed = a.Blamed is { } b ? $"Windows' debugger blames {b}" : "The debugger didn't name a cause";
        if (a.BlamesWindowsItself) blamed += " (Windows itself: usually a symptom of a driver or hardware problem, not the cause)";
        var text = blamed + (a.FailureBucket is { } fb ? $".\nFailure group: {fb}" : ".");
        return CrashModules.Explain(a.Blamed, a.FailureBucket) is { } meaning ? text + "\nWhat it means: " + meaning : text;
    }

    private async Task LoadCrashesAsync()
    {
        HasDebugger = DumpAnalyzer.FindCdb() is not null;
        try
        {
            var read = await CrashLog.ReadAsync(TimeSpan.FromDays(120));
            var apps = await AppCrashes.ReadAsync(TimeSpan.FromDays(45));
            var dumps = DumpFiles.List();
            _timeline = read with { BlueScreens = CrashLog.AttachMinidumps(read.BlueScreens, dumps) };

            BlueScreens.Clear();
            foreach (var b in _timeline.BlueScreens)
            {
                var cached = b.DumpPath is { } p && _analyses.TryGetValue(p, out var a) ? a : null;
                BlueScreens.Add(new BlueScreenRow(b, cached is null ? null : Describe(cached), cached?.Blamed));
            }
            ShutdownTimes.Clear();
            foreach (var s in _timeline.Shutdowns.Take(20)) ShutdownTimes.Add($"{s.At:ddd d MMM, h:mm tt}  ({Converters.Format.Ago(s.At)})");
            AppCrashList.Clear();
            foreach (var g in apps.Take(15))
                AppCrashList.Add(new AppCrashRow(g.App, string.Join(", ", new[] { g.Crashes > 0 ? $"{g.Crashes} crash{(g.Crashes == 1 ? "" : "es")}" : null, g.Hangs > 0 ? $"{g.Hangs} hang{(g.Hangs == 1 ? "" : "s")}" : null }.Where(x => x is not null)),
                    Converters.Format.Ago(g.Last), g.Module, g.Reason));

            DumpSummary = dumps.Count == 0 ? "No crash dumps are saved on this PC."
                : $"{dumps.Count} crash dump{(dumps.Count == 1 ? "" : "s")} saved in {DumpFiles.MinidumpFolder} ({Converters.Format.Bytes(dumps.Sum(d => d.Bytes))}).";
            CrashSummary = _timeline.BlueScreens.Count == 0
                ? "No blue screens in the last four months."
                : $"{_timeline.BlueScreens.Count} blue screen{(_timeline.BlueScreens.Count == 1 ? "" : "s")} in the last four months, the latest {Converters.Format.Ago(_timeline.BlueScreens[0].At)}.";
            if (_timeline.Shutdowns.Count > 0) CrashSummary += $" The PC also stopped without shutting down {_timeline.Shutdowns.Count} time{(_timeline.Shutdowns.Count == 1 ? "" : "s")}.";
            BuildOverview();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("crashes", ex);
            SetBanner("Couldn't read the crash history: " + ex.Message, NoticeKind.Warning);
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync(BlueScreenRow? row)
    {
        if (row?.Screen.DumpPath is not { } dump) return;
        if (DumpAnalyzer.FindCdb() is not { } cdb)
        {
            HasDebugger = false;
            SetBanner("The Windows debugger isn't installed yet. Press “Install WinDbg” first.", NoticeKind.Info);
            return;
        }
        if (!await Dialogs.ConfirmAsync($"Let WinDbg read {Path.GetFileName(dump)} to find out what crashed?\n\nIt downloads Windows' debug symbols from Microsoft's symbol server the first time (about 100 MB, kept in {SymbolCacheFolder}). The server learns the names and version numbers of the Windows files in the dump, not your files or documents.", "Analyze")) return;

        row.IsBusy = true;
        try
        {
            // Windows keeps minidumps where only administrators can read them: the helper copies this one for the debugger.
            var readable = dump;
            if (!DumpFiles.CanRead(dump))
            {
                row.Analysis = "Windows keeps crash dumps where only administrators can read them, so the admin helper copies this one first…";
                var copy = await AdminAsync(new CopyCrashDumpOp(Path.GetFileName(dump)));
                if (copy is null) { row.Analysis = "The dump wasn't copied (admin approval was cancelled), so it couldn't be analyzed."; return; }
                if (!copy.Ok || copy.Data is not { Length: > 0 } copied) { row.Analysis = "The dump couldn't be copied: " + copy.Message; return; }
                readable = copied;
            }

            row.Analysis = "Reading the dump… the first one takes a few minutes while symbols download.";
            Directory.CreateDirectory(SymbolCacheFolder);
            var symbols = $"srv*{SymbolCacheFolder}*https://msdl.microsoft.com/download/symbols";
            var r = await ProcessRunner.RunAsync(cdb, ["-z", readable, "-y", symbols, "-c", DumpAnalyzer.AnalyzeCommand], null, TimeSpan.FromMinutes(12));
            if (r.TimedOut) { row.Analysis = "The debugger took too long. Try again (symbols already downloaded are kept)."; return; }
            var analysis = DumpAnalyzer.Parse(dump, r.Lines);
            if (analysis.Code is null && analysis.Blamed is null)
            {
                row.Analysis = "The debugger couldn't read this dump. " + (DumpAnalyzer.ProblemIn(r.Lines) ?? $"(cdb exit {r.ExitCode}: {r.Lines.LastOrDefault()})");
                ErrorReporter.Log("crash analysis", new InvalidOperationException(string.Join(Environment.NewLine, r.Lines.TakeLast(15))));
                return;
            }
            SaveAnalysis(analysis);
            row.Analysis = Describe(analysis);
            row.Blamed = analysis.Blamed;
            BuildOverview();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            row.Analysis = "Couldn't run the debugger: " + ex.Message;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallDebuggerAsync()
    {
        if (!await Dialogs.ConfirmAsync("Install WinDbg (Microsoft's debugger, from winget) for your account? It's about 200 MB and is only used to read crash dumps when you ask.", "Install WinDbg")) return;
        DebuggerBusy = true;
        DebuggerStatus = "Installing WinDbg…";
        try
        {
            var winget = WingetCli.Locate();
            if (winget is null) { DebuggerStatus = "winget wasn't found."; return; }
            var r = await ProcessRunner.RunAsync(winget, ["install", "--id", "Microsoft.WinDbg", "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                line => Platform.OnUiThread(() => DebuggerStatus = line.Length > 90 ? line[..90] + "…" : line), TimeSpan.FromMinutes(20));
            HasDebugger = DumpAnalyzer.FindCdb() is not null;
            DebuggerStatus = r.ExitCode == 0 && HasDebugger ? "WinDbg is installed." : $"The install didn't finish (exit {r.ExitCode}). {r.Lines.LastOrDefault()}";
            SetBanner(HasDebugger ? "WinDbg is installed. Press Analyze on a blue screen." : DebuggerStatus, HasDebugger ? NoticeKind.Success : NoticeKind.Warning);
        }
        finally
        {
            DebuggerBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDumpFolder() => Platform.ShowInFileManager(DumpFiles.MinidumpFolder);
}
