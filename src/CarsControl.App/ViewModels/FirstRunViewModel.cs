using CarsControl.App.Services;
using CarsControl.Core.Settings;
using CarsControl.Core.Help;
using CarsControl.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

/// <summary>
/// Stages this PC's profile and settings, explains optional features, then previews changes before saving.
/// Nothing needs admin rights
/// (the privacy switches that do are offered on the Privacy page afterwards), and every choice lives in
/// Settings, so skipping loses nothing.
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly PrivacyViewModel _privacy;

    public const int LastStep = 6;

    public event Action? CloseRequested;
    public event Action<string>? OpenSettingsSection;
    public event Action<string, string?>? OpenPage;

    public IReadOnlyList<PcTypeOption> PcTypeOptions { get; } =
    [new(PcType.Auto, "Detect desktop or laptop"), new(PcType.Desktop, "Desktop PC"), new(PcType.Laptop, "Laptop"), new(PcType.GamingFirst, "Gaming PC")];

    public FirstRunViewModel(AppSettings settings, PrivacyViewModel privacy)
    {
        _settings = settings;
        _privacy = privacy;
        var s = settings.Store.Current;
        _holdDrivers = s.Profile.HoldDriverUpdates;
        _keepCrash = s.Profile.KeepCrashEvidence;
        _startWithWindows = settings.StartWithWindows;
        _maintenanceOn = s.Maintenance.Enabled;
        _gamingAuto = s.Gaming.Automatic;
        _gamingApprove = s.Gaming.ApproveAtSignIn;
        _restartAway = s.Restart.Enabled;
        _boardSensors = s.Health.ReadBoardSensors;
        _pcTypeChoice = PcTypeOptions.FirstOrDefault(o => o.Value == s.Profile.PcType) ?? PcTypeOptions[0];
        _privacy.Refresh();
        PrivacyCount = _privacy.UserSwitchesOff;
        _privacyApply = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirst), nameof(IsLast), nameof(StepText), nameof(Title), nameof(Intro), nameof(Step0), nameof(Step1), nameof(Step2), nameof(Step3), nameof(Step4), nameof(Step5), nameof(Step6), nameof(NextText), nameof(ReviewText))]
    private int _step;

    [ObservableProperty] private bool _holdDrivers;
    [ObservableProperty] private bool _keepCrash;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _maintenanceOn;
    [ObservableProperty] private bool _gamingAuto;
    [ObservableProperty] private bool _gamingApprove;
    [ObservableProperty] private bool _privacyApply;
    [ObservableProperty] private int _privacyCount;
    [ObservableProperty] private PcTypeOption? _pcTypeChoice;
    [ObservableProperty] private bool _restartAway;
    [ObservableProperty] private bool _boardSensors;
    [ObservableProperty] private string _followUpNote = "Optional setup can also be opened later from the app's pages.";
    private string? _followUpTarget;
    private bool _finishing;

    private SetupChoices Choices => new(PcTypeChoice?.Value ?? PcType.Auto, HoldDrivers, KeepCrash, StartWithWindows, MaintenanceOn, GamingAuto, GamingApprove, RestartAway, BoardSensors);
    public string ProfileAdvice => $"Recommendations are for {Recommendations.Describe(Recommendations.Resolve(PcTypeChoice?.Value ?? PcType.Auto, RecommendationContext.HasBattery))}. Choosing a profile only changes the advice; review all changes before Finish.";
    public string SensorStatus => PawnIo.IsInstalled() ? "PawnIO is already installed. Reading CPU and board sensors uses an admin session you approve." : "PawnIO is optional and is not installed. CPU and board readings need its signed driver; GPU readings work without it. Install it explicitly from Health > Sensors.";
    public string ReviewText
    {
        get
        {
            var changes = FirstRunSetup.Preview(FirstRunSetup.Read(_settings.Store.Current, _settings.StartWithWindows), Choices).ToList();
            if (PrivacyApply && PrivacyCount > 0) changes.Add($"Apply {PrivacyCount} suggested privacy switches for your account; each has its own Undo on Privacy.");
            return (changes.Count == 0 ? "Your existing settings will be kept." : string.Join("\n", changes.Select(c => "• " + c)))
                + "\n\nUndo setup settings is available in Settings > This PC. Optional drivers, backup destinations, sync folders and phone subscriptions are configured separately; Finish does not install or send anything.\n\n" + FollowUpNote;
        }
    }

    partial void OnPcTypeChoiceChanged(PcTypeOption? value) => OnPropertyChanged(nameof(ProfileAdvice));

    [RelayCommand]
    private void UseRecommended()
    {
        var choices = FirstRunSetup.Recommend(Choices, RecommendationContext.HasBattery);
        StartWithWindows = choices.StartWithWindows;
        MaintenanceOn = choices.MaintenanceOn;
        GamingAuto = choices.GamingAuto;
        GamingApprove = choices.GamingApprove;
        RestartAway = choices.RestartAway;
        BoardSensors = choices.BoardSensors;
        OnPropertyChanged(nameof(ReviewText));
    }

    [RelayCommand]
    private void ChooseFollowUp(string target)
    {
        if (target is not ("backup" or "sync" or "sensors" or "alerts")) return;
        _followUpTarget = target;
        FollowUpNote = $"After Finish, open {target switch { "backup" => "file backups", "sync" => "setup sync", "sensors" => "Health > Sensors", _ => "Phone alerts" }}. Other optional features stay available from their pages.";
        OnPropertyChanged(nameof(ReviewText));
    }

    public bool IsFirst => Step == 0;
    public bool IsLast => Step == LastStep;
    public bool Step0 => Step == 0;
    public bool Step1 => Step == 1;
    public bool Step2 => Step == 2;
    public bool Step3 => Step == 3;
    public bool Step4 => Step == 4;
    public bool Step5 => Step == 5;
    public bool Step6 => Step == 6;
    public string StepText => $"Step {Step + 1} of {LastStep + 1}";
    public string NextText => IsLast ? "Finish" : "Next";
    public string PrivacyText => PrivacyCount == 0
        ? "Every privacy switch that doesn't need admin is already on."
        : $"Turn on the {PrivacyCount} privacy switches that only change your own account (no admin needed): ads and tips, tracking IDs, tailored experiences…";

    public string Title => Step switch
    {
        0 => "Welcome to C.A.R.S. Control",
        1 => "What should run by itself?",
        2 => "Privacy",
        3 => "Back up files and sync your setup",
        4 => "CPU and motherboard sensors",
        5 => "Phone alerts",
        _ => "Review your choices",
    };

    public string Intro => Step switch
    {
        0 => "Choose this PC's profile and review suggestions. Nothing changes until Finish; Skip all keeps your current settings.",
        1 => "These work in the background and never show an admin prompt while you're away.",
        2 => "Windows sends a lot to Microsoft by default. You choose what stops.",
        3 => "File backups protect your documents. Setup sync shares preferences through a folder you choose. They are separate and both optional.",
        4 => "Choose whether C.A.R.S. should read CPU and board sensors when the optional driver and an approved admin session are available.",
        5 => "Get a short note on your phone when something needs you: a failed update, a nearly full drive, a blue screen.",
        _ => "Finish saves only the choices listed below. Skip all keeps the existing settings and marks setup as seen.",
    };

    public bool HasPrivacySwitches => PrivacyCount > 0;

    partial void OnPrivacyCountChanged(int value)
    {
        OnPropertyChanged(nameof(PrivacyText));
        OnPropertyChanged(nameof(HasPrivacySwitches));
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0) Step--;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!IsLast)
        {
            Step++;
            return;
        }
        await FinishAsync(applyChoices: true);
    }

    [RelayCommand]
    private Task SkipAsync() => FinishAsync(applyChoices: false);

    [RelayCommand]
    private void SetUpAlerts()
    {
        ChooseFollowUp("alerts");
    }

    private async Task FinishAsync(bool applyChoices)
    {
        if (_finishing) return;
        _finishing = true;
        try
        {
            if (applyChoices)
            {
                var choices = Choices;
                var undo = FirstRunSetup.Capture(_settings.Store.Current, _settings.StartWithWindows, _settings.StartupCommand);
                _settings.Store.Update(s =>
                {
                    s.FirstRunUndo = undo;
                    FirstRunSetup.Apply(s, choices);
                });
                if (_settings.StartWithWindows != StartWithWindows) _settings.StartWithWindows = StartWithWindows;
                RecommendationContext.Set(choices.PcType);
                if (PrivacyApply) await _privacy.ApplyUserSwitchesAsync();
            }
            _settings.Store.Update(s => s.FirstRunCompleted = SelfUpdateService.Current.ToString());
            CloseRequested?.Invoke();
            if (applyChoices && _followUpTarget is { } target)
            {
                if (target == "alerts") OpenSettingsSection?.Invoke("Alerts");
                else OpenPage?.Invoke(target == "sensors" ? "health" : "backup", target switch { "sensors" => "sensors", "sync" => "setup", _ => null });
            }
        }
        finally { _finishing = false; }
    }
}
