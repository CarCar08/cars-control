namespace CarsControl.Core.Settings;

/// <summary>The current <see cref="ProfileSettings"/>, set by the app at startup and whenever settings
/// change, so Core decisions (default ticks, warnings) follow your settings instead of being hard-coded.</summary>
public static class ActiveProfile
{
    public static ProfileSettings Current { get; set; } = new();
}
