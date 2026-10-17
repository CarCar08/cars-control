using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Startup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class StartupRowViewModel(StartupItem item, StartupViewModel owner) : ObservableObject
{
    public StartupItem Item { get; private set; } = item;
    public string Name => Item.Name;
    public string Detail => string.Join(" · ", new[] { Item.Publisher, Item.Where }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string? Command => Item.Command;
    public string? Note => Item.Note ?? StartupNotes.For(Item.Name);
    public bool NeedsAdmin => Item.NeedsAdmin;
    public bool IsLocked => StartupWriter.IsPermanentlyProtected(Item) || Item.Note?.StartsWith("C.A.R.S. won't change", StringComparison.Ordinal) == true;
    public bool CanOpenFolder => Item.Exe is not null && System.IO.File.Exists(Item.Exe);

    public override string ToString() => $"{Name}, {(IsOn ? "on" : "off")}";

    [ObservableProperty] private bool _isOn = item.Enabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;

    internal bool Syncing { get; set; }

    internal void Set(StartupItem fresh)
    {
        Item = fresh;
        Syncing = true;
        IsOn = fresh.Enabled;
        Syncing = false;
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(IsLocked));
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!Syncing) _ = owner.ChangeAsync(this, value);
    }

    [RelayCommand]
    private void OpenFolder() => Platform.ShowInFileManager(Item.Exe);

    [RelayCommand]
    private Task DisableCompletelyAsync() => owner.DisableServiceAsync(this);
}

/// <summary>
/// Apps › Startup: everything that starts by itself, in one list, with a switch each. Run entries and Startup-folder
/// shortcuts are switched the way Task Manager does it, so both always agree; services go between automatic and on
/// demand; scheduled tasks on and off. Your own things change as you, the rest through the admin helper.
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    private readonly HelperClient _helper;
    private readonly CarsDb _db;

    public ObservableCollection<StartupRowViewModel> Apps { get; } = [];
    public ObservableCollection<StartupRowViewModel> Services { get; } = [];
    public ObservableCollection<StartupRowViewModel> Tasks { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private string _filter = "";

    private IReadOnlyList<StartupItem> _all = [];

    public StartupViewModel(HelperClient helper, CarsDb db)
    {
        _helper = helper;
        _db = db;
    }

    partial void OnFilterChanged(string value) => Populate();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var all = await Task.Run(() => StartupReader.ReadRunAndFolders().Concat(StartupReader.ReadServices()).Concat(StartupReader.ReadTasks()).ToList());
            _all = all;
            Populate();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("startup", ex);
            Notice("Couldn't read everything that starts with Windows: " + ex.Message, NoticeKind.Warning);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Populate()
    {
        var f = Filter.Trim();
        bool Match(StartupItem i) => f.Length == 0 || i.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || (i.Publisher?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
        Fill(Apps, _all.Where(i => i.Kind is StartupKind.Run or StartupKind.Folder && Match(i)));
        Fill(Services, _all.Where(i => i.Kind == StartupKind.Service && Match(i)));
        Fill(Tasks, _all.Where(i => i.Kind == StartupKind.Task && Match(i)));
        var on = _all.Count(i => i.Enabled);
        Summary = $"{on} of {_all.Count} items start by themselves.";
    }

    private void Fill(ObservableCollection<StartupRowViewModel> target, IEnumerable<StartupItem> items)
    {
        // Rows keep their identity (and a switch you're about to flip) across refreshes.
        var wanted = items.ToList();
        for (var i = target.Count - 1; i >= 0; i--)
            if (wanted.All(w => w.Id != target[i].Item.Id)) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            var at = target.ToList().FindIndex(r => r.Item.Id == w.Id);
            if (at < 0)
            {
                target.Insert(Math.Min(i, target.Count), new StartupRowViewModel(w, this));
            }
            else
            {
                target[at].Set(w);
                if (at != i) target.Move(at, i);
            }
        }
    }

    private void Notice(string text, NoticeKind kind)
    {
        Banner = text;
        BannerKind = kind;
    }

    // ---------- changing one ----------

    internal async Task ChangeAsync(StartupRowViewModel row, bool on)
    {
        var item = row.Item;
        if (row.IsLocked)
        {
            Revert(row, !on);
            Notice($"{item.Name}: {item.Note}", NoticeKind.Info);
            return;
        }
        if (!on && row.Note is { } note && !await Dialogs.ConfirmAsync($"{item.Name}\n\n{note}\n\nSwitch it off anyway?", "Switch off"))
        {
            Revert(row, true);
            return;
        }

        row.IsBusy = true;
        try
        {
            var (ok, message) = await ApplyAsync(item, on);
            if (!ok) Revert(row, !on);
            row.Status = ok ? null : message;
            Notice(ok ? message : $"{item.Name}: {message}", ok ? NoticeKind.Success : NoticeKind.Error);
            if (ok) Record(item, on ? "Started with Windows again" : "Stopped starting with Windows", message);
        }
        finally
        {
            row.IsBusy = false;
        }
        await VerifyAsync(row);
    }

    internal async Task DisableServiceAsync(StartupRowViewModel row)
    {
        var item = row.Item;
        if (row.IsLocked || item.Kind != StartupKind.Service) return;
        if (!await Dialogs.ConfirmAsync($"Disable the “{item.Name}” service completely?\n\nNothing will be able to start it, and apps that need it may stop working. “Switch off” (on demand) is the gentler choice.", "Disable", danger: true)) return;
        row.IsBusy = true;
        try
        {
            var r = await AdminAsync(new ServiceStartTypeOp(ServiceName(item), "disabled"));
            if (r is null) return;
            Notice(r.Ok ? $"{item.Name} is disabled." : r.Message ?? "Windows refused.", r.Ok ? NoticeKind.Success : NoticeKind.Error);
            if (r.Ok) Record(item, "Disabled a service", r.Message);
        }
        finally
        {
            row.IsBusy = false;
        }
        await RefreshAsync();
    }

    private async Task<(bool Ok, string Message)> ApplyAsync(StartupItem item, bool on)
    {
        switch (item.Kind)
        {
            case StartupKind.Run or StartupKind.Folder when item.Scope == StartupScope.You:
                try
                {
                    return StartupWriter.TrySetYours(item, on) ? (true, on ? $"{item.Name} will start with Windows." : $"{item.Name} won't start with Windows.") : (false, "That isn't one of your own entries.");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    return (false, "Windows refused the change: " + ex.Message);
                }

            case StartupKind.Run or StartupKind.Folder:
            {
                var id = StartupId.Parse(item.Id)!;
                var where = id.Kind == "folder" ? "folder" : id.Where == "HKLM32" ? "run32" : "run";
                var r = await AdminAsync(new StartupEntryOp(where, id.Name, on));
                return r is null ? (false, "Admin approval was cancelled, so nothing changed.") : (r.Ok, r.Message ?? (r.Ok ? "Done." : "Windows refused."));
            }

            case StartupKind.Service:
            {
                var r = await AdminAsync(new ServiceStartTypeOp(ServiceName(item), on ? "auto" : "manual"));
                return r is null ? (false, "Admin approval was cancelled, so nothing changed.") : (r.Ok, r.Message ?? (r.Ok ? "Done." : "Windows refused."));
            }

            default: // scheduled task
            {
                var path = item.Id["task:".Length..];
                if (item.Scope == StartupScope.You && StartupWriter.TrySetTask(path, on)) return (true, on ? $"The “{item.Name}” task is on." : $"The “{item.Name}” task is off.");
                var r = await AdminAsync(new ScheduledTaskToggleOp(path, on));
                return r is null ? (false, "Admin approval was cancelled, so nothing changed.") : (r.Ok, r.Message ?? (r.Ok ? "Done." : "Windows refused."));
            }
        }
    }

    private static string ServiceName(StartupItem item) => item.Id["svc:".Length..];

    private async Task<OpResult?> AdminAsync(HelperOp op)
    {
        try
        {
            return await _helper.SendAsync(op, dryRun: HelperClient.DevUnelevated);
        }
        catch (HelperCancelledException)
        {
            return null;
        }
        catch (HelperUnavailableException ex)
        {
            Notice(ex.Message, NoticeKind.Error);
            return null;
        }
    }

    /// <summary>Reads the item back, so the switch shows what Windows now says (a refused change flips back).</summary>
    private async Task VerifyAsync(StartupRowViewModel row)
    {
        var id = row.Item.Id;
        var fresh = await Task.Run(() => (id.StartsWith("svc:") ? StartupReader.ReadServices() : id.StartsWith("task:") ? StartupReader.ReadTasks() : StartupReader.ReadRunAndFolders()).FirstOrDefault(i => i.Id == id));
        if (fresh is not null && !HelperClient.DevUnelevated) row.Set(fresh);
    }

    private static void Revert(StartupRowViewModel row, bool to)
    {
        row.Syncing = true;
        row.IsOn = to;
        row.Syncing = false;
    }

    private void Record(StartupItem item, string title, string? message) =>
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "startup", item.Id, item.Name, "Startup", null, null, item.NeedsAdmin, "Succeeded", 0, $"{title}: {item.Name}", message));
}
