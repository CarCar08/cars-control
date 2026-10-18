using Microsoft.Win32;

namespace CarsControl.Core.Tweaks;

/// <summary>Where a tweak's values live, which decides who may write them.</summary>
public enum TweakScope
{
    /// <summary>HKCU: written by the app, as you.</summary>
    User,

    /// <summary>HKCU\Software\Policies: your own hive, but only admins may write policies, so the helper
    /// writes it under HKEY_USERS\{your SID} (never the admin account's hive).</summary>
    UserPolicy,

    /// <summary>HKLM: all users, through the helper.</summary>
    Machine,
}

public enum TweakGroup
{
    // Privacy page
    Telemetry, Ads, AI, Search, Edge, Office, Taskbar,

    // Optimization page (1.1)
    Power, Gaming, Visuals, Background, Network,
}

/// <summary>How sure we are an optimization does what it says (the Optimization page shows it on every row).</summary>
public enum Evidence
{
    /// <summary>Microsoft documents the effect.</summary>
    Documented,

    /// <summary>Independent measurements show a consistent effect.</summary>
    Measured,

    /// <summary>Helps on some PCs and hurts on others: measure it on yours (Test it on this PC).</summary>
    TestIt,

    /// <summary>A matter of taste: no speed difference worth measuring.</summary>
    Preference,
}

/// <summary>One registry value a tweak sets. <see cref="Default"/> is what Windows has out of the box
/// (null = the value doesn't exist).</summary>
public sealed record TweakValue(string Key, string Name, object Recommended, object? Default = null)
{
    public RegistryValueKind Kind => Recommended is string ? RegistryValueKind.String : RegistryValueKind.DWord;
}

public sealed class Tweak
{
    public required string Id { get; init; }
    public required TweakGroup Group { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required TweakScope Scope { get; init; }
    public required IReadOnlyList<TweakValue> Values { get; init; }

    /// <summary>Windows build range where the setting exists (Recall: 26100+; Chat: before 22631).</summary>
    public int MinBuild { get; init; }
    public int MaxBuild { get; init; } = int.MaxValue;

    /// <summary>Shown under the switch ("Edge will say it's managed by your organization").</summary>
    public string? Note { get; init; }

    /// <summary>Only listed when this is true (Office tweaks need Office).</summary>
    public Func<bool>? Applies { get; init; }

    /// <summary>Left out of "Apply recommended" (e.g. it would break something on this PC).</summary>
    public Func<string?>? Caution { get; init; }

    /// <summary>Optimization page: how sure we are, in one plain sentence why, and where that comes from.</summary>
    public Evidence Evidence { get; init; } = Evidence.Documented;
    public string? EvidenceNote { get; init; }
    public string? SourceUrl { get; init; }

    /// <summary>When the change takes effect, if not right away ("after a restart", "after signing out").</summary>
    public string? TakesEffect { get; init; }

    public bool NeedsAdmin => Scope != TweakScope.User;

    public bool IsOptimization => Group >= TweakGroup.Power;

    /// <summary>
    /// The switch puts back a Windows default that something (often a "debloat" script) turned off, like Game Mode: its value is
    /// the default, and the row shows On whenever Windows behaves the default way.
    /// </summary>
    public bool RestoresWindowsDefault { get; init; }

    /// <summary>Could plausibly change a game's frame rate, so the Optimization page offers "Test it on this PC" for it.</summary>
    public bool FrameTestable { get; init; }

    public bool IsAvailable(int build) => build >= MinBuild && build < MaxBuild && (Applies?.Invoke() ?? true);
}

/// <summary>
/// Every privacy and debloat switch, as data (like <see cref="Cleanup.CleanupRules"/>): the app and the
/// admin helper use this same list, and the helper accepts only these ids. Deliberately missing: anything
/// that weakens security (Defender, SmartScreen, firewall, UAC, Windows Update) and Windows Error Reporting
/// (crash reports are evidence when diagnosing blue screens).
/// </summary>
public static class TweakCatalog
{
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string DataCollection = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string WindowsAi = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";
    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string OfficePrivacy = @"Software\Policies\Microsoft\Office\16.0\Common\Privacy";

    public static string GroupTitle(TweakGroup g) => g switch
    {
        TweakGroup.Telemetry => "Telemetry & diagnostics",
        TweakGroup.Ads => "Ads, tips & suggestions",
        TweakGroup.AI => "Copilot, Recall & AI extras",
        TweakGroup.Search => "Widgets, news & web search",
        TweakGroup.Edge => "Microsoft Edge",
        TweakGroup.Office => "Microsoft Office",
        TweakGroup.Taskbar => "Taskbar",
        TweakGroup.Power => "Power",
        TweakGroup.Gaming => "Gaming",
        TweakGroup.Visuals => "Visual effects",
        TweakGroup.Background => "Background and start-up",
        _ => "Network",
    };

    private static IReadOnlyList<Tweak>? _all;

    /// <summary>Privacy switches and optimizations (the helper accepts only these ids).</summary>
    public static IReadOnlyList<Tweak> All => _all ??= [.. Privacy, .. Optimizations.All];

    private static IReadOnlyList<Tweak> Privacy { get; } =
    [
        // ---------- Telemetry & diagnostics ----------
        new()
        {
            Id = "diagnostic-data", Group = TweakGroup.Telemetry, Scope = TweakScope.Machine,
            Title = "Send only required diagnostic data",
            Description = "Stops optional diagnostic data (browsing, app use, inking) from going to Microsoft.",
            Values = [new(DataCollection, "AllowTelemetry", 1)],
            Caution = () => WindowsInsider.IsEnrolled() ? "This PC is in the Windows Insider Program, which needs optional diagnostic data: this would stop Insider builds." : null,
        },
        new()
        {
            Id = "tailored-experiences", Group = TweakGroup.Telemetry, Scope = TweakScope.User,
            Title = "No tailored experiences",
            Description = "Microsoft doesn't use your diagnostic data for personalized tips, ads and recommendations.",
            Values = [new(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)],
        },
        new()
        {
            Id = "inking-typing", Group = TweakGroup.Telemetry, Scope = TweakScope.User,
            Title = "No inking & typing personalization",
            Description = "Windows stops building a personal dictionary from what you type and write.",
            Values =
            [
                new(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0),
                new(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0),
                new(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, 1),
                new(@"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, 1),
            ],
        },
        new()
        {
            Id = "feedback-requests", Group = TweakGroup.Telemetry, Scope = TweakScope.User,
            Title = "Never ask for feedback",
            Description = "No \"How likely are you to recommend Windows?\" pop-ups.",
            Values = [new(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)],
        },
        new()
        {
            Id = "activity-history", Group = TweakGroup.Telemetry, Scope = TweakScope.Machine,
            Title = "No activity history",
            Description = "Windows doesn't keep (or upload) a history of the apps, files and sites you used.",
            Values =
            [
                new(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                new(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
            ],
        },
        new()
        {
            Id = "online-speech", Group = TweakGroup.Telemetry, Scope = TweakScope.User,
            Title = "No online speech recognition",
            Description = "Voice typing and dictation don't send your voice to Microsoft's cloud (offline recognition still works).",
            Values = [new(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0)],
        },
        new()
        {
            Id = "track-app-launches", Group = TweakGroup.Telemetry, Scope = TweakScope.User,
            Title = "Don't track app launches",
            Description = "Start and search stop learning from the apps you open (your \"most used\" list goes away).",
            Values = [new(Advanced, "Start_TrackProgs", 0, 1)],
        },

        // ---------- Ads, tips & suggestions ----------
        new()
        {
            Id = "advertising-id", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No advertising ID",
            Description = "Apps can't use an ID that follows you between apps for personalized ads.",
            Values = [new(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)],
        },
        new()
        {
            Id = "language-list", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "Websites can't read your language list",
            Description = "Sites stop getting your Windows languages for local content (a small tracking signal).",
            Values = [new(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1)],
        },
        new()
        {
            Id = "settings-suggestions", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No suggested content in Settings",
            Description = "Settings stops showing promotions and \"recommended\" items.",
            Values =
            [
                new(Cdm, "SubscribedContent-338393Enabled", 0, 1),
                new(Cdm, "SubscribedContent-353694Enabled", 0, 1),
                new(Cdm, "SubscribedContent-353696Enabled", 0, 1),
            ],
        },
        new()
        {
            Id = "start-recommendations", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No tips and promoted apps in Start",
            Description = "Start's Recommended section stops showing tips, shortcuts and new apps.",
            Values = [new(Advanced, "Start_IrisRecommendations", 0, 1)],
        },
        new()
        {
            Id = "tips-notifications", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No tips and suggestions notifications",
            Description = "\"Get tips and suggestions when using Windows\" is turned off.",
            Values =
            [
                new(Cdm, "SubscribedContent-338389Enabled", 0, 1),
                new(Cdm, "SoftLandingEnabled", 0, 1),
            ],
        },
        new()
        {
            Id = "welcome-experience", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No welcome experience after updates",
            Description = "No full-screen \"see what's new\" or \"finish setting up your device\" after updates.",
            Values =
            [
                new(Cdm, "SubscribedContent-310093Enabled", 0, 1),
                new(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1),
            ],
        },
        new()
        {
            Id = "lock-screen-tips", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No fun facts and tips on the lock screen",
            Description = "The lock screen shows just your picture (Spotlight's tips and ads go).",
            Values =
            [
                new(Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
                new(Cdm, "SubscribedContent-338387Enabled", 0, 1),
            ],
        },
        new()
        {
            Id = "silent-app-installs", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No suggested apps installed silently",
            Description = "Windows stops quietly adding promoted apps and games to Start.",
            Values =
            [
                new(Cdm, "SilentInstalledAppsEnabled", 0, 1),
                new(Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                new(Cdm, "PreInstalledAppsEnabled", 0, 1),
                new(Cdm, "OemPreInstalledAppsEnabled", 0, 1),
            ],
        },
        new()
        {
            Id = "explorer-sync-ads", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No OneDrive and Microsoft 365 ads in File Explorer",
            Description = "File Explorer stops showing sync provider notifications.",
            Values = [new(Advanced, "ShowSyncProviderNotifications", 0, 1)],
        },
        new()
        {
            Id = "account-notifications", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No account nags in Start",
            Description = "Start stops showing \"back up your PC\" and Microsoft account notifications.",
            Values = [new(Advanced, "Start_AccountNotifications", 0, 1)],
        },
        new()
        {
            Id = "suggested-notifications", Group = TweakGroup.Ads, Scope = TweakScope.User,
            Title = "No suggested notification settings",
            Description = "Windows stops suggesting that you turn notifications off or on for apps.",
            Values = [new(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.ActionCenter.SmartOptOut", "Enabled", 0)],
        },

        // ---------- Copilot, Recall & AI extras ----------
        new()
        {
            Id = "copilot", Group = TweakGroup.AI, Scope = TweakScope.UserPolicy,
            Title = "Turn off Windows Copilot",
            Description = "The policy that switches off Copilot in Windows for your account (remove the Copilot app below too).",
            Values = [new(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)],
        },
        new()
        {
            Id = "copilot-button", Group = TweakGroup.AI, Scope = TweakScope.User,
            Title = "No Copilot button on the taskbar",
            Description = "Hides the Copilot taskbar button where Windows still shows one.",
            Values = [new(Advanced, "ShowCopilotButton", 0, 1)],
        },
        new()
        {
            Id = "recall", Group = TweakGroup.AI, Scope = TweakScope.Machine, MinBuild = 26100,
            Title = "No Recall snapshots",
            Description = "Recall never saves snapshots of your screen (on Copilot+ PCs; harmless elsewhere).",
            Values = [new(WindowsAi, "DisableAIDataAnalysis", 1)],
        },
        new()
        {
            Id = "click-to-do", Group = TweakGroup.AI, Scope = TweakScope.Machine, MinBuild = 26100,
            Title = "No Click to Do",
            Description = "Turns off the AI actions overlay for what's on screen.",
            Values = [new(WindowsAi, "DisableClickToDo", 1)],
        },
        new()
        {
            Id = "notepad-ai", Group = TweakGroup.AI, Scope = TweakScope.Machine,
            Title = "No AI features in Notepad",
            Description = "Notepad's Copilot rewrite and summarize features are switched off.",
            Values = [new(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1)],
        },
        new()
        {
            Id = "paint-ai", Group = TweakGroup.AI, Scope = TweakScope.Machine,
            Title = "No AI features in Paint",
            Description = "Paint's Cocreator, generative fill and image creator are switched off.",
            Values =
            [
                new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
                new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
                new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1),
            ],
        },

        // ---------- Widgets, news & web search ----------
        new()
        {
            Id = "widgets", Group = TweakGroup.Search, Scope = TweakScope.Machine,
            Title = "No Widgets (news feed)",
            Description = "Removes the Widgets board and its news feed from the taskbar for everyone.",
            Values = [new(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
        },
        new()
        {
            Id = "lock-screen-widgets", Group = TweakGroup.Search, Scope = TweakScope.Machine,
            Title = "No weather and news on the lock screen",
            Values = [new(@"SOFTWARE\Policies\Microsoft\Dsh", "DisableWidgetsOnLockScreen", 1)],
            Description = "The lock screen stops showing the weather-and-more card.",
        },
        new()
        {
            Id = "web-search", Group = TweakGroup.Search, Scope = TweakScope.UserPolicy,
            Title = "No Bing results in Start search",
            Description = "Searching from Start looks at your PC only; nothing you type is sent to Bing.",
            Values = [new(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1)],
        },
        new()
        {
            Id = "search-highlights", Group = TweakGroup.Search, Scope = TweakScope.User,
            Title = "No search highlights",
            Description = "Search stops showing trending topics and daily illustrations.",
            Values = [new(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, 1)],
        },

        // ---------- Edge ----------
        new()
        {
            Id = "edge-shopping", Group = TweakGroup.Edge, Scope = TweakScope.Machine,
            Title = "No shopping assistant and Rewards in Edge",
            Description = "Edge stops price-comparison pop-ups and Microsoft Rewards prompts.",
            Values = [new(EdgePolicy, "EdgeShoppingAssistantEnabled", 0), new(EdgePolicy, "ShowMicrosoftRewards", 0)],
            Note = "Edge shows \"Your browser is managed by your organization\" while any Edge policy is set.",
        },
        new()
        {
            Id = "edge-sidebar", Group = TweakGroup.Edge, Scope = TweakScope.Machine,
            Title = "No Copilot sidebar in Edge",
            Description = "Removes Edge's sidebar (and its Copilot button).",
            Values = [new(EdgePolicy, "HubsSidebarEnabled", 0)],
            Note = "Edge shows \"Your browser is managed by your organization\" while any Edge policy is set.",
        },
        new()
        {
            Id = "edge-recommendations", Group = TweakGroup.Edge, Scope = TweakScope.Machine,
            Title = "No recommendations and personalization in Edge",
            Description = "Edge stops showing feature tips and stops using your browsing to personalize ads and news.",
            Values =
            [
                new(EdgePolicy, "ShowRecommendationsEnabled", 0),
                new(EdgePolicy, "SpotlightExperiencesAndRecommendationsEnabled", 0),
                new(EdgePolicy, "PersonalizationReportingEnabled", 0),
            ],
            Note = "Edge shows \"Your browser is managed by your organization\" while any Edge policy is set.",
        },
        new()
        {
            Id = "edge-diagnostics", Group = TweakGroup.Edge, Scope = TweakScope.Machine,
            Title = "Edge sends no optional diagnostic data",
            Description = "Only the required diagnostic data, like Windows.",
            Values = [new(EdgePolicy, "DiagnosticData", 0)],
            Note = "Edge shows \"Your browser is managed by your organization\" while any Edge policy is set.",
        },

        // ---------- Office ----------
        new()
        {
            Id = "office-connected", Group = TweakGroup.Office, Scope = TweakScope.UserPolicy, Applies = Office.IsInstalled,
            Title = "No optional connected experiences in Office",
            Description = "Office stops online features like 3D maps, LinkedIn and Insights that send content to Microsoft (co-authoring and saving to OneDrive keep working).",
            Values = [new(OfficePrivacy, "ControllerConnectedServicesEnabled", 2)],
        },
        new()
        {
            Id = "office-content-analysis", Group = TweakGroup.Office, Scope = TweakScope.UserPolicy, Applies = Office.IsInstalled,
            Title = "Office doesn't analyze your content",
            Description = "Turns off Designer, Editor's online checks and other features that analyze what you write.",
            Values = [new(OfficePrivacy, "UserContentDisabled", 2)],
        },
        new()
        {
            Id = "office-telemetry", Group = TweakGroup.Office, Scope = TweakScope.UserPolicy, Applies = Office.IsInstalled,
            Title = "Office sends only required diagnostic data",
            Description = "Optional diagnostic data from Word, Excel, Outlook and the rest is turned off.",
            Values = [new(@"Software\Policies\Microsoft\Office\Common\ClientTelemetry", "SendTelemetry", 3)],
        },

        // ---------- Taskbar ----------
        new()
        {
            Id = "task-view-button", Group = TweakGroup.Taskbar, Scope = TweakScope.User,
            Title = "No Task View button",
            Description = "Win+Tab still opens Task View.",
            Values = [new(Advanced, "ShowTaskViewButton", 0, 1)],
        },
        new()
        {
            Id = "chat-button", Group = TweakGroup.Taskbar, Scope = TweakScope.User, MaxBuild = 22631,
            Title = "No Chat (Teams) button",
            Description = "Hides the Chat button older Windows 11 builds put on the taskbar.",
            Values = [new(Advanced, "TaskbarMn", 0, 1)],
        },
        new()
        {
            Id = "search-icon-only", Group = TweakGroup.Taskbar, Scope = TweakScope.User,
            Title = "Search as an icon, not a box",
            Description = "Frees taskbar space; click it or press Win to search.",
            Values = [new(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 1, 2)],
        },
    ];

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public static IReadOnlyList<Tweak> Available(int build) => All.Where(t => t.IsAvailable(build)).ToList();
}

public static class WindowsInsider
{
    /// <summary>Enrolled in the Windows Insider Program (Insider builds require optional diagnostic data).</summary>
    public static bool IsEnrolled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
        return k?.GetValue("EnablePreviewBuilds") is int n && n > 0 || k?.GetValue("BranchName") is string { Length: > 0 };
    }
}

public static class Office
{
    public static bool IsInstalled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun");
        return k is not null;
    }
}
