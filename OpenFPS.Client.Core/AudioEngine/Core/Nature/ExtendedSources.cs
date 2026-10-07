using System;
using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// A source that is metres across, heard as metres across: a tree's crown, a fire's bed.
///
/// WHY. A crown is thousands of leaves striking over a volume eight metres wide, and each ear hears its
/// own mix of them. Played as one synth at one point through the HRTF, both ears got the same noise,
/// filtered and delayed: interaural coherence 1.00 in every band (docs/AUDIO_QUALITY_2026-10-06.md
/// item 8). Identical noise at both ears is heard as narrow and inside the head, and in the high bands
/// as harsh (Blauert 1997, "Spatial Hearing", 3.3; apparent source width and IACC: Hidaka, Beranek and
/// Okano 1995). A real crown at a few metres measures nothing like it.
///
/// WHAT. The synth renders its events into several PLACES, each its own output and its own voice at
/// its own point on the source: every event (a leaf strike, a crackle) goes to exactly one place, and
/// every continuous part (the air past the twigs, the flames' roar, the fizz) has its own noise at
/// each place. So the places are independent streams, not copies of one stream: copies of one signal
/// from two points comb (coherent-copies-sound-inside-out), independent streams do not. And because an
/// event goes to one place and the noise's power is split, the places' powers add to the source's, at
/// any split.
///
/// HOW MANY. Place 0 is the source's middle (the map's emitter). The others are spread over it. How
/// much of the source goes to them is the SPREAD, from the angle the places subtend from the listener:
/// none under <see cref="PointDegrees"/> (the places would be closer together than the ear can tell
/// apart, so they are one voice and cost one), all of it over <see cref="FullDegrees"/>, and it slews
/// at <see cref="SlewPerSecond"/>, so walking up to a tree widens it over a second or so, with no step
/// in level. Merged, the outer places' voices are let go.
///
/// OPENFPS_WIDE_SOURCES=0 plays every one from one point, the game before 2026-10-06.
/// </summary>
public static class ExtendedSources
{
    public static bool Enabled = Environment.GetEnvironmentVariable("OPENFPS_WIDE_SOURCES") != "0";

    /// <summary>Under this half-angle the places are one point to the ear, degrees: the localisation
    /// blur straight ahead is about 1-4° (Blauert 1997, table 2.1), so two sources this far apart are
    /// not told apart, and a source this narrow is not heard as wide.</summary>
    public const float PointDegrees = 2.5f;

    /// <summary>From this half-angle the source is played wholly from its places, degrees.</summary>
    public const float FullDegrees = 6f;

    /// <summary>How fast the spread moves, per second: a full swing in about 1.4 s.</summary>
    public const float SlewPerSecond = 0.7f;

    /// <summary>How many places a tree's crown is heard from round its middle: one per bough
    /// (FoliageSynth.Boughs). Measured (AudioLab --wide-sources, 2026-10-06) against three.</summary>
    public static int TreePlaces = int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_WIDE_TREE_PLACES"), out int t) && t >= 1
        ? Math.Min(t, FoliageSynth.Boughs) : FoliageSynth.Boughs;

    /// <summary>How many places a fire pit's bed is heard from round its flames; a bigger fire says its own
    /// (FireSpec.Places, FireSynth.Layout).</summary>
    public const int FirePlaces = 3;

    /// <summary>How many places each tap of a water feature is heard from round its middle.</summary>
    public const int WaterTapPlaces = 3;

    /// <summary>The most places a source is heard from, its middle included: the voice ids the client gives a
    /// source (ClientAudioSystem.PlaceVoiceId). A layout with more is heard from its middle alone.</summary>
    public const int MaxPlaces = 12;

    /// <summary>For the lab: how far out the places are, as a share of where they belong. Zero puts every
    /// place on the middle, which leaves the streams independent and changes only where they are heard
    /// from (AudioLab --wide-sources collapse=1). Read when a layout is first made.</summary>
    internal static float LayoutScale = 1f;

    /// <summary>For the lab: a spread to use at any distance instead of the one the angle asks for.</summary>
    internal static float ForceSpread = float.NaN;

    private static readonly ConcurrentDictionary<string, Vector3[]?> _layouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The places of a source, by its physical key, as offsets from its middle in the source's own frame
    /// (x east, y up, z north on an unrotated entity): place 0 is the middle itself. Null for anything
    /// that is not an extended source of this kind (a machine, a whole fountain at one point, the rain).
    /// </summary>
    public static Vector3[]? Layout(string? physicalKey)
    {
        if (!Enabled || string.IsNullOrEmpty(physicalKey)) return null;
        var layout = _layouts.GetOrAdd(physicalKey, static key => Make(key));
        return layout != null && layout.Length <= MaxPlaces ? layout : null;
    }

    private static Vector3[]? Make(string key)
    {
        try
        {
            if (key.StartsWith("foliage:", StringComparison.OrdinalIgnoreCase))
            {
                var spec = FoliageSpec.ByName(key[8..]);
                int n = TreePlaces;
                var places = new Vector3[1 + n];
                for (int j = 0; j < n; j++)
                {
                    // The middle of the boughs this place stands for.
                    Vector3 sum = Vector3.Zero;
                    int count = 0;
                    for (int b = 0; b < FoliageSynth.Boughs; b++)
                        if (FoliageSynth.PlaceOfBough(b, n) == j) { sum += FoliageSynth.BoughOffset(spec, b); count++; }
                    places[1 + j] = count > 0 ? sum / count * LayoutScale : Vector3.Zero;
                }
                return places;
            }
            // One tap of a water feature ("water:<preset>/<feature>/<tap>"): round the place its water
            // lands, at half its extent. The whole feature at one point ("water:<preset>") is not spread.
            if (key.StartsWith("water:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = key[6..].Split('/');
                if (parts.Length != 3 || !int.TryParse(parts[2], out int tap)) return null;
                var spec = WaterFeatureSpec.ByName(parts[0]);
                if (tap < 0 || tap >= spec.Taps.Length) return null;
                float r = 0.5f * spec.Taps[tap].ExtentMetres * LayoutScale;
                var places = new Vector3[1 + WaterTapPlaces];
                for (int j = 0; j < WaterTapPlaces; j++)
                {
                    float a = MathF.Tau * (j + 0.25f) / WaterTapPlaces;
                    places[1 + j] = new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
                }
                return places;
            }
            // Running water: along its length, or round where it lands (RunningWaterSynth.Layout).
            if (key.StartsWith("flow:", StringComparison.OrdinalIgnoreCase))
            {
                var spec = RunningWaterSpec.ByName(key[5..]);
                if (spec.Places <= 1) return null;
                var places = RunningWaterSynth.Layout(spec);
                for (int i = 0; i < places.Length; i++) places[i] *= LayoutScale;
                return places;
            }
            // Waves at an edge: along it, and on its break line (ShoreSynth.Layout), its length the map's.
            if (key.StartsWith("shore:", StringComparison.OrdinalIgnoreCase))
            {
                ShoreSpec.ParseKey(key, out string preset, out var geometry);
                var spec = ShoreSpec.ByName(preset);
                if (spec.TotalPlaces <= 1) return null;
                var places = ShoreSynth.Layout(spec, geometry?.LengthMetres ?? spec.LengthMetres);
                for (int i = 0; i < places.Length; i++) places[i] *= LayoutScale;
                return places;
            }
            // A wood heard as one (WoodChorus): a place a bough's worth of it, six round the wood at
            // two-thirds of its half-widths, where its trees' wind is read as well (FoliageSynth.ReadWindAt).
            if (WoodChorus.ParseKey(key, out _, out float rx, out float rz))
            {
                int n = FoliageSynth.Boughs;
                var places = new Vector3[1 + n];
                for (int j = 0; j < n; j++)
                {
                    float a = MathF.Tau * (j + 0.5f) / n;
                    places[1 + j] = new Vector3(MathF.Cos(a) * rx, 0f, MathF.Sin(a) * rz) * (2f / 3f) * LayoutScale;
                }
                return places;
            }
            if (key.StartsWith("fire:", StringComparison.OrdinalIgnoreCase))
            {
                var spec = FireSpec.ByName(key[5..]);
                var places = FireSynth.Layout(spec);
                if (places.Length <= 1) return null;
                for (int i = 0; i < places.Length; i++) places[i] *= LayoutScale;
                return places;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>How far the outer places reach from the middle, m.</summary>
    public static float Reach(ReadOnlySpan<Vector3> layout)
    {
        float r = 0f;
        for (int i = 1; i < layout.Length; i++) r = MathF.Max(r, layout[i].Length());
        return r;
    }

    /// <summary>The spread a source of this reach wants at this distance from its middle, 0 to 1.</summary>
    public static float SpreadFor(float reachMetres, float distanceMetres)
    {
        if (!float.IsNaN(ForceSpread)) return Math.Clamp(ForceSpread, 0f, 1f);
        if (reachMetres <= 0f) return 0f;
        float half = MathF.Atan2(reachMetres, MathF.Max(0.01f, distanceMetres)) * (180f / MathF.PI);
        float x = Math.Clamp((half - PointDegrees) / (FullDegrees - PointDegrees), 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>Moves a spread toward its target at the slew rate.</summary>
    public static float Slew(float now, float target, float dt)
    {
        float step = SlewPerSecond * MathF.Max(0f, dt);
        return now + Math.Clamp(target - now, -step, step);
    }

    /// <summary>
    /// Each place's share of the source's power at a spread: the middle keeps 1 - s·n/(n+1), each of
    /// the n others s/(n+1). At full spread every place has an equal share; merged, the middle has it
    /// all. They always sum to one.
    /// </summary>
    public static void Shares(float spread, Span<float> shares)
    {
        int n = shares.Length - 1;
        if (n <= 0) { if (shares.Length == 1) shares[0] = 1f; return; }
        float s = Math.Clamp(spread, 0f, 1f);
        float each = s / (n + 1);
        shares[0] = 1f - each * n;
        for (int i = 1; i <= n; i++) shares[i] = each;
    }

    /// <summary>
    /// The one gain every place's voice is given so that together they are as loud as the source played
    /// from its middle, at this listener: the places are at their own distances, and the mixer's law
    /// (Loudness.RenderedGain, applied per voice) makes the near ones louder. That is right for where the
    /// sound comes from and wrong for how much of it there is, which the source's declared level and its
    /// extent already say. So the powers the voices will render are summed and the total is set to what
    /// the middle would render alone. One for a merged source, and inside the extent, where the law is flat.
    /// </summary>
    public static float Balance(Vector3 listener, Vector3 middle, ReadOnlySpan<Vector3> places, ReadOnlySpan<float> shares,
                                float referenceMetres, float rangeMetres)
    {
        float centre = Loudness.RenderedGain(1f, referenceMetres, rangeMetres, Vector3.Distance(listener, middle));
        double sum = 0;
        for (int i = 0; i < places.Length && i < shares.Length; i++)
        {
            float g = Loudness.RenderedGain(1f, referenceMetres, rangeMetres, Vector3.Distance(listener, places[i]));
            sum += shares[i] * (double)g * g;
        }
        if (sum <= 1e-20 || centre <= 0f) return 1f;
        return (float)(centre / Math.Sqrt(sum));
    }
}
