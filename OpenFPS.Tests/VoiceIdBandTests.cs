using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;
using OpenFPS.Acoustics;

namespace OpenFPS.Tests;

/// <summary>
/// No two live voices share an id (docs/SPECIAL_CASES_AUDIT.md, S6 and P3c). The ids were bands, each a formula
/// of an entity id or a counter set by hand to sit apart, and 47 pairs of them met at the entity counts the maps
/// have or after enough was counted: a siren took a driving aid's id, a loudspeaker's wall copies the one-offs'.
/// The registry gives them out now: an entity's own voice is under its entity id, everything else under an id
/// held while it is wanted and then free for the next.
/// </summary>
public class VoiceIdBandTests
{
    private readonly ITestOutputHelper _o;
    public VoiceIdBandTests(ITestOutputHelper o) => _o = o;

    public static IEnumerable<object[]> Scenarios() => EmitterStreamReplayTests.Scenarios();

    /// <summary>
    /// In every stored scenario: an id is given only while nothing is under it (no entry, nothing sounding),
    /// and at the end of every frame every voice the mixer holds is under its entity's own id or under an id the
    /// registry holds for it, as is every entry. So no id is two voices at once.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void NoTwoLiveVoicesShareAnId(string scenario)
    {
        var problems = new List<string>();
        int given = 0, checkedVoices = 0;
        Replay.RunScript(r =>
        {
            r.Audio.World.Voices.OnGiven = (id, key) =>
            {
                given++;
                if (id > VoiceRegistry.FirstId || id < VoiceRegistry.LastId) problems.Add($"{id} for {key} is outside the ids given");
                if (key.Kind != VoiceKind.Probe && r.Mixer.IsPlaying(id)) problems.Add($"frame {r.Frame}: {id} given for {key} while a voice sounds under it");
                if (r.Audio.World.Voices.IsRegistered(id)) problems.Add($"frame {r.Frame}: {id} given for {key} while it is an entry");
            };
            r.AfterTick = x =>
            {
                foreach (int id in x.Mixer.GetActiveSpatialSoundIds())
                {
                    checkedVoices++;
                    if (id >= 0) continue;                                       // an entity's own voice
                    if (!x.Audio.World.Voices.TryKeyOf(id, out _)) problems.Add($"frame {x.Frame}: {id} sounds and no id is held for it");
                }
                foreach (var e in x.Audio.World.Voices.Entries)
                {
                    if (VoiceRegistry.IsOwnVoice(e.Kind))
                    {
                        if (e.VoiceId != e.OwnerId) problems.Add($"frame {x.Frame}: {e.Kind} of {e.OwnerId} is under {e.VoiceId}");
                    }
                    else if (!x.Audio.World.Voices.TryKeyOf(e.VoiceId, out _)) problems.Add($"frame {x.Frame}: entry {e.VoiceId} ({e.Kind}) has no id held");
                }
            };
            Replay.Scenarios[scenario](r);
        });
        _o.WriteLine($"{scenario}: {given} ids given, {checkedVoices} voice-frames checked");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
        Assert.True(given > 0, "no id was given: nothing was checked");
    }

    /// <summary>
    /// A place of a tree, a tap or a wood reads its middle's synth, which the mixer finds by the id the place
    /// names (SpatialEmitter.PlaceOfEntity): by the end of the frame a place starts in, a voice sounds under that
    /// id. A wood's voice is under an id given out, so its places name that and not the wood's entity.
    /// </summary>
    [Fact]
    public void A_place_names_a_voice_that_sounds()
    {
        var live = new HashSet<string>();
        var started = new List<(string Place, string Middle)>();
        var problems = new List<string>();
        var middles = new HashSet<string>();
        string frame = "";
        void EndFrame()
        {
            foreach (var (place, middle) in started)
                if (!live.Contains(middle)) problems.Add($"frame {frame}: place {place} names {middle}, which is no voice");
            started.Clear();
        }
        foreach (var line in Replay.Run("places_and_woods"))
        {
            var p = line.Split(' ');
            if (p.Length < 3) continue;
            if (p[0] != frame) { EndFrame(); frame = p[0]; }
            if (p[1] == "Stop") live.Remove(p[2]);
            if (p[1] != "Play") continue;
            live.Add(p[2]);
            string middle = p.First(f => f.StartsWith("PlaceOfEntity=", StringComparison.Ordinal))["PlaceOfEntity=".Length..];
            if (middle == "0") continue;
            started.Add((p[2], middle));
            middles.Add(middle);
        }
        EndFrame();
        _o.WriteLine($"places_and_woods: places of {middles.Count} sources ({string.Join(", ", middles)})");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
        // The tap, the tree and four of the woods spread here; a wood's middle is under an id given out, below zero.
        int woods = middles.Count(m => m.StartsWith('-'));
        Assert.True(woods >= 4 && middles.Count >= 6, $"{middles.Count} sources had places, {woods} of them woods: the woods' were not checked");
    }

    /// <summary>The entity ids S6's overlaps start from, Albany's and Magnolia's counts, and the furthest it checked.</summary>
    private static readonly int[] S6EntityIds = { 1, 14_000, 18_024, 32_598, 61_000, 61_001, 70_047, 100_001, 134_000, 200_001, 241_666, 250_000 };

    /// <summary>
    /// At every entity id S6 named, every voice an entity can have alongside every voice nobody's, all at once:
    /// all different, none an entity's id nor a wood's.
    /// </summary>
    [Fact]
    public void Every_voice_of_every_entity_at_the_ids_S6_named_is_an_id_of_its_own()
    {
        var reg = new VoiceRegistry { Frame = 1 };
        var seen = new Dictionary<int, VoiceKey>();
        void Give(VoiceKind kind, int owner, int part = 0)
        {
            int id = reg.IdFor(kind, owner, part);
            Assert.True(id <= VoiceRegistry.FirstId && id >= VoiceRegistry.LastId, $"{kind} of {owner}: {id}");
            Assert.True(seen.TryAdd(id, new VoiceKey(kind, owner, part)), $"{kind} of {owner}, {part}: {id} is {seen.GetValueOrDefault(id)}'s too");
        }
        foreach (int e in S6EntityIds)
        {
            for (int slot = 0; slot < EarlyReflections.MaxArrivals; slot++) Give(VoiceKind.Reflection, e, slot);
            foreach (var kind in new[] { VoiceKind.Intake, VoiceKind.Borrowed, VoiceKind.Siren, VoiceKind.Horn, VoiceKind.HeldHorn,
                                         VoiceKind.Talker, VoiceKind.Probe })
                Give(kind, e);
            for (int k = 1; k < ExtendedSources.MaxPlaces; k++) Give(VoiceKind.Place, e, k);
            for (int k = 0; k < 2; k++) Give(VoiceKind.EngineEcho, e, e * 2 + k);
            Give(VoiceKind.Wood, WoodChorus.FirstId - e);
            Give(VoiceKind.Place, WoodChorus.FirstId - e, 1);
        }
        foreach (var aid in Enum.GetValues<AidVoice>())
            for (int n = 0; n < Math.Max(1, OldVoiceIds.TurnsOf(VoiceKind.Aid, (int)aid)); n++) Give(VoiceKind.Aid, (int)aid, n);
        foreach (var (kind, n) in new[] { (VoiceKind.Transient, 64), (VoiceKind.Beacon, 24), (VoiceKind.Bird, 48), (VoiceKind.StepEcho, 48),
                                          (VoiceKind.RainDrop, 48), (VoiceKind.OwnStep, 12), (VoiceKind.Footstep, 64),
                                          (VoiceKind.OwnVoiceCopy, 8), (VoiceKind.Reference, 2) })
            for (int k = 0; k < n; k++) Give(kind, VoiceRegistry.NoEntity, k);
        Give(VoiceKind.OwnVoice, VoiceRegistry.NoEntity);
        Give(VoiceKind.OwnStep, VoiceRegistry.NoEntity, VoiceRegistry.Landing);
        for (int p = 1; p < 12; p++) Give(VoiceKind.Cabin, VoiceRegistry.NoEntity, p);
        for (int room = 0; room < 8; room++) for (int k = 0; k < 8; k++) Give(VoiceKind.TalkerCopy, room, k);
        for (int train = 0; train < 50; train++) for (int slot = 0; slot < 8; slot++) Give(VoiceKind.TrainField, train, slot);
        for (int s = 0; s < 9; s++) for (int k = 0; k < 8; k++) Give(VoiceKind.Rain, s, k);
        _o.WriteLine($"{seen.Count} voices at once, ids {seen.Keys.Max()} down to {seen.Keys.Min()}");
        Assert.Equal(seen.Count, reg.IdsHeld);
    }

    /// <summary>
    /// A million engine echoes one after another, each let go when it stops: the ids are used again, and never
    /// reach another voice's (S6: never reused, they reached the borrowed cars' band after about ten hours).
    /// </summary>
    [Fact]
    public void A_count_never_reused_runs_on_in_the_same_few_ids()
    {
        var reg = new VoiceRegistry { Frame = 1 };
        int siren = reg.IdFor(VoiceKind.Siren, 61_001);
        int lowest = 0;
        var fading = new Queue<int>();
        for (int n = 0; n < 1_000_000; n++)
        {
            reg.Frame = n + 2;
            int id = reg.IdFor(VoiceKind.EngineEcho, 7, n);
            Assert.NotEqual(siren, id);
            reg.Declare(siren, 61_001, VoiceKind.Siren, default);
            lowest = Math.Min(lowest, id);
            // The last four still fading out.
            fading.Enqueue(id);
            if (fading.Count > 4) fading.Dequeue();
            reg.Sweep(reg.Frame, fading.Contains);
        }
        _o.WriteLine($"a million echoes in ids {VoiceRegistry.FirstId} down to {lowest}; {reg.IdsHeld} held at the end");
        Assert.True(lowest >= VoiceRegistry.FirstId - 16, $"the echoes reached {lowest}");
        Assert.Equal(siren, reg.IdFor(VoiceKind.Siren, 61_001));
    }

    /// <summary>
    /// An id is held while its voice is an entry, while the mixer still has it (a voice fading out after its
    /// owner let it go), and in the frame it was asked for; then it is the next voice's. A question's id is held
    /// as long as the worker keeps what it traced. An entity's own voice is under its entity id, a wood's under
    /// an id given out.
    /// </summary>
    [Fact]
    public void An_id_is_held_until_its_voice_has_stopped_then_free()
    {
        var reg = new VoiceRegistry { Frame = 1, ProbeHoldFrames = 3 };
        Assert.Equal(4242, reg.IdFor(VoiceKind.Engine, 4242));
        // A wood is an entity the client made: its voice is under an id given out, the same while it is held.
        int wood = reg.Hold(VoiceKind.Wood, WoodChorus.FirstId - 3);
        Assert.InRange(wood, VoiceRegistry.LastId, VoiceRegistry.FirstId);
        Assert.Equal(wood, reg.IdFor(VoiceKind.Wood, WoodChorus.FirstId - 3));
        Assert.True(reg.TryKeyOf(wood, out var woodKey) && woodKey == new VoiceKey(VoiceKind.Wood, WoodChorus.FirstId - 3, 0));

        int place = reg.IdFor(VoiceKind.Place, 9, 1);
        reg.Declare(place, 9, VoiceKind.Place, default);
        bool fading = true;
        reg.Sweep(1, id => id == place && fading);
        reg.Frame = 2;
        reg.Sweep(2, id => id == place && fading);              // let go by its owner, still fading out
        Assert.True(reg.TryKeyOf(place, out var key) && key == new VoiceKey(VoiceKind.Place, 9, 1));
        reg.Frame = 3;
        Assert.Equal(place, reg.IdFor(VoiceKind.Place, 9, 1)); // taken back while it fades: the same voice
        reg.Frame = 4;
        fading = false;
        reg.Sweep(4, id => id == place && fading);
        Assert.False(reg.TryKeyOf(place, out _));
        reg.Frame = 5;
        Assert.Equal(place, reg.IdFor(VoiceKind.Bird, VoiceRegistry.NoEntity, 0));   // the next voice's

        int probe = reg.ProbeFor(77);
        for (int f = 6; f <= 8; f++) { reg.Frame = f; reg.Sweep(f); Assert.True(reg.TryKeyOf(probe, out _), $"frame {f}"); }
        reg.Frame = 9;
        reg.Sweep(9);
        Assert.False(reg.TryKeyOf(probe, out _));

        // A one-off is a voice of its own, however many of its kind came before it (P3d).
        Assert.NotEqual(reg.IdFor(VoiceKind.RainDrop, VoiceRegistry.NoEntity, 3), reg.IdFor(VoiceKind.RainDrop, VoiceRegistry.NoEntity, 3 + 48));
    }
}
