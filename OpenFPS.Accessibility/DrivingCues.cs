namespace OpenFPS.Client.Core;

/// <summary>
/// The driving sounds, each on or off (docs/DRIVING_AIDS.md). <see cref="Enabled"/> is Shift+K: every
/// one of them at once, the spoken road and the car's own sounds aside. Static for the same reason as
/// <see cref="NavigationAids"/>: a head keeps its own copy of <see cref="ClientSettings"/> for the run,
/// and the live value is what it saves, whoever saves.
/// </summary>
public static class DrivingCues
{
    public static bool Enabled { get; set; } = true;
    /// <summary>The guide beep on the line ahead.</summary>
    public static bool Guide { get; set; } = true;
    /// <summary>The centre-line and kerb beeps, and the rumble of a wheel on a line.</summary>
    public static bool LineSensors { get; set; } = true;
    /// <summary>A click every 15 degrees of turn, and the chime when lined up with the road.</summary>
    public static bool TurnClicks { get; set; } = true;
    /// <summary>How hard to brake for what is ahead.</summary>
    public static bool BrakeCue { get; set; } = true;
    /// <summary>The two notes when you go over the speed limit.</summary>
    public static bool SpeedWarning { get; set; } = true;

    /// <summary>The names /drivecues takes, and what each one is.</summary>
    public static readonly (string Name, string What)[] Names =
    {
        ("guide", "the guide beep"), ("lines", "line beeps and rumble"), ("clicks", "turn clicks"),
        ("brake", "the brake cue"), ("speed", "the speed limit warning"),
    };

    public static bool Get(string name) => name switch
    {
        "guide" => Guide, "lines" => LineSensors, "clicks" => TurnClicks, "brake" => BrakeCue, "speed" => SpeedWarning,
        _ => Enabled,
    };

    public static bool Set(string name, bool on)
    {
        switch (name)
        {
            case "guide": Guide = on; return true;
            case "lines": LineSensors = on; return true;
            case "clicks": TurnClicks = on; return true;
            case "brake": BrakeCue = on; return true;
            case "speed": SpeedWarning = on; return true;
            case "all": Enabled = on; return true;
            default: return false;
        }
    }
}
