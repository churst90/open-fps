using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

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

    /// <summary>The shipped world, unless OPENFPS_WIDE_SOURCES, OPENFPS_EXTENT_RULE or OPENFPS_CABIN_PATHS says
    /// otherwise.</summary>
    public static AudioWorldOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// <summary>The same, from <paramref name="variable"/> instead of the process's environment.</summary>
    public static AudioWorldOptions From(Func<string, string?> variable) => new()
    {
        WideSources = WideSources.From(variable),
        CabinPathsOn = variable("OPENFPS_CABIN_PATHS") != "0",
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
