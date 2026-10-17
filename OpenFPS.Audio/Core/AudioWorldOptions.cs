using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Engine;
using OpenFPS.Sound.Core.Nature;

namespace OpenFPS.Audio.Core;

/// <summary>
/// The options of one audio world where hosts may differ (docs/SOUND_LIBRARY_BOUNDARY.md, 7.5): how it hears
/// extended sources (<see cref="WideSources"/>) and whether a vehicle's own sound comes into its cabin by its
/// paths (<see cref="CabinPathsOn"/>). Every reader of that world asks this one, the mixer's voices as it makes
/// them and the host's placing through AudioEngineFacade.Options, so an option set for one world never reaches
/// another (a test beside a session, a lab comparing two settings).
///
/// Who holds it: the mixer (FmodAudioProvider.Options), and the host reaches the same one through
/// AudioEngineFacade.Options. A new one is the shipped world; <see cref="FromEnvironment"/> is that, unless the
/// run's environment says otherwise. Set before the voices it shapes are made: a voice keeps what it was made with.
/// </summary>
public sealed class AudioWorldOptions
{
    /// <summary>How this world hears a tree's crown, a fire's bed, running water and a shore across their extent
    /// (OPENFPS_WIDE_SOURCES, OPENFPS_EXTENT_RULE).</summary>
    public WideSources WideSources { get; init; } = new();

    /// <summary>A vehicle's own sound comes into its cabin by its paths, each from where it comes in (CabinPaths);
    /// off plays the interior from one point (OPENFPS_CABIN_PATHS=0).</summary>
    public bool CabinPathsOn { get; set; } = true;

    /// <summary>For the lab: every cabin path from where the one interior voice was (still separate streams), to
    /// tell what the directions do from what the split does.</summary>
    public bool CabinOnePlace { get; set; }

    /// <summary>How many voices of each kind the world may have at most (docs/SOUND_LIBRARY_BOUNDARY.md 7.5): what
    /// the mixer's load then gives up and takes back within.</summary>
    public VoiceBudgets Budgets { get; init; } = new();

    /// <summary>Trace lines (OPENFPS_AUDIO_DEBUG=1): the listener's own feet, the paths other people's steps start
    /// on, and every one-off sound received and played.</summary>
    public bool Trace { get; init; }

    /// <summary>The shipped world, unless OPENFPS_WIDE_SOURCES, OPENFPS_EXTENT_RULE or OPENFPS_CABIN_PATHS says
    /// otherwise.</summary>
    public static AudioWorldOptions FromEnvironment() => From(System.Environment.GetEnvironmentVariable);

    /// <summary>The same, from <paramref name="variable"/> instead of the process's environment.</summary>
    public static AudioWorldOptions From(Func<string, string?> variable) => new()
    {
        WideSources = WideSources.From(variable),
        CabinPathsOn = variable("OPENFPS_CABIN_PATHS") != "0",
        Budgets = VoiceBudgets.From(variable),
        Trace = variable("OPENFPS_AUDIO_DEBUG") == "1",
    };

    /// <summary>The paths into this vehicle's cabin in this world, or null if it has none or this world plays the
    /// interior from one point.</summary>
    public CabinPaths.Layout? CabinLayout(VehicleProfile v, IPresets presets)
        => CabinPathsOn ? CabinPaths.For(v, presets) : null;

    /// <summary>Where a cabin path is played from, relative to the head in the vehicle's frame
    /// (<see cref="CabinPaths.Offset"/>), or where the one voice was under the lab's <see cref="CabinOnePlace"/>.</summary>
    public Vector3 CabinOffset(CabinPaths.Layout layout, int path, Vector3 ear)
        => CabinOnePlace ? CabinPaths.OnePlace : CabinPaths.Offset(layout, path, ear);
}

/// <summary>The most voices of each kind an audio world plays; the mixer's load steers it below them.</summary>
public sealed record VoiceBudgets
{
    /// <summary>
    /// How many vehicles may run a live engine at once (OPENFPS_ENGINE_VOICES overrides). Each is a whole
    /// engine integrated sample by sample on the render pool; a V8 renders about nine seconds of audio per
    /// core-second in a release build (`--engine-cost`). The rest borrow a near car's ring (distant voices).
    /// </summary>
    public int Engines { get; init; } = 32;

    /// <summary>
    /// Reflections per engine (OPENFPS_ENGINE_ECHOES: 0 none, 1 one; a diagnostic lever). Echoes churn
    /// more than anything else in the audio system (about 256 created and released in 98 s on the city)
    /// and are the only voices that read another voice's buffers, so taking them out is the quickest way
    /// to clear or implicate them in a mixer-thread fault.
    /// </summary>
    /// <remarks>
    /// An unsmeared echo is a coherent copy of the engine: against the direct sound a comb that sweeps as
    /// either moves (a car heard inside out), alone a point beamed from a wall. So each echo is smeared by
    /// the roughness of its wall (EngineEchoState.Scattering) and swells and fades over a few hundred
    /// milliseconds instead of switching.
    /// </remarks>
    public int Echoes { get; init; } = OpenFPS.Audio.Acoustics.EngineReflections.MaxEchoesPerEngine;

    /// <summary>
    /// How many machines may have their front outlet voiced separately (OPENFPS_FRONT_VOICES=0 keeps
    /// each on one voice, for an A/B). A car's ends are told apart only inside about 20 m, so few are
    /// needed except on a start grid. The first thing given up when the mixer runs short, before
    /// reflections: the machine stays fully audible without it.
    /// </summary>
    public int Front { get; init; } = 6;

    /// <summary>
    /// How many outer places of trees, fires and fountain taps may have voices at once (ExtendedSources),
    /// nearest source first; OPENFPS_PLACE_VOICES=0 keeps each on its middle. A place given up costs only
    /// geometry (the source merges to its middle at the same level), so places are the first thing the
    /// mixer gives up and the last it takes back, a source's worth at a time.
    /// </summary>
    public int Places { get; init; } = 36;

    /// <summary>
    /// How many voices the standing machines (air conditioners, mowers, plant), aircraft, fountains, bells
    /// and trains may have at once. Its own budget: a map carries tens of vehicles but hundreds of small
    /// machines (the city has 114 window units). Ranked not by distance but by how loud each would be here,
    /// at the ear (Loudness.RenderedGain, then Loudness.HeardGain): a rooftop condenser at 65 dB 80 m away
    /// beats a 59 dB window unit behind a hedge. Counted in voices, not things: a fountain is five and a
    /// train up to six (TrainVoicing). OPENFPS_MACHINE_VOICES overrides, and 0 is a valid answer: every
    /// machine and aircraft voice off.
    /// </summary>
    public int Machines { get; init; } = 24;

    /// <summary>The shipped budgets, unless <paramref name="variable"/> says otherwise.</summary>
    public static VoiceBudgets From(Func<string, string?> variable)
    {
        var b = new VoiceBudgets();
        return b with
        {
            Engines = int.TryParse(variable("OPENFPS_ENGINE_VOICES"), out int e) && e > 0 ? e : b.Engines,
            Echoes = int.TryParse(variable("OPENFPS_ENGINE_ECHOES"), out int ec) && ec >= 0 ? Math.Min(ec, b.Echoes) : b.Echoes,
            Front = int.TryParse(variable("OPENFPS_FRONT_VOICES"), out int fv) && fv >= 0 ? fv : b.Front,
            Places = int.TryParse(variable("OPENFPS_PLACE_VOICES"), out int pv) && pv >= 0 ? pv : b.Places,
            Machines = int.TryParse(variable("OPENFPS_MACHINE_VOICES"), out int m) && m >= 0 ? m : b.Machines,
        };
    }
}
