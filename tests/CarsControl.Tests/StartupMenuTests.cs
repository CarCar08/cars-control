using CarsControl.Contracts;
using CarsControl.Core.ContextMenu;
using CarsControl.Core.Elevation;
using CarsControl.Core.Startup;

namespace CarsControl.Tests;

public class StartupApprovalTests
{
    [Fact]
    public void Task_managers_flag_bytes_mean_what_it_says()
    {
        Assert.True(StartupApproval.IsEnabled(null));
        Assert.True(StartupApproval.IsEnabled([]));
        Assert.True(StartupApproval.IsEnabled([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(StartupApproval.IsEnabled([0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(StartupApproval.IsEnabled([0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(StartupApproval.IsEnabled([0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void Written_values_are_twelve_bytes_and_round_trip()
    {
        var on = StartupApproval.Enabled();
        var off = StartupApproval.Disabled(new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, on.Length);
        Assert.Equal(12, off.Length);
        Assert.True(StartupApproval.IsEnabled(on));
        Assert.False(StartupApproval.IsEnabled(off));
        Assert.Equal(new DateTime(2026, 10, 14, 3, 0, 0, DateTimeKind.Utc), DateTime.FromFileTimeUtc(BitConverter.ToInt64(off, 4)));
    }
}

public class CommandLinesTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Proton\\ProtonVPN.exe\" --tray", "C:\\Program Files\\Proton\\ProtonVPN.exe")]
    [InlineData("C:\\Tools\\thing.exe /s /q", "C:\\Tools\\thing.exe")]
    [InlineData("C:\\Program Files\\App\\app.exe", "C:\\Program Files\\App\\app.exe")]
    [InlineData("\"C:\\a b\\x.exe\"", "C:\\a b\\x.exe")]
    [InlineData("notepad", "notepad")]
    [InlineData("cmd /c start x", "cmd")]
    public void The_program_is_found_in_a_start_up_command(string command, string expected) => Assert.Equal(expected, CommandLines.Executable(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    public void Nothing_usable_is_null(string? command) => Assert.Null(CommandLines.Executable(command));

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var windows = Environment.GetEnvironmentVariable("SystemRoot")!;
        Assert.Equal(windows + "\\notepad.exe", CommandLines.Executable("%SystemRoot%\\notepad.exe /p"));
    }
}

public class StartupIdTests
{
    [Theory]
    [InlineData("run:HKCU:OneDrive", "run", "HKCU", "OneDrive")]
    [InlineData("run:HKLM32:Discord", "run", "HKLM32", "Discord")]
    [InlineData("run:HKLM:Name: with colon", "run", "HKLM", "Name: with colon")]
    [InlineData("folder:user:ShareX.lnk", "folder", "user", "ShareX.lnk")]
    [InlineData("folder:common:Tailscale.lnk", "folder", "common", "Tailscale.lnk")]
    [InlineData("svc:Everything", "svc", "", "Everything")]
    [InlineData("task:\\ASUS\\Thing", "task", "", "\\ASUS\\Thing")]
    public void Ids_parse(string id, string kind, string where, string name)
    {
        var parsed = StartupId.Parse(id);
        Assert.Equal(new StartupId(kind, where, name), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("run:HKXX:x")]
    [InlineData("folder:other:x")]
    [InlineData("run:HKCU:")]
    [InlineData("task:NoLeadingSlash")]
    [InlineData("random")]
    public void Odd_ids_do_not(string id) => Assert.Null(StartupId.Parse(id));
}

public class StartupRulesTests
{
    [Theory]
    [InlineData("Everything", true)]
    [InlineData("Google.Updater_157.0", true)]
    [InlineData("x; calc", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void Service_names_are_checked(string name, bool valid) => Assert.Equal(valid, ServiceRules.IsValidName(name));

    [Fact]
    public void Microsofts_and_windows_own_services_are_not_listed()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(ServiceRules.IsListable("BITS", windows + "\\System32\\svchost.exe", "Microsoft Corporation"));
        Assert.False(ServiceRules.IsListable("Foo", windows + "\\System32\\foo.exe", "Someone Else"));
        Assert.False(ServiceRules.IsListable("Bar", "C:\\Program Files\\Bar\\bar.exe", "Microsoft Corporation"));
        Assert.True(ServiceRules.IsListable("Everything", "C:\\Program Files\\Everything\\Everything.exe", "voidtools"));
        Assert.False(ServiceRules.IsListable("bad name", "C:\\x.exe", "x"));
    }

    [Theory]
    [InlineData("SaladBowl", null)]
    [InlineData("Tailscale", null)]
    [InlineData("ProtonVPN Service", null)]
    [InlineData("NvContainerLocalSystem", "NVIDIA Corporation")]
    [InlineData("CarsControlHelper", null)]
    [InlineData("AnyDesk", "AnyDesk Software GmbH")]
    [InlineData("Parsec", null)]
    public void Services_this_pc_relies_on_are_protected(string name, string? publisher) => Assert.True(ServiceRules.IsProtected(name, publisher));

    [Theory]
    [InlineData("Everything", "voidtools")]
    [InlineData("LGHUBUpdaterService", "Logitech, Inc.")]
    [InlineData("Windhawk", "Ramen Software")]
    public void Other_services_can_be_changed(string name, string publisher) => Assert.False(ServiceRules.IsProtected(name, publisher));

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("manual", "demand")]
    [InlineData("disabled", "disabled")]
    [InlineData("boot", null)]
    [InlineData("system", null)]
    [InlineData("", null)]
    public void Only_three_start_types_map_to_sc(string start, string? sc) => Assert.Equal(sc, ServiceRules.ScStartType(start));

    [Theory]
    [InlineData("\\ASUS\\Thing", true)]
    [InlineData("\\Desktop Commander Remote", true)]
    [InlineData("\\Microsoft\\Windows\\Defrag\\ScheduledDefrag", false)]
    [InlineData("\\microsoft\\x", false)]
    [InlineData("\\CarsControl-Maintenance", false)]
    [InlineData("Microsoft\\x", false)]
    [InlineData("\\a\" /RU SYSTEM", false)]
    [InlineData("\\a&b", false)]
    [InlineData("", false)]
    public void Only_plain_non_microsoft_task_paths_are_listable(string path, bool ok) => Assert.Equal(ok, TaskRules.IsListable(path));

    [Theory]
    [InlineData("\\ExitNodeGuard", true)]
    [InlineData("\\Salad Updater", true)]
    [InlineData("\\ASUS\\Thing", false)]
    public void Tasks_other_tools_depend_on_are_protected(string path, bool prot) => Assert.Equal(prot, TaskRules.IsProtected(path));

    [Fact]
    public void Known_items_carry_a_warning_and_the_match_is_at_the_start_of_the_name()
    {
        Assert.Contains("exit-node guard", StartupNotes.For("Proton VPN"));
        Assert.Contains("Defender keeps protecting", StartupNotes.For("SecurityHealth"));
        Assert.Null(StartupNotes.For("Sam's Steam Toolkit"));
        Assert.NotNull(StartupNotes.For("Steam"));
        Assert.Null(StartupNotes.For("Something unknown"));
    }

    [Fact]
    public void Item_flags_follow_kind_and_scope()
    {
        var svc = new StartupItem("svc:X", StartupKind.Service, StartupScope.Everyone, "X", "x", null, null, true, "w");
        var mine = new StartupItem("run:HKCU:Y", StartupKind.Run, StartupScope.You, "Y", "y", null, null, true, "w");
        var all = new StartupItem("run:HKLM:Z", StartupKind.Run, StartupScope.Everyone, "Z", "z", null, null, true, "w");
        Assert.True(svc.NeedsAdmin);
        Assert.False(mine.NeedsAdmin);
        Assert.True(all.NeedsAdmin);
    }
}

public class StartupHelperOpTests
{
    private static HelperOperations Ops(bool dev = false) => new(new HelperLog(), parentProfile: null, devUnelevated: dev);

    private static Task<OpResult> Run(HelperOp op, bool dryRun = true, bool dev = false) => Ops(dev).ExecuteAsync(op, dryRun, _ => { }, CancellationToken.None);

    [Theory]
    [InlineData("registry", "x")]
    [InlineData("run", "")]
    [InlineData("run", "..\\x")]
    [InlineData("run", "a/b")]
    [InlineData("folder", "c:evil")]
    public async Task Startup_entries_need_a_known_place_and_a_plain_name(string where, string name)
    {
        Assert.Equal(OpStatus.Rejected, (await Run(new StartupEntryOp(where, name, false))).Status);
    }

    [Fact]
    public async Task An_entry_that_isnt_there_is_not_found()
    {
        Assert.Equal(OpStatus.NotFound, (await Run(new StartupEntryOp("run", "No such entry " + Guid.NewGuid(), false))).Status);
    }

    [Theory]
    [InlineData("bad name; calc", "auto")]
    [InlineData("Everything", "boot")]
    [InlineData("Everything", "")]
    public async Task Service_changes_need_a_valid_name_and_a_start_type_from_the_short_list(string service, string type)
    {
        Assert.Equal(OpStatus.Rejected, (await Run(new ServiceStartTypeOp(service, type))).Status);
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("Spooler")]
    [InlineData("SaladBowl")]
    [InlineData("Tailscale")]
    public async Task Windows_and_protected_services_are_refused_or_absent(string service)
    {
        var r = await Run(new ServiceStartTypeOp(service, "disabled"));
        Assert.True(r.Status is OpStatus.Rejected or OpStatus.NotFound, r.Status + " " + r.Message);
    }

    [Theory]
    [InlineData("\\Microsoft\\Windows\\Defrag\\ScheduledDefrag")]
    [InlineData("\\CarsControl-Maintenance")]
    [InlineData("\\ExitNodeGuard")]
    [InlineData("\\x\" /RU SYSTEM")]
    public async Task Task_changes_refuse_microsofts_ours_and_protected_tasks(string path)
    {
        Assert.Equal(OpStatus.Rejected, (await Run(new ScheduledTaskToggleOp(path, false))).Status);
    }

    [Fact]
    public async Task A_plain_task_dry_runs_to_the_exact_schtasks_command()
    {
        var r = await Run(new ScheduledTaskToggleOp("\\ASUS\\Thing", false));
        Assert.Equal(OpStatus.DryRun, r.Status);
        Assert.Contains("schtasks.exe", r.Message);
        Assert.Contains("/Change /TN \\ASUS\\Thing /DISABLE", r.Message);
    }

    [Fact]
    public async Task Startup_ops_in_dev_mode_are_dry_runs_only()
    {
        foreach (HelperOp op in new HelperOp[] { new StartupEntryOp("run", "x", true), new ServiceStartTypeOp("Everything", "manual"), new ScheduledTaskToggleOp("\\ASUS\\Thing", true) })
            Assert.Equal(OpStatus.Rejected, (await Run(op, dryRun: false, dev: true)).Status);
    }

    [Fact]
    public void Startup_ops_round_trip_through_the_pipe_format()
    {
        HelperOp[] ops = [new StartupEntryOp("run32", "Discord", false), new ServiceStartTypeOp("Everything", "auto"), new ScheduledTaskToggleOp("\\ASUS\\Thing", true)];
        foreach (var op in ops)
            Assert.Equal(op, HelperJson.Deserialize<HelperRequest>(HelperJson.Serialize(new HelperRequest(1, op)))!.Op);
    }
}

public class ContextMenuTests
{
    [Theory]
    [InlineData("{474C98EE-CF3D-41f5-80E3-4AAB0AB04301}", true)]
    [InlineData("{474C98EE-CF3D-41F5-80E3-4AAB0AB04301}", true)]
    [InlineData("474C98EE-CF3D-41f5-80E3-4AAB0AB04301", false)]
    [InlineData("{not a guid}", false)]
    [InlineData("Nilesoft.Shell", false)]
    [InlineData("", false)]
    public void Clsids_are_recognised(string text, bool valid) => Assert.Equal(valid, ContextMenuEntries.IsClsid(text));

    [Theory]
    [InlineData("cmd", true)]
    [InlineData("Open Git Bash here", true)]
    [InlineData("a\\b", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void Verb_names_cannot_climb_out_of_their_key(string verb, bool ok) => Assert.Equal(ok, ContextMenuEntries.SafeVerb(verb));

    [Fact]
    public void Nilesoft_shell_is_never_switched()
    {
        var warn = ContextMenuEntries.Warn("Nilesoft.Shell ( @nilesoft.shell)", "Nilesoft", null);
        Assert.NotNull(warn);
        var entry = new MenuEntry("handler:Folders:Nilesoft.Shell", MenuPlace.Folders, MenuEntryKind.Handler, "Nilesoft.Shell", null, "Nilesoft", false, "{11111111-2222-3333-4444-555555555555}", false, warn);
        Assert.True(entry.Locked);
        Assert.False(ContextMenuEntries.SetHidden(entry, true));
    }

    [Fact]
    public void An_entry_with_no_clsid_or_a_bad_id_cannot_be_changed()
    {
        var noClsid = new MenuEntry("handler:Files:x", MenuPlace.Files, MenuEntryKind.Handler, "x", null, null, false, null, false, null);
        Assert.False(ContextMenuEntries.SetHidden(noClsid, true));
        var badVerb = new MenuEntry("verb:Files:..\\evil", MenuPlace.Files, MenuEntryKind.Verb, "x", null, null, false, null, false, null);
        Assert.False(ContextMenuEntries.SetHidden(badVerb, true));
        var badPlace = new MenuEntry("verb:Nowhere:cmd", MenuPlace.Files, MenuEntryKind.Verb, "x", null, null, false, null, false, null);
        Assert.False(ContextMenuEntries.SetHidden(badPlace, true));
    }

    [Fact]
    public void The_cars_entries_run_the_app_with_a_quoted_folder_argument()
    {
        Assert.Contains(CarsEntries.All, e => e.Root == "Directory" && e.Spec.Arguments == "--explore \"%1\"");
        Assert.Contains(CarsEntries.All, e => e.Root == @"Directory\Background" && e.Spec.Arguments == "--explore \"%V\"");
        Assert.Contains(CarsEntries.All, e => e.Root == "Drive");
        Assert.All(CarsEntries.All, e => Assert.StartsWith(CarsEntries.Prefix, e.Spec.KeySuffix));
    }

    [Fact]
    public void The_explore_argument_accepts_real_folders_and_drives_only()
    {
        var temp = Path.GetTempPath().TrimEnd('\\');
        Assert.Equal(Path.GetFullPath(temp), ExploreArgument.Validate(temp));
        Assert.Equal(Path.GetFullPath(temp), ExploreArgument.Validate("\"" + temp + "\""));
        Assert.NotNull(ExploreArgument.Validate(@"C:\"));
        Assert.Null(ExploreArgument.Validate(null));
        Assert.Null(ExploreArgument.Validate(""));
        Assert.Null(ExploreArgument.Validate(@"relative\path"));
        Assert.Null(ExploreArgument.Validate(temp + @"\..\Windows"));
        Assert.Null(ExploreArgument.Validate(@"\\server\share"));
        Assert.Null(ExploreArgument.Validate(@"\\?\C:\Windows"));
        Assert.Null(ExploreArgument.Validate(@"C:\definitely\not\here\" + Guid.NewGuid()));
        Assert.Null(ExploreArgument.Validate(Environment.ProcessPath)); // a file, not a folder
    }

    [Fact]
    public void Activation_text_round_trips_and_ignores_anything_else()
    {
        var temp = Path.GetFullPath(Path.GetTempPath().TrimEnd('\\'));
        Assert.Equal(temp, ExploreArgument.FromActivation(ExploreArgument.ToActivation(temp)));
        Assert.Null(ExploreArgument.FromActivation("settings"));
        Assert.Null(ExploreArgument.FromActivation(null));
        Assert.Null(ExploreArgument.FromActivation("explore:relative"));
    }
}
