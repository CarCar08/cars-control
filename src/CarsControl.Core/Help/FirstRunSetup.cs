using CarsControl.Core.Settings;

namespace CarsControl.Core.Help;

/// <summary>Only the settings offered by the wizard; paths, credentials and update sources cannot enter through this model.</summary>
public sealed record SetupChoices(PcType PcType, bool HoldDrivers, bool KeepCrash, bool StartWithWindows,
    bool MaintenanceOn, bool GamingAuto, bool GamingApprove, bool RestartAway, bool BoardSensors);

public sealed record SetupUndo(SetupChoices Choices, string? HoldReason, string? CrashReason, string? StartupCommand = null);

public static class FirstRunSetup
{
    public static SetupChoices Read(CarsSettings s, bool startsWithWindows) => new(s.Profile.PcType,
        s.Profile.HoldDriverUpdates, s.Profile.KeepCrashEvidence, startsWithWindows, s.Maintenance.Enabled,
        s.Gaming.Automatic, s.Gaming.ApproveAtSignIn, s.Restart.Enabled, s.Health.ReadBoardSensors);

    public static SetupUndo Capture(CarsSettings s, bool startsWithWindows, string? startupCommand = null) => new(Read(s, startsWithWindows), s.Profile.HoldDriverUpdatesReason, s.Profile.KeepCrashEvidenceReason, startupCommand);

    /// <summary>Recommendations only stage choices; choosing a PC profile never changes the PC by itself.</summary>
    public static SetupChoices Recommend(SetupChoices choices, bool hasBattery)
    {
        var type = Recommendations.Resolve(choices.PcType, hasBattery);
        bool Value(string id, bool current) => Recommendations.For(id, type)?.On ?? current;
        return choices with
        {
            StartWithWindows = Value("StartWithWindows", choices.StartWithWindows),
            MaintenanceOn = Value("MaintenanceEnabled", choices.MaintenanceOn),
            GamingAuto = Value("GamingAutomatic", choices.GamingAuto),
            GamingApprove = Value("GamingApproveAtSignIn", choices.GamingApprove),
            RestartAway = Value("RestartAway", choices.RestartAway),
            BoardSensors = Value("HealthBoardSensors", choices.BoardSensors)
        };
    }

    public static IReadOnlyList<string> Preview(SetupChoices before, SetupChoices after)
    {
        var changes = new List<string>();
        if (before.PcType != after.PcType) changes.Add($"PC profile: {before.PcType} → {after.PcType}");
        void Add(string title, bool oldValue, bool newValue) { if (oldValue != newValue) changes.Add($"{title}: {(oldValue ? "on" : "off")} → {(newValue ? "on" : "off")}"); }
        Add("Hold driver updates", before.HoldDrivers, after.HoldDrivers);
        Add("Keep crash evidence", before.KeepCrash, after.KeepCrash);
        Add("Start at sign-in", before.StartWithWindows, after.StartWithWindows);
        Add("Scheduled maintenance", before.MaintenanceOn, after.MaintenanceOn);
        Add("Automatic Gaming mode", before.GamingAuto, after.GamingAuto);
        Add("Ask for Gaming mode admin access at sign-in", before.GamingApprove, after.GamingApprove);
        Add("Restart when away", before.RestartAway, after.RestartAway);
        Add("Read CPU and board sensors when available", before.BoardSensors, after.BoardSensors);
        return changes;
    }

    public static void Apply(CarsSettings s, SetupChoices choices)
    {
        if (!Enum.IsDefined(choices.PcType)) throw new ArgumentException("Unknown PC profile.", nameof(choices));
        s.Profile.PcType = choices.PcType;
        s.Profile.HoldDriverUpdates = choices.HoldDrivers;
        s.Profile.KeepCrashEvidence = choices.KeepCrash;
        if (choices.HoldDrivers && string.IsNullOrWhiteSpace(s.Profile.HoldDriverUpdatesReason)) s.Profile.HoldDriverUpdatesReason = "Chosen during first-time setup";
        if (choices.KeepCrash && string.IsNullOrWhiteSpace(s.Profile.KeepCrashEvidenceReason)) s.Profile.KeepCrashEvidenceReason = "Chosen during first-time setup";
        s.Maintenance.Enabled = choices.MaintenanceOn;
        s.Gaming.Automatic = choices.GamingAuto;
        s.Gaming.ApproveAtSignIn = choices.GamingApprove;
        s.Restart.Enabled = choices.RestartAway;
        s.Health.ReadBoardSensors = choices.BoardSensors;
    }

    public static void Restore(CarsSettings s, SetupUndo undo)
    {
        Apply(s, undo.Choices);
        s.Profile.HoldDriverUpdatesReason = undo.HoldReason;
        s.Profile.KeepCrashEvidenceReason = undo.CrashReason;
    }
}
