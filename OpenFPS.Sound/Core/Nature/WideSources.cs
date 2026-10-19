using System.Collections.Concurrent;
using System.Numerics;

namespace OpenFPS.Sound.Core.Nature;

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
    public static WideSources FromEnvironment() => From(System.Environment.GetEnvironmentVariable);

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
        _banked.Clear();
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
            // A line, a fire's burning area and a wood's crowns are cut at the ear instead.
            if (self.LineLength(key, presets) > 0f || self.IsFire(key) || self.IsWood(key)) return null;
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

    /// <summary>Whether a line's bed is heard as its middle and its two banks, each its own flow (BedSection.Banked).
    /// False for anything that is not a line this world cuts.</summary>
    public bool LineBanked(string? physicalKey, IPresets presets)
    {
        if (LineWidth(physicalKey, presets) < LinePath.WideBedMetres) return false;
        return _banked.GetOrAdd(physicalKey!, static (key, presets) =>
        {
            try { return BedSection.Banked(presets.Flow(key[5..])); }
            catch (Exception) { return false; }
        }, presets);
    }
    private readonly ConcurrentDictionary<string, bool> _banked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The path a line source is heard along: the line its emitter carries (SoundEmitterComponent.Line, in its own
    /// frame), or a straight line of <paramref name="length"/> along its own x through <paramref name="position"/>.
    /// </summary>
    public static LinePath PathOf(Vector3 position, Quaternion rotation, Vector3[]? line, float length, float width, bool banked = false)
        => line is { Length: >= 2 }
            ? LinePath.Of(position, rotation, line, width, banked)
            : LinePath.Straight(position, Vector3.Transform(Vector3.UnitX, rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : rotation), length, width, banked);

    /// <summary>A whole shore's outline heard as one source (ShoreSpec.OutlineKey), cut along itself at the ear under
    /// <see cref="ExtendedSources.PlaceRule.Pieces"/>. False for everything when this world does not spread its sources.</summary>
    public bool IsOutline(string? physicalKey)
        => _enabled && _rule == ExtendedSources.PlaceRule.Pieces && ShoreSpec.TryOutline(physicalKey, out _, out _);

    /// <summary>An open water's surface heard from the whole of itself (ShoreSpec.OpenKey), the area inside its outline cut at
    /// the ear (ExtendedSources.CutArea) under <see cref="ExtendedSources.PlaceRule.Pieces"/>. False for everything when this
    /// world does not spread its sources.</summary>
    public bool IsOpenWater(string? physicalKey)
        => _enabled && _rule == ExtendedSources.PlaceRule.Pieces && ShoreSpec.TryOpen(physicalKey, out _, out _);

    /// <summary>A fire heard from its whole burning area (docs/FIRE.md 12.13): its shape cut at the ear as an open water's
    /// surface is (ExtendedSources.CutArea), its flames over runs of the pieces, under <see cref="ExtendedSources.PlaceRule.Pieces"/>.
    /// False for everything when this world does not spread its sources.</summary>
    public bool IsFire(string? physicalKey)
        => _enabled && _rule == ExtendedSources.PlaceRule.Pieces && physicalKey != null && physicalKey.StartsWith("fire:", StringComparison.OrdinalIgnoreCase);

    /// <summary>A wood heard as one (WoodChorus) heard from its crowns: cut at the ear by the bearings they lie on
    /// (ExtendedSources.CutPoints), under <see cref="ExtendedSources.PlaceRule.Pieces"/>. False for everything when this world
    /// does not spread its sources.</summary>
    public bool IsWood(string? physicalKey)
        => _enabled && _rule == ExtendedSources.PlaceRule.Pieces && physicalKey != null && physicalKey.StartsWith(WoodChorus.Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>A surface cut at the ear by the bearings it covers: an open water's or a fire's.</summary>
    public bool IsArea(string? physicalKey) => IsOpenWater(physicalKey) || IsFire(physicalKey);

    /// <summary>Anything this world cuts at the ear rather than lays out: a line, a shore's outline, a surface, a wood.</summary>
    public bool IsCut(string? physicalKey, IPresets presets)
        => LineLength(physicalKey, presets) > 0f || IsOutline(physicalKey) || IsArea(physicalKey) || IsWood(physicalKey);

    /// <summary>How many streams a running-water synth renders: one for each piece a line can be cut into, or
    /// its own places.</summary>
    public int FlowPlaces(string? physicalKey, RunningWaterSpec spec, IPresets presets)
        => LineLength(physicalKey, presets) > 0f ? ExtendedSources.MaxPlaces : Math.Max(1, spec.Places);

    /// <summary>The spread a source of this reach wants at this distance from its middle, 0 to 1
    /// (<see cref="ExtendedSources.SpreadFor"/>), or the lab's forced one.</summary>
    public float SpreadFor(float reachMetres, float distanceMetres)
        => float.IsNaN(ForceSpread) ? ExtendedSources.SpreadFor(reachMetres, distanceMetres) : Math.Clamp(ForceSpread, 0f, 1f);
}
