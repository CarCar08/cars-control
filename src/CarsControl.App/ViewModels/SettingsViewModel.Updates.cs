using System.Runtime.InteropServices;
using System.Text;
using CarsControl.App.Services;
using CarsControl.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

/// <summary>Settings › About &amp; updates.</summary>
public sealed partial class SettingsViewModel
{
    private SelfUpdateService _selfUpdate = null!;

    [ObservableProperty] private bool _updatesAuto;
    [ObservableProperty] private bool _updatesPre;
    [ObservableProperty] private string _updatesRepo = "";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string? _updateNotes;
    [ObservableProperty] private string? _updateTitle;
    [ObservableProperty] private bool _updateReady;
    [ObservableProperty] private bool _updateBusy;
    [ObservableProperty] private bool _updatesAvailableHere;

    public string AppVersionText => $"C.A.R.S. Control {SelfUpdateService.Current}";

    public const string ProjectUrl = BugReport.ProjectUrl;

    private void InitUpdates(SelfUpdateService service)
    {
        _selfUpdate = service;
        _selfUpdate.Changed += RefreshUpdateState;
    }

    private void ReloadUpdates()
    {
        var u = _settings.Store.Current.Updates;
        UpdatesAuto = u.AutoCheck;
        UpdatesPre = u.IncludePreReleases;
        UpdatesRepo = u.Repository;
        UpdatesAvailableHere = SelfUpdateService.IsInstalledCopy;
        RefreshUpdateState();
    }

    private void RefreshUpdateState()
    {
        UpdateBusy = _selfUpdate.IsBusy;
        UpdateReady = _selfUpdate.Ready;
        UpdateStatus = !SelfUpdateService.IsInstalledCopy
            ? "This copy isn't the installed one (it runs from a development folder), so it doesn't update itself."
            : _selfUpdate.Status;
        UpdateTitle = _selfUpdate.Available is { } r ? $"What's new in {r.Version}" : null;
        UpdateNotes = _selfUpdate.Available?.Notes is { Length: > 0 } notes && CarsControl.Setup.ReleaseNotesText.Plain(notes) is { Length: > 0 } plain ? plain : null;
    }

    partial void OnUpdatesAutoChanged(bool value) => SaveUpdates(u => u.AutoCheck = value);
    partial void OnUpdatesPreChanged(bool value) => SaveUpdates(u => u.IncludePreReleases = value);

    partial void OnUpdatesRepoChanged(string value)
    {
        // owner/name only: it ends up in a web address and a command line.
        if (System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^[A-Za-z0-9][A-Za-z0-9\-]{0,38}/[A-Za-z0-9._\-]{1,100}$"))
            SaveUpdates(u => u.Repository = value.Trim());
    }

    private void SaveUpdates(Action<UpdateSettings> change)
    {
        if (_loading) return;
        _settings.Store.Update(s => change(s.Updates));
    }

    [RelayCommand]
    private Task CheckUpdatesNowAsync() => _selfUpdate.CheckAsync(manual: true);

    [RelayCommand]
    private Task InstallUpdateAsync() => _selfUpdate.ApplyAsync();

    [RelayCommand]
    private void SkipUpdate() => _selfUpdate.Skip();

    [RelayCommand]
    private void OpenReleasePage() => Platform.OpenUri(_selfUpdate.Available?.PageUrl is { Length: > 0 } url ? url : $"https://github.com/{_settings.Store.Current.Updates.Repository}/releases");

    [RelayCommand]
    private static void RunFirstRun() => Views.FirstRunWindow.Open();

    [RelayCommand]
    private static void OpenProject() => Platform.OpenUri(ProjectUrl);

    /// <summary>The licences of everything C.A.R.S. ships with: the installed copy has the file; a development build opens the repository's.</summary>
    [RelayCommand]
    private static void OpenLicenses()
    {
        var local = System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        // Notepad, not the .md association, which may be missing or an editor.
        if (System.IO.File.Exists(local)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = true })?.Dispose();
        else Platform.OpenUri(ProjectUrl + "/blob/main/THIRD-PARTY-NOTICES.md");
    }

    [RelayCommand]
    private void OpenIssues() => BugReport.Open(_helper.IsConnected);

    /// <summary>What a bug report needs, with nothing personal in it (no user name, no paths outside the app).</summary>
    [RelayCommand]
    private void CopyDiagnostics()
    {
        Platform.CopyText(BugReport.Clean(BugReport.Diagnostics(_helper.IsConnected)));
        Message = "Copied. Paste it into the bug report.";
    }
}
