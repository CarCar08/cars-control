using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Services;
using CarsControl.Core.Backup;
using CarsControl.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class BackupJobRowViewModel(BackupJobSettings job, BackupsViewModel owner) : ObservableObject
{
    public BackupJobSettings Job { get; private set; } = job;
    public string Id => Job.Id;
    public string Name => Job.Name;
    public string What => Job.Sources.Count == 0 ? "No folders chosen yet" : string.Join(", ", Job.Sources.Select(s => Path.GetFileName(s.TrimEnd('\\')) is { Length: > 0 } n ? n : s));
    public string Where => Job.Kind == "usb" ? $"USB drive “{Job.UsbLabel ?? "?"}” ({(BackupService.Destination(Job).Usb is null ? "not plugged in" : "plugged in")})" : (string.IsNullOrWhiteSpace(Job.Folder) ? "No destination yet" : Job.Folder!);
    public string When => Job.Enabled ? $"Every {(Job.IntervalHours % 24 == 0 ? $"{Job.IntervalHours / 24} day{(Job.IntervalHours == 24 ? "" : "s")}" : $"{Job.IntervalHours} hours")} while you're away; older versions are kept {Job.KeepDays} days" : "Switched off";
    public string Last => Job.LastRunUtc is { } t ? $"{Converters.Format.Ago(t.ToLocalTime())}: {Job.LastResult}" : "Never run";
    public string? Warning => BackupService.SameDiskWarning(Job);
    public string Level => Job.LastOk == false ? "Bad" : Warning is not null ? "Watch" : Job.LastOk == true ? "Good" : "Info";
    public override string ToString() => $"{Name}: {Last}";

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _progress;

    internal void Set(BackupJobSettings job)
    {
        Job = job;
        foreach (var n in new[] { nameof(Name), nameof(What), nameof(Where), nameof(When), nameof(Last), nameof(Warning), nameof(Level) }) OnPropertyChanged(n);
    }

    [RelayCommand] private Task RunAsync() => owner.RunAsync(this);
    [RelayCommand] private void Stop() => owner.Stop(this);
    [RelayCommand] private void Edit() => owner.Edit(this);
    [RelayCommand] private Task DeleteAsync() => owner.DeleteAsync(this);
    [RelayCommand] private void OpenDestination() => owner.OpenDestination(this);
}

public sealed record UsbChoice(VolumeInfo Volume)
{
    public string Display => $"{Volume.Display} · {Converters.Format.Bytes(Volume.FreeBytes)} free";
    public override string ToString() => Display;
}

public sealed record SuggestedFolder(string Path, string Label, bool Exists)
{
    public override string ToString() => Label;
}

public sealed partial class SourceRowViewModel(string path, BackupsViewModel owner) : ObservableObject
{
    public string Path { get; } = path;
    public override string ToString() => Path;
    [RelayCommand] private void Remove() => owner.RemoveSource(this);
}

/// <summary>
/// Backup & sync › Backups: the jobs, and an editor for one. Destinations are a folder (put it inside Proton Drive to get
/// an encrypted off-site copy) or a USB drive remembered by its volume ID, which backs up the moment it's plugged in.
/// </summary>
public sealed partial class BackupsViewModel : ObservableObject
{
    private readonly BackupService _service;
    private readonly AppSettings _settings;
    private string? _editingId;

    public ObservableCollection<BackupJobRowViewModel> Jobs { get; } = [];
    public ObservableCollection<SourceRowViewModel> EditSources { get; } = [];
    public ObservableCollection<UsbChoice> UsbDrives { get; } = [];
    public IReadOnlyList<SuggestedFolder> Suggestions { get; }
    public IReadOnlyList<int> KeepChoices { get; } = [7, 14, 30, 60, 90, 180, 365];
    public IReadOnlyList<int> IntervalChoices { get; } = [6, 12, 24, 48, 72, 168];

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "My backup";
    [ObservableProperty] private bool _editToUsb;
    [ObservableProperty] private string? _editFolder;
    [ObservableProperty] private UsbChoice? _editUsb;
    [ObservableProperty] private int _editKeepDays = 30;
    [ObservableProperty] private int _editIntervalHours = 24;
    [ObservableProperty] private string _editExcludes = "";
    [ObservableProperty] private string? _editWarning;
    [ObservableProperty] private string? _editProblem;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private string _protonHint = "";

    public BackupsViewModel(BackupService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        Suggestions = BuildSuggestions();
        _service.Changed += () => Platform.OnUiThread(Refresh);
        _service.Progress += p => Platform.OnUiThread(() =>
        {
            if (Jobs.FirstOrDefault(j => j.Id == p.JobId) is { } row)
            {
                row.IsRunning = p.IsRunning;
                row.Progress = p.Status;
            }
        });
        Refresh();
    }

    private static IReadOnlyList<SuggestedFolder> BuildSuggestions()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var items = new (string Path, string Label)[]
        {
            (docs, "Documents"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures"),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop"),
            (Path.Combine(profile, "Saved Games"), "Saved Games (many games keep saves here)"),
            (Path.Combine(docs, "My Games"), "My Games (more game saves)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos"),
        };
        return items.Where(i => !string.IsNullOrEmpty(i.Path)).DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(i => new SuggestedFolder(i.Path, i.Label, Directory.Exists(i.Path))).Where(i => i.Exists).ToList();
    }

    public void Refresh()
    {
        var jobs = _service.Jobs.ToList();
        for (var i = Jobs.Count - 1; i >= 0; i--)
            if (jobs.All(j => j.Id != Jobs[i].Id)) Jobs.RemoveAt(i);
        foreach (var j in jobs)
        {
            var row = Jobs.FirstOrDefault(r => r.Id == j.Id);
            if (row is null) Jobs.Add(new BackupJobRowViewModel(j, this) { IsRunning = _service.IsRunning(j.Id) });
            else row.Set(j);
        }
        var proton = Core.Storage.CloudSync.Roots().FirstOrDefault(r => r.Provider.Contains("Proton", StringComparison.OrdinalIgnoreCase));
        ProtonHint = proton is null ? "" : $"Proton Drive is at {proton.Path}. A folder inside it is copied (end-to-end encrypted) to Proton's servers.";
    }

    // ---------- editor ----------

    [RelayCommand]
    private void NewJob()
    {
        _editingId = null;
        EditName = Jobs.Count == 0 ? "My files" : $"Backup {Jobs.Count + 1}";
        EditSources.Clear();
        foreach (var s in Suggestions.Where(s => s.Label is "Documents" or "Pictures" or "Desktop" or "Saved Games (many games keep saves here)")) AddSource(s.Path);
        EditToUsb = false;
        EditFolder = Core.Storage.CloudSync.Roots().FirstOrDefault(r => r.Provider.Contains("Proton", StringComparison.OrdinalIgnoreCase)) is { } p ? Path.Combine(p.Path, "C.A.R.S. Backups") : null;
        LoadUsb(null);
        EditKeepDays = 30;
        EditIntervalHours = 24;
        EditExcludes = "*.tmp, Thumbs.db, desktop.ini";
        EditProblem = null;
        IsEditing = true;
        CheckEdit();
    }

    internal void Edit(BackupJobRowViewModel row)
    {
        var j = row.Job;
        _editingId = j.Id;
        EditName = j.Name;
        EditSources.Clear();
        foreach (var s in j.Sources) AddSource(s);
        EditToUsb = j.Kind == "usb";
        EditFolder = j.Folder;
        LoadUsb(j.UsbSerial);
        EditKeepDays = KeepChoices.Contains(j.KeepDays) ? j.KeepDays : 30;
        EditIntervalHours = IntervalChoices.Contains(j.IntervalHours) ? j.IntervalHours : 24;
        EditExcludes = string.Join(", ", j.Excludes);
        EditProblem = null;
        IsEditing = true;
        CheckEdit();
    }

    private void LoadUsb(string? serial)
    {
        UsbDrives.Clear();
        foreach (var v in Volumes.List().Where(v => v.IsUsb)) UsbDrives.Add(new UsbChoice(v));
        EditUsb = UsbDrives.FirstOrDefault(u => u.Volume.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase)) ?? UsbDrives.FirstOrDefault();
    }

    private void AddSource(string path)
    {
        if (EditSources.All(s => !s.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) EditSources.Add(new SourceRowViewModel(path, this));
    }

    internal void RemoveSource(SourceRowViewModel row)
    {
        EditSources.Remove(row);
        CheckEdit();
    }

    [RelayCommand]
    private void AddSuggestion(SuggestedFolder? s)
    {
        if (s is not null) AddSource(s.Path);
        CheckEdit();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (Platform.PickFolder("Choose a folder to back up") is { } p) AddSource(p);
        CheckEdit();
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        if (Platform.PickFolder("Choose where the backup goes", EditFolder) is { } p) EditFolder = p;
        CheckEdit();
    }

    partial void OnEditToUsbChanged(bool value) => CheckEdit();
    partial void OnEditFolderChanged(string? value) => CheckEdit();
    partial void OnEditUsbChanged(UsbChoice? value) => CheckEdit();

    private BackupJobSettings Compose(BackupJobSettings? into = null)
    {
        var j = into ?? new BackupJobSettings();
        j.Name = EditName.Trim().Length == 0 ? "Backup" : EditName.Trim();
        j.Sources = EditSources.Select(s => s.Path).ToList();
        j.Kind = EditToUsb ? "usb" : "folder";
        j.Folder = EditToUsb ? null : EditFolder;
        j.UsbSerial = EditToUsb ? EditUsb?.Volume.Serial : null;
        j.UsbLabel = EditToUsb ? EditUsb?.Volume.Label : null;
        j.KeepDays = EditKeepDays;
        j.IntervalHours = EditIntervalHours;
        j.Excludes = EditExcludes.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return j;
    }

    private void CheckEdit()
    {
        var draft = Compose(new BackupJobSettings { UsbSubfolder = "C.A.R.S. Backup" });
        EditWarning = BackupService.SameDiskWarning(draft);
        var (dest, problem, _) = BackupService.Destination(draft);
        EditProblem = problem ?? (dest is null ? null : BackupEngine.Validate(new BackupOptions(draft.Sources, dest, draft.KeepDays, draft.Excludes)));
    }

    public bool CanSave => EditProblem is null && EditSources.Count > 0 && (EditToUsb ? EditUsb is not null : !string.IsNullOrWhiteSpace(EditFolder));
    partial void OnEditProblemChanged(string? value) => OnPropertyChanged(nameof(CanSave));

    [RelayCommand]
    private void Save()
    {
        CheckEdit();
        if (!CanSave) return;
        _settings.Store.Update(s =>
        {
            var job = _editingId is null ? null : s.Backup.Jobs.FirstOrDefault(j => j.Id == _editingId);
            if (job is null) s.Backup.Jobs.Add(Compose());
            else Compose(job);
        });
        IsEditing = false;
        Refresh();
        Notice(_editingId is null ? "Backup added. It runs while you're away; “Back up now” runs it right away." : "Saved.", NoticeKind.Success);
    }

    [RelayCommand]
    private void Cancel() => IsEditing = false;

    // ---------- running ----------

    internal async Task RunAsync(BackupJobRowViewModel row)
    {
        row.IsRunning = true;
        row.Progress = "Starting…";
        var result = await _service.RunAsync(row.Id, "Run now");
        Refresh();
        if (result is not null) Notice(result.Ok ? $"“{row.Name}”: {result.Summary}" : $"“{row.Name}”: {result.Summary}", result.Ok ? NoticeKind.Success : NoticeKind.Warning);
        else if (row.Job.LastResult is { } last) Notice($"“{row.Name}”: {last}", NoticeKind.Warning);
    }

    internal void Stop(BackupJobRowViewModel row) => _service.Cancel(row.Id);

    internal async Task DeleteAsync(BackupJobRowViewModel row)
    {
        if (!await Dialogs.ConfirmAsync($"Remove the “{row.Name}” backup job?\n\nIt stops running. The copies already made are not deleted.", "Remove", danger: true)) return;
        _settings.Store.Update(s => s.Backup.Jobs.RemoveAll(j => j.Id == row.Id));
        Refresh();
    }

    internal void OpenDestination(BackupJobRowViewModel row)
    {
        var (dest, _, _) = BackupService.Destination(row.Job);
        if (dest is not null) Platform.ShowInFileManager(Directory.Exists(dest) ? dest : Path.GetDirectoryName(dest));
    }

    private void Notice(string text, NoticeKind kind)
    {
        Banner = text;
        BannerKind = kind;
    }
}
