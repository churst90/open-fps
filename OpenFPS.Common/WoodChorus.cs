using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// The wind in a wood, heard from further than the wind in one tree.
///
/// One tree's crown is heard to about 90 m (FoliageSpec's level, Loudness.AudibleRange). A wood of N of
/// them is N independent streams of leaf strikes and shedding noise: the same sound, N times the power,
/// 10 log N dB louder, and so heard much further than any one of its trees. Played tree by tree, every
/// tree past 90 m was culled as inaudible and the wood went silent with them. So past
/// <see cref="ChorusMetres"/> a tree is not a voice of its own: the trees of each wood (by 200 m cell and
/// species) are one source, placed over the wood's real extent (ExtendedSources, key "wood:"), whose synth
/// renders as many trees as it stands for (FoliageSynth.Trees) and reads the wind across the wood, so a
/// gust crosses it as it crosses the trees.
///
/// HANDOVER. Between <see cref="IndividualMetres"/> and <see cref="ChorusMetres"/> a tree is in both, its
/// power split between its own voice and the wood's by a smooth weight, so walking into a wood nothing
/// steps: the shares always add to the tree. The wood's gain is set from its trees' own distances (each
/// as 1/d from its own place), not from the wood's middle, so the wood renders exactly the power its trees
/// would at their own distances, whatever the wood's shape and wherever the listener stands.
///
/// Built from the crowns the client holds (ClientWorldState.RefreshWoods); <see cref="Weigh"/> runs every
/// audio frame. Pure: the lab and the tests use it as the game does. docs/WORLD_STREAMING.md.
/// </summary>
public sealed class WoodChorus
{
    /// <summary>Within this a tree is wholly its own voice, m.</summary>
    public const float IndividualMetres = 70f;
    /// <summary>From this a tree is wholly in its wood, m. One tree is audible to about 90 m, so the
    /// hand-over is done before a tree would have faded on its own.</summary>
    public const float ChorusMetres = 110f;
    /// <summary>A wood is the trees of one species in one square this size, m.</summary>
    public const float CellMetres = 200f;
    /// <summary>The first synthetic entity id of a wood; each next is one lower.</summary>
    public const int FirstId = -20_000_000;
    public const string Prefix = "wood:";

    public readonly record struct Crown(int Id, Vector3 At, string Preset);

    public sealed class Wood
    {
        public int Id;
        public string Preset = "";
        public (int X, int Z) Cell;
        public Vector3 Centre;
        public float RadiusX, RadiusZ;
        public string Key = "";
        public float RangeMetres;
        public float ReferenceMetres;
        public int[] Crowns = Array.Empty<int>();   // indices into the chorus's crowns
        public float Extent => MathF.Max(RadiusX, RadiusZ);
    }

    private readonly Crown[] _crowns;
    private readonly Wood[] _woods;
    private readonly Dictionary<int, float> _treeReference = new();

    public IReadOnlyList<Wood> Woods => _woods;
    public IReadOnlyList<Crown> Crowns => _crowns;

    private WoodChorus(Crown[] crowns, Wood[] woods) { _crowns = crowns; _woods = woods; }

    /// <summary>The woods of these crowns. <paramref name="idFor"/> gives each wood (cell and species) its
    /// entity id, so a wood keeps its id while its trees come and go.</summary>
    public static WoodChorus Build(IReadOnlyList<Crown> crowns, Func<(int X, int Z), string, int> idFor)
    {
        var groups = new Dictionary<((int, int) Cell, string Preset), List<int>>();
        for (int i = 0; i < crowns.Count; i++)
        {
            var c = crowns[i];
            var cell = ((int)MathF.Floor(c.At.X / CellMetres), (int)MathF.Floor(c.At.Z / CellMetres));
            var key = (cell, c.Preset.ToLowerInvariant());
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<int>();
            list.Add(i);
        }
        var woods = new List<Wood>();
        foreach (var ((cell, preset), members) in groups)
        {
            FoliageSpec spec;
            try { spec = FoliageSpec.ByName(preset); } catch (Exception) { continue; }
            var centre = Vector3.Zero;
            foreach (int i in members) centre += crowns[i].At;
            centre /= members.Count;
            float rx = 0f, rz = 0f;
            foreach (int i in members)
            {
                rx = MathF.Max(rx, MathF.Abs(crowns[i].At.X - centre.X));
                rz = MathF.Max(rz, MathF.Abs(crowns[i].At.Z - centre.Z));
            }
            // Out to the crowns' edges, and in steps of ten metres: the key carries the size, and a
            // wood whose size does not change keeps its voice.
            rx = MathF.Ceiling((rx + spec.CrownRadiusMetres) / 10f) * 10f;
            rz = MathF.Ceiling((rz + spec.CrownRadiusMetres) / 10f) * 10f;
            var (_, reference) = Loudness.Place(spec.SourceLevelDb, spec.ExtentMetres);
            woods.Add(new Wood
            {
                Id = idFor(cell, preset),
                Preset = preset,
                Cell = cell,
                Centre = centre,
                RadiusX = rx,
                RadiusZ = rz,
                Key = KeyFor(preset, rx, rz),
                RangeMetres = Loudness.AudibleRange(spec.SourceLevelDb + 10f * MathF.Log10(members.Count)) + MathF.Max(rx, rz),
                ReferenceMetres = reference,
                Crowns = members.ToArray(),
            });
        }
        woods.Sort((a, b) => b.Id.CompareTo(a.Id));
        return new WoodChorus(crowns is Crown[] arr ? arr : new List<Crown>(crowns).ToArray(), woods.ToArray());
    }

    public static string KeyFor(string preset, float radiusX, float radiusZ)
        => FormattableString.Invariant($"{Prefix}{preset}@{radiusX:F0}x{radiusZ:F0}");

    /// <summary>A wood's key: "wood:&lt;foliage preset&gt;@&lt;radius x&gt;x&lt;radius z&gt;".</summary>
    public static bool ParseKey(string? key, out string preset, out float radiusX, out float radiusZ)
    {
        preset = ""; radiusX = radiusZ = 0f;
        if (key == null || !key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = key[Prefix.Length..];
        int at = rest.IndexOf('@');
        if (at <= 0) return false;
        preset = rest[..at];
        var size = rest[(at + 1)..].Split('x');
        return size.Length == 2
            && float.TryParse(size[0], NumberStyles.Float, CultureInfo.InvariantCulture, out radiusX)
            && float.TryParse(size[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radiusZ);
    }

    /// <summary>A tree's power share in its own voice at this distance: 1 near, 0 from the chorus on.</summary>
    public static float IndividualShare(float distance)
    {
        float x = Math.Clamp((distance - IndividualMetres) / (ChorusMetres - IndividualMetres), 0f, 1f);
        return 1f - x * x * (3f - 2f * x);
    }

    /// <summary>What one frame plays: each tree's amplitude in its own voice, and each wood's trees and
    /// gain. Reused frame to frame.</summary>
    public sealed class Weights
    {
        /// <summary>A crown's amplitude gain in its own voice (0: not a voice of its own).</summary>
        public readonly Dictionary<int, float> Individual = new();
        /// <summary>A wood's trees now (its synth's FoliageSynth.Trees) and the amplitude gain that sets
        /// its rendered power to its trees' at their own distances.</summary>
        public readonly Dictionary<int, (float Trees, float Gain)> Woods = new();
    }

    /// <summary>
    /// The shares for a listener at <paramref name="ear"/>. <paramref name="voiced"/> says which trees have
    /// a voice of their own now (the audio system's budget holds standing sources to a few): a tree that
    /// has none is heard in its wood whatever its distance, so a wood within the hand-over does not lose
    /// the trees the budget left out. Its own share is still given (<see cref="Weights.Individual"/>), so it
    /// can be ranked for a voice; once it has one, it leaves the wood.
    /// </summary>
    public void Weigh(Vector3 ear, Weights into, Func<int, bool>? voiced = null)
    {
        into.Individual.Clear();
        into.Woods.Clear();
        foreach (var w in _woods)
        {
            double trees = 0, power = 0;
            float refT = w.ReferenceMetres;
            foreach (int i in w.Crowns)
            {
                var c = _crowns[i];
                float d = Vector3.Distance(ear, c.At);
                float own = IndividualShare(d);
                into.Individual[c.Id] = MathF.Sqrt(own);
                float rest = voiced == null || voiced(c.Id) ? 1f - own : 1f;
                if (rest <= 0f) continue;
                trees += rest;
                float dd = MathF.Max(d, refT);
                power += rest / (dd * (double)dd);
            }
            if (trees <= 1e-4) { into.Woods[w.Id] = (0f, 0f); continue; }
            // As 1/d from each tree's own place, against the wood's own law: flat inside its extent,
            // 1/d from its middle beyond it (Loudness.Widen keeps gain × reference, so only the
            // distance term differs).
            float dw = MathF.Max(Vector3.Distance(ear, w.Centre), MathF.Max(refT, w.Extent));
            float gain = (float)Math.Sqrt(power * dw * dw / trees);
            into.Woods[w.Id] = ((float)trees, Math.Clamp(gain, 0f, 4f));
        }
    }
}
