using System.Globalization;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What a voice draws its chance from (docs/SPECIAL_CASES_AUDIT.md, P3d step 6): a hash of what it is the sound
/// of, its kind, owner and part (VoiceRegistry.SeedOf). Until then a voice's seed was the id it had before ids
/// were given out: an entity's id for its engine, a band's formula for the rest. So a wood could not be given an
/// id without sounding different, and neighbours' seeds were neighbours: a model makes its parts' seeds by
/// adding small numbers to its own, and car 102's front tyres drew what car 101's rear tyres did.
/// </summary>
public class VoiceSeedTests
{
    private readonly ITestOutputHelper _o;
    public VoiceSeedTests(ITestOutputHelper o) => _o = o;

    /// <summary>
    /// The same on every machine and in every run, whatever order the kinds are declared in: these were worked
    /// out apart from the game's code (FNV-1a over the kind's name, then MurmurHash3's finaliser over the owner
    /// and over the part, the top bit dropped). A seed that moves here moves what every client renders.
    /// </summary>
    [Theory]
    [InlineData("Engine", 101, 0, 981579750)]
    [InlineData("Engine", 102, 0, 1155673558)]
    [InlineData("Machine", 703, 0, 1735510816)]
    [InlineData("Wood", -20_000_000, 0, 1366644366)]
    [InlineData("Place", 701, 1, 1796156176)]
    [InlineData("Place", 701, 2, 1474202509)]
    [InlineData("RainDrop", int.MinValue, 0, 1521936818)]
    [InlineData("RainDrop", int.MinValue, 48, 445461833)]
    [InlineData("OwnStep", int.MinValue, -1, 846859412)]
    [InlineData("Footstep", int.MinValue, -1, 1173287103)]
    [InlineData("Siren", 61_001, 0, 219761681)]
    [InlineData("Aid", 1, 0, 601558956)]
    public void A_seed_is_the_same_everywhere(string kind, int owner, int part, int seed)
    {
        Assert.Equal(seed, VoiceRegistry.SeedOf(Enum.Parse<VoiceKind>(kind), owner, part));
        // The numbers above stand for these: nobody's voice, and a landing.
        Assert.Equal(int.MinValue, VoiceRegistry.NoEntity);
        Assert.Equal(-1, VoiceRegistry.Landing);
    }

    /// <summary>The entity ids the id bands met at (S6), Albany's and Magnolia's counts, and the first few.</summary>
    private static readonly int[] Owners = { 1, 2, 3, 101, 102, 14_000, 18_024, 32_598, 61_000, 61_001, 70_047, 100_001, 134_000, 200_001, 241_666, 250_000 };

    /// <summary>
    /// Every kind of every one of these owners, sixteen parts each, and the first two thousand one-offs of each
    /// kind nobody owns: no seed below zero (a model may take its seed's remainder, and an engine's seed, its
    /// entity's id, never was), and no two voices with one seed.
    /// </summary>
    [Fact]
    public void Seeds_are_never_below_zero_and_no_two_of_these_voices_share_one()
    {
        var seen = new Dictionary<int, VoiceKey>();
        void Take(VoiceKind kind, int owner, int part)
        {
            int seed = VoiceRegistry.SeedOf(kind, owner, part);
            Assert.True(seed >= 0, $"{kind} of {owner}, {part}: {seed}");
            Assert.True(seen.TryAdd(seed, new VoiceKey(kind, owner, part)), $"{kind} of {owner}, {part} has the seed of {seen.GetValueOrDefault(seed)}: {seed}");
        }
        foreach (var kind in Enum.GetValues<VoiceKind>())
        {
            foreach (int owner in Owners)
                for (int part = 0; part < 16; part++) Take(kind, owner, part);
            Take(kind, WoodChorus.FirstId, 0);
            Take(kind, WoodChorus.FirstId - 1, 0);
            Take(kind, VoiceRegistry.NoEntity, VoiceRegistry.Landing);
            for (int n = 0; n < 2000; n++) Take(kind, VoiceRegistry.NoEntity, n);
        }
        _o.WriteLine($"{seen.Count} voices, seeds {seen.Keys.Min()} to {seen.Keys.Max()}");
    }

    /// <summary>
    /// A model makes the seeds of its parts by adding to its own (an engine's click is its seed and 5, its wet
    /// tyres its seed and 131, the rear tyres one more), so two voices whose seeds are near each other share
    /// noise: the same hiss from two cars, which the ear hears as one source between them. Entities next to each
    /// other in the map's numbering, each part of one thing and the next, and one one-off and the next are all
    /// further apart than any model reaches from its own seed by adding (a few hundred).
    /// </summary>
    [Fact]
    public void Neighbours_are_seeded_far_apart()
    {
        const int Apart = 5_000;
        int nearest = int.MaxValue;
        void Far(VoiceKind kind, int owner, int part, int nextOwner, int nextPart)
        {
            long d = Math.Abs((long)VoiceRegistry.SeedOf(kind, owner, part) - VoiceRegistry.SeedOf(kind, nextOwner, nextPart));
            nearest = (int)Math.Min(nearest, d);
            Assert.True(d > Apart, $"{kind} of {owner}, {part} and of {nextOwner}, {nextPart} are seeded {d} apart");
        }
        foreach (var kind in new[] { VoiceKind.Engine, VoiceKind.Machine, VoiceKind.Aircraft, VoiceKind.Horn, VoiceKind.HeldHorn, VoiceKind.Wood })
            for (int id = 1; id < 3000; id++) Far(kind, id, 0, id + 1, 0);
        for (int n = 0; n < 3000; n++)
        {
            Far(VoiceKind.EngineEcho, 101, n, 101, n + 1);
            Far(VoiceKind.Reflection, 18_024, n, 18_024, n + 1);
            Far(VoiceKind.Wood, WoodChorus.FirstId - n, 0, WoodChorus.FirstId - n - 1, 0);
        }
        _o.WriteLine($"the nearest two are {nearest} apart");
    }

    public static IEnumerable<object[]> Scenarios() => EmitterStreamReplayTests.Scenarios();

    /// <summary>
    /// In every stored scenario, every voice started with a seed has the seed of what its id is held for: its
    /// kind, owner and part, or for an entity's own voice its kind and its entity. So no voice is seeded by one
    /// thing and numbered by another, and none by its id.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Every_voice_started_is_seeded_by_what_it_is(string scenario)
    {
        const string Mark = "\u0001given ";
        var ownKinds = new Dictionary<int, VoiceKind>();
        var run = Replay.RunScript(r =>
        {
            r.Audio.World.Voices.OnGiven = (id, key) => r.Mixer.Lines.Add(string.Create(CultureInfo.InvariantCulture, $"{Mark}{id} {(int)key.Kind} {key.Owner} {key.Part}"));
            r.AfterTick = x =>
            {
                foreach (var e in x.Audio.World.Voices.Entries)
                    if (VoiceRegistry.IsOwnVoice(e.Kind)) ownKinds[e.VoiceId] = e.Kind;
            };
            Replay.Scenarios[scenario](r);
        });

        var heldFor = new Dictionary<int, VoiceKey>();
        var problems = new List<string>();
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        int seeded = 0, unseeded = 0;
        foreach (var line in run.Mixer.Lines)
        {
            if (line.StartsWith(Mark, StringComparison.Ordinal))
            {
                var p = line[Mark.Length..].Split(' ').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                heldFor[p[0]] = new VoiceKey((VoiceKind)p[1], p[2], p[3]);
                continue;
            }
            var parts = line.Split(' ');
            if (parts.Length < 3 || parts[1] != "Play") continue;
            int id = int.Parse(parts[2], CultureInfo.InvariantCulture);
            int seed = int.Parse(parts.First(f => f.StartsWith("Seed=", StringComparison.Ordinal))["Seed=".Length..], CultureInfo.InvariantCulture);
            // A voice nothing draws from (a recorded one-off, a beacon's blip) is left unseeded.
            if (seed == 0) { unseeded++; continue; }
            if (!heldFor.TryGetValue(id, out var key))
            {
                if (!ownKinds.TryGetValue(id, out var own)) { problems.Add($"frame {parts[0]}: {id} was started with seed {seed} and is nothing's voice"); continue; }
                key = new VoiceKey(own, id, 0);
            }
            seeded++;
            kinds.Add(key.Kind.ToString());
            int expected = VoiceRegistry.SeedOf(key.Kind, key.Owner, key.Part);
            if (seed != expected) problems.Add($"frame {parts[0]}: {id}, {key}, was started with seed {seed}, not {expected}");
            if (seed == id) problems.Add($"frame {parts[0]}: {id}, {key}, is seeded by its id");
        }
        _o.WriteLine($"{scenario}: {seeded} starts seeded ({string.Join(", ", kinds)}), {unseeded} unseeded");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
        Assert.True(seeded > 0, "no voice was seeded: nothing was checked");
    }
}
