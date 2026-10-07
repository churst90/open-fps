using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Rail;

/// <summary>
/// Which of a train's sources share a voice, and at what weight (TrainSlotState, docs/TRAINS.md
/// "Voicing a train"). Pure, so the tests can ask it anything.
///
/// The ear places a source to within a few degrees; two sources closer together than that, as seen from
/// where the listener stands, are one sound from one place. So the sources are grouped along the train,
/// neighbours with neighbours, the pair that look closest together merged first, until no more than
/// <see cref="MaxFieldVoices"/> groups are left and every pair still apart is further apart than
/// <see cref="MergeBelowDegrees"/>. Beside the line, the bogies passing you are each their own voice and
/// the far end of the train is one; a kilometre off, the whole train is one.
///
/// A source played through a voice that is not its own keeps its own level: its weight
/// (<see cref="Weight"/>) is what makes it as loud there as the mixer would have played it alone.
/// </summary>
public static class TrainVoicing
{
    /// <summary>Voices for the rolling stock and the engines.</summary>
    public const int MaxFieldVoices = 4;
    /// <summary>The horn or whistle's voice, while it sounds; and the bell's.</summary>
    public const int WarningSlot = MaxFieldVoices, BellSlot = MaxFieldVoices + 1;
    /// <summary>Every voice a train can have.</summary>
    public const int Slots = MaxFieldVoices + 2;

    /// <summary>Two groups closer together than this are merged whatever the count.</summary>
    public const float MergeBelowDegrees = 4f;
    /// <summary>A group is split back out only past this (the hysteresis that keeps a voice from coming
    /// and going as the train moves a metre).</summary>
    public const float SplitAboveDegrees = 7f;

    public readonly record struct Source(int Index, float Along, Vector3 Position);

    /// <summary>
    /// Groups <paramref name="sources"/> (any order) into runs along the train, as above. Returns each
    /// group's sources by their Index, in order along the train. <paramref name="previousGroups"/> is how
    /// many there were last time: fewer than that are kept unless a pair is further apart than
    /// <see cref="SplitAboveDegrees"/>, so the count does not flicker.
    /// </summary>
    public static List<List<int>> Group(IReadOnlyList<Source> sources, Vector3 ear, int maxGroups, int previousGroups)
    {
        var sorted = sources.OrderBy(s => s.Along).ToList();
        var groups = new List<(int First, int Last)>();
        for (int i = 0; i < sorted.Count; i++) groups.Add((i, i));
        var dir = sorted.Select(s => Vector3.Normalize(s.Position - ear + new Vector3(0f, 1e-4f, 0f))).ToArray();
        float mergeBelow = MathF.Cos(MergeBelowDegrees * MathF.PI / 180f);
        float splitAbove = MathF.Cos(SplitAboveDegrees * MathF.PI / 180f);

        while (groups.Count > 1)
        {
            // The neighbouring pair that, merged, would span the smallest angle.
            int best = -1;
            float bestCos = -2f;
            for (int g = 0; g + 1 < groups.Count; g++)
            {
                float c = Vector3.Dot(dir[groups[g].First], dir[groups[g + 1].Last]);
                if (c > bestCos) { bestCos = c; best = g; }
            }
            bool tooMany = groups.Count > Math.Max(1, maxGroups);
            // Fewer than last time only for a pair the ear cannot tell apart; as many as last time unless a
            // pair stands well apart.
            float threshold = groups.Count > previousGroups ? splitAbove : mergeBelow;
            if (!tooMany && bestCos < threshold) break;
            groups[best] = (groups[best].First, groups[best + 1].Last);
            groups.RemoveAt(best + 1);
        }

        var result = new List<List<int>>(groups.Count);
        foreach (var (first, last) in groups)
        {
            var g = new List<int>(last - first + 1);
            for (int i = first; i <= last; i++) g.Add(sorted[i].Index);
            result.Add(g);
        }
        return result;
    }

    /// <summary>
    /// Which voice each group goes to: the one already carrying most of its sources, so a group drifting
    /// along the train keeps its voice; a group with no voice takes a free one: one still playing with
    /// nothing in it (<paramref name="held"/>) first, then one idle before it, over one being handed on.
    /// <paramref name="previousSlotOf"/> is each source's voice last time.
    /// </summary>
    public static int[] AssignSlots(IReadOnlyList<IReadOnlyList<int>> groups, IReadOnlyDictionary<int, int> previousSlotOf,
                                    IReadOnlyList<bool>? held = null)
    {
        var slotOf = new int[groups.Count];
        Array.Fill(slotOf, -1);
        var taken = new bool[MaxFieldVoices];
        var used = new bool[MaxFieldVoices];
        foreach (var s in previousSlotOf.Values) if (s >= 0 && s < MaxFieldVoices) used[s] = true;

        // The biggest overlaps first.
        var claims = new List<(int Group, int Slot, int Count)>();
        for (int g = 0; g < groups.Count; g++)
        {
            var counts = new int[MaxFieldVoices];
            foreach (int i in groups[g])
                if (previousSlotOf.TryGetValue(i, out int s) && s >= 0 && s < MaxFieldVoices) counts[s]++;
            for (int s = 0; s < MaxFieldVoices; s++) if (counts[s] > 0) claims.Add((g, s, counts[s]));
        }
        foreach (var (g, s, _) in claims.OrderByDescending(c => c.Count))
        {
            if (slotOf[g] >= 0 || taken[s]) continue;
            slotOf[g] = s;
            taken[s] = true;
        }
        for (int g = 0; g < groups.Count; g++)
        {
            if (slotOf[g] >= 0) continue;
            int pick = -1;
            for (int s = 0; s < MaxFieldVoices && pick < 0; s++) if (!taken[s] && !used[s] && held != null && s < held.Count && held[s]) pick = s;
            for (int s = 0; s < MaxFieldVoices && pick < 0; s++) if (!taken[s] && !used[s]) pick = s;
            for (int s = 0; s < MaxFieldVoices && pick < 0; s++) if (!taken[s]) pick = s;
            if (pick < 0) continue;     // more groups than voices: Group never makes that many
            slotOf[g] = pick;
            taken[pick] = true;
        }
        return slotOf;
    }

    /// <summary>The level a train's field voices are declared at: its loudest source that is not a
    /// signal. What the weights are worked out against; any figure would do, this one keeps them near one.</summary>
    public static float SlotLevelDb(IReadOnlyList<TrainLayout.Entry> layout)
    {
        float max = 0f;
        foreach (var e in layout) if (!e.IsSignal) max = MathF.Max(max, e.LevelDb);
        return max;
    }

    /// <summary>
    /// The mixer's gain for a physical source of this level and size at this distance, as it would play
    /// it through a voice of its own (Loudness.Place, then the law), before the path and the ear model.
    /// </summary>
    public static float LawGain(float levelDb, float extentMetres, float distance)
    {
        var (gain, reference) = Loudness.Place(levelDb, extentMetres);
        return Loudness.RenderedGain(gain, reference, Loudness.AudibleRange(levelDb), distance);
    }

    /// <summary>
    /// The weight that plays a source of <paramref name="sourceLevelDb"/>, which its own voice would play at
    /// gain <paramref name="sourceGain"/>, through a voice declared at <paramref name="slotLevelDb"/> and
    /// played at <paramref name="slotGain"/>, at the level it would have had. Every physical voice renders
    /// its declared level at the same point under full scale, so a source's pressure is scaled by the two
    /// levels' ratio and by the two gains'.
    /// </summary>
    public static float Weight(float sourceLevelDb, float sourceGain, float slotLevelDb, float slotGain)
        => slotGain > 1e-12f
            ? MathF.Pow(10f, (slotLevelDb - sourceLevelDb) / 20f) * sourceGain / slotGain
            : 0f;
}
