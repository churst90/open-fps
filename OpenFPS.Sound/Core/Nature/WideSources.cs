using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// How one world hears its extended sources (<see cref="ExtendedSources"/>): whether it spreads them at all, by
/// which rule its places share a source's power, and the lab's two levers, with the layouts and line lengths it
/// has worked out under them. Every reader of that world asks this one (AudioWorldOptions.WideSources), so a
/// lever set for one world never reaches another (a test beside a session, a lab comparing two settings).
///
/// A new one is the shipped hearing; <see cref="FromEnvironment"/> is that, unless the run's environment says
/// otherwise. Changing a lever forgets what was worked out under the old one, so the next layout asked for is
/// made under the new; a voice already made keeps the places it was made with.
/// </summary>
public sealed class WideSources
{
    /// <summary>Spread at all: off plays every one from one point, as before 2026-10-06 (OPENFPS_WIDE_SOURCES=0).</summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Forget(); }
    }
    private bool _enabled = true;

    /// <summary>How the places share a source's power: each as loud as what it holds (the shipped rule, 2026-10-16),
    /// or held to the middle as before (OPENFPS_EXTENT_RULE=middle, for an A/B).</summary>
    public ExtendedSources.PlaceRule Rule
    {
        get => _rule;
        set { _rule = value; Forget(); }
    }
    private ExtendedSources.PlaceRule _rule = ExtendedSources.PlaceRule.Pieces;

    /// <summary>For the lab: how far out the places are, as a share of where they belong. Zero puts every
    /// place on the middle, which leaves the streams independent and changes only where they are heard
    /// from (AudioLab --wide-sources collapse=1).</summary>
    public float LayoutScale
    {
        get => _scale;
        set { _scale = value; Forget(); }
    }
    private float _scale = 1f;

    /// <summary>For the lab: a spread to use at any distance instead of the one the angle asks for; NaN for none.</summary>
    public float ForceSpread { get; set; } = float.NaN;

    /// <summary>The shipped hearing, unless OPENFPS_WIDE_SOURCES=0 or OPENFPS_EXTENT_RULE=middle says otherwise.</summary>
    public static WideSources FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// <summary>The same, from <paramref name="variable"/> instead of the process's environment.</summary>
    public static WideSources From(Func<string, string?> variable) => new()
    {
        Enabled = variable("OPENFPS_WIDE_SOURCES") != "0",
        Rule = variable("OPENFPS_EXTENT_RULE") == "middle" ? ExtendedSources.PlaceRule.Middle : ExtendedSources.PlaceRule.Pieces,
    };

    private readonly ConcurrentDictionary<string, Vector3[]?> _layouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, float> _lines = new(StringComparer.OrdinalIgnoreCase);

    private void Forget()
    {
        _layouts.Clear();
        _lines.Clear();
        _widths.Clear();
    }

    /// <summary>
    /// The places of a source, by its physical key, as offsets from its middle in the source's own frame
    /// (x east, y up, z north on an unrotated entity): place 0 is the middle itself. Null for anything
    /// that is not an extended source of this kind (a machine, a whole fountain at one point, the rain), for a
    /// line this world cuts into pieces instead (<see cref="LineLength"/>), and for every source when it does not
    /// spread them. Which kind it is and its model are <paramref name="presets"/>' (the catalogue's).
    /// </summary>
    public Vector3[]? Layout(string? physicalKey, IPresets presets)
    {
        if (!_enabled || string.IsNullOrEmpty(physicalKey)) return null;
        var layout = _layouts.GetOrAdd(physicalKey, static (key, args) =>
        {
            var (self, presets) = args;
            if (self.LineLength(key, presets) > 0f) return null;
            var places = presets.Places(key);
            if (places != null && self._scale != 1f)
                for (int i = 0; i < places.Length; i++) places[i] *= self._scale;
            return places;
        }, (this, presets));
        return layout != null && layout.Length <= ExtendedSources.MaxPlaces ? layout : null;
    }

    /// <summary>
    /// The length of a source heard as a line cut into pieces, m: running water laid along its own x (a creek,
    /// a ditch, a rivulet, a gutter), under <see cref="ExtendedSources.PlaceRule.Pieces"/>. Zero for anything
    /// else, and for everything when this world does not spread its sources.
    /// </summary>
    public float LineLength(string? physicalKey, IPresets presets)
    {
        if (!_enabled || _rule != ExtendedSources.PlaceRule.Pieces || string.IsNullOrEmpty(physicalKey)
            || !physicalKey.StartsWith("flow:", StringComparison.OrdinalIgnoreCase)) return 0f;
        return _lines.GetOrAdd(physicalKey, static (key, presets) => ExtendedSources.LineLengthOf(key, presets), presets);
    }

    /// <summary>
    /// How wide a line's bed is, m: a stream's, which a wide one is heard across (LinePath.WideBedMetres). Zero for a
    /// gutter (its water hugs the kerb) and for anything that is not a line this world cuts.
    /// </summary>
    public float LineWidth(string? physicalKey, IPresets presets)
    {
        if (LineLength(physicalKey, presets) <= 0f) return 0f;
        return _widths.GetOrAdd(physicalKey!, static (key, presets) =>
        {
            try
            {
                var spec = presets.Flow(key[5..]);
                return spec.Channel == FlowChannel.Stream ? spec.WidthMetres : 0f;
            }
            catch (Exception) { return 0f; }
        }, presets);
    }
    private readonly ConcurrentDictionary<string, float> _widths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The path a line source is heard along: the line its emitter carries (SoundEmitterComponent.Line, in its own
    /// frame), or a straight line of <paramref name="length"/> along its own x through <paramref name="position"/>.
    /// </summary>
    public static LinePath PathOf(Vector3 position, Quaternion rotation, Vector3[]? line, float length, float width)
        => line is { Length: >= 2 }
            ? LinePath.Of(position, rotation, line, width)
            : LinePath.Straight(position, Vector3.Transform(Vector3.UnitX, rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : rotation), length, width);

    /// <summary>A whole shore's outline heard as one source (ShoreSpec.OutlineKey), cut along itself at the ear under
    /// <see cref="ExtendedSources.PlaceRule.Pieces"/>. False for everything when this world does not spread its sources.</summary>
    public bool IsOutline(string? physicalKey)
        => _enabled && _rule == ExtendedSources.PlaceRule.Pieces && ShoreSpec.TryOutline(physicalKey, out _, out _);

    /// <summary>How many streams a running-water synth renders: one for each piece a line can be cut into, or
    /// its own places.</summary>
    public int FlowPlaces(string? physicalKey, RunningWaterSpec spec, IPresets presets)
        => LineLength(physicalKey, presets) > 0f ? ExtendedSources.MaxPlaces : Math.Max(1, spec.Places);

    /// <summary>The spread a source of this reach wants at this distance from its middle, 0 to 1
    /// (<see cref="ExtendedSources.SpreadFor"/>), or the lab's forced one.</summary>
    public float SpreadFor(float reachMetres, float distanceMetres)
        => float.IsNaN(ForceSpread) ? ExtendedSources.SpreadFor(reachMetres, distanceMetres) : Math.Clamp(ForceSpread, 0f, 1f);
}
