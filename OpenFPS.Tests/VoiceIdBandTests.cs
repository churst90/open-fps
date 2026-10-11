using System.Reflection;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Every voice-id band the client computes (docs/SPECIAL_CASES_AUDIT.md, appendix), read from the seed
/// table the ids are still made by (VoiceRegistry.IdFor, SeedOf), and checked against every other. Nothing allocates voice ids:
/// each band is a formula of an entity id or a counter, set by hand to sit apart (finding S6), so two
/// kinds of voice that land on one id move or cut each other without a word in the log.
///
/// Entity ids are checked to <see cref="HighestEntityId"/>. The maps reach 18,024 on Albany's join and
/// 32,598 over all of Magnolia; the bands must hold past the largest map there is, not only today's.
/// Counters that are never reused (engine echoes, train ordinals) grow without bound, so they are
/// checked unbounded and each overlap says after how many.
/// </summary>
public class VoiceIdBandTests
{
    private readonly ITestOutputHelper _o;
    public VoiceIdBandTests(ITestOutputHelper o) => _o = o;

    /// <summary>The entity id the bands are checked to: past every overlap S6 names, the last of them the
    /// talkers' band running into the cabin paths' at 200,001.</summary>
    internal const int HighestEntityId = 250_000;

    private enum Scale { Fixed, Entity, Counter }

    /// <summary>A band: its ids, as closed intervals, for entity ids up to e and n counted.</summary>
    private sealed record Band(string Name, Scale Scale, Func<long, long, IReadOnlyList<(long Lo, long Hi)>> At);

    private static int Const(Type t, string name)
        => (int)(t.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 ?? throw new MissingFieldException(t.Name, name)).GetRawConstantValue()!;

    private static IReadOnlyList<(long, long)> One(long a, long b) => new[] { (Math.Min(a, b), Math.Max(a, b)) };
    private static readonly IReadOnlyList<(long, long)> None = Array.Empty<(long, long)>();

    /// <summary>The ids of the first <paramref name="parts"/> of a kind of voice, as one closed interval.</summary>
    private static (long, long) Span(Func<int, int> idOfPart, int parts)
    {
        long lo = long.MaxValue, hi = long.MinValue;
        for (int k = 0; k < parts; k++) { int id = idOfPart(k); lo = Math.Min(lo, id); hi = Math.Max(hi, id); }
        return (lo, hi);
    }

    private static int Seed(VoiceKind kind, int owner, int part = 0) => VoiceRegistry.SeedOf(kind, owner, part);

    /// <summary>Every band in the appendix but the source probes, which are questions to the acoustic
    /// worker and never a voice: the seed table (VoiceRegistry.SeedOf), the ids voices had.</summary>
    private static List<Band> Bands()
    {
        const int Any = VoiceRegistry.NoEntity, Counted = 400_000;
        var cas = typeof(ClientAudioSystem);
        int arrivals = EarlyReflections.MaxArrivals;
        int places = ExtendedSources.MaxPlaces;
        int cabinPaths = MachineRegistry.Ids.Max(v => CabinPaths.For(MachineRegistry.VehicleFor(v))?.Count ?? 0);
        int ownCopies = Const(cas, "OwnVoiceCopies");
        int talkerRooms = Const(cas, "MaxTalkerRooms");

        var aidIds = new List<(long, long)>();
        foreach (var aid in Enum.GetValues<AidVoice>()) aidIds.Add(Span(k => Seed(VoiceKind.Aid, (int)aid, k), 1000));

        int woodTop = WoodChorus.FirstId, woodBottom = WoodChorus.FirstId - 999_999;   // a million woods counted down (ClientWorldState.RefreshWoods)
        int trainSlots = TrainVoicing.Slots;
        long echo0 = Seed(VoiceKind.EngineEcho, Any, 0), train0 = Seed(VoiceKind.TrainField, 0, 0);
        Assert.Equal(echo0 - 1, Seed(VoiceKind.EngineEcho, Any, 1));
        Assert.Equal(train0 - trainSlots, Seed(VoiceKind.TrainField, 1, 0));

        return new List<Band>
        {
            new("entity voices", Scale.Entity, (e, _) => One(1, e)),
            new("woods", Scale.Fixed, (_, _) => One(woodBottom, woodTop)),
            new("reflection copies", Scale.Entity, (e, _) => One(Seed(VoiceKind.Reflection, (int)e, arrivals - 1), Seed(VoiceKind.Reflection, 1, 0))),
            new("transients", Scale.Fixed, (_, _) => new[] { Span(k => Seed(VoiceKind.Transient, Any, k), Counted) }),
            // Counted past the ints, as a counter never reused is: one id down each.
            new("engine echoes", Scale.Counter, (_, n) => n < 1 ? None : One(echo0 - (n - 1), echo0)),
            new("borrowed far cars", Scale.Entity, (e, _) => One(Seed(VoiceKind.Borrowed, (int)e), Seed(VoiceKind.Borrowed, 1))),
            new("front outlets", Scale.Entity, (e, _) => One(Seed(VoiceKind.Intake, (int)e), Seed(VoiceKind.Intake, 1))),
            new("sirens", Scale.Entity, (e, _) => One(Seed(VoiceKind.Siren, (int)e), Seed(VoiceKind.Siren, 1))),
            new("driving aids", Scale.Fixed, (_, _) => aidIds),
            new("beacons", Scale.Fixed, (_, _) => new[] { Span(k => Seed(VoiceKind.Beacon, Any, k), 1000) }),
            new("birds", Scale.Fixed, (_, _) => new[] { Span(k => Seed(VoiceKind.Bird, Any, k), 1000) }),
            new("horns", Scale.Entity, (e, _) => One(Seed(VoiceKind.Horn, (int)e), Seed(VoiceKind.Horn, 1))),
            new("own voice", Scale.Fixed, (_, _) => One(Seed(VoiceKind.OwnVoiceCopy, Any, ownCopies - 1), Seed(VoiceKind.OwnVoice, Any))),
            new("talkers", Scale.Entity, (e, _) => One(Seed(VoiceKind.Talker, (int)e), Seed(VoiceKind.Talker, 1))),
            new("cabin paths", Scale.Fixed, (_, _) => One(Seed(VoiceKind.Cabin, Any, cabinPaths - 1), Seed(VoiceKind.Cabin, Any, 1))),
            new("talker copies", Scale.Fixed, (_, _) => One(Seed(VoiceKind.TalkerCopy, talkerRooms - 1, ownCopies - 1), Seed(VoiceKind.TalkerCopy, 0, 0))),
            new("step echoes", Scale.Fixed, (_, _) => new[] { Span(k => Seed(VoiceKind.StepEcho, Any, k), 1000) }),
            new("held horns", Scale.Entity, (e, _) => One(Seed(VoiceKind.HeldHorn, (int)e), Seed(VoiceKind.HeldHorn, 1))),
            new("trains", Scale.Counter, (_, n) => n < 1 ? None : One(train0 - (n * trainSlots - 1), train0)),
            new("rain", Scale.Fixed, (_, _) => new[]
            {
                Span(s => Seed(VoiceKind.Rain, s, 0), RainFeeds.Slots),
                (Seed(VoiceKind.Rain, RainFeeds.Slots - 1, 7), Seed(VoiceKind.Rain, 0, 1)),
                Span(k => Seed(VoiceKind.RainDrop, Any, k), 1000),
            }),
            new("places", Scale.Entity, (e, _) => One(Seed(VoiceKind.Place, (int)e, places - 1), Seed(VoiceKind.Place, 1, 1))),
            new("wood places", Scale.Fixed, (_, _) => One(Seed(VoiceKind.Place, woodBottom, places - 1), Seed(VoiceKind.Place, woodTop, 1))),
            new("reference", Scale.Fixed, (_, _) => One(Seed(VoiceKind.Reference, Any, 1), Seed(VoiceKind.Reference, Any, 0))),
        };
    }

    private static bool Meet(IReadOnlyList<(long Lo, long Hi)> a, IReadOnlyList<(long Lo, long Hi)> b)
    {
        foreach (var x in a)
            foreach (var y in b)
                if (x.Lo <= y.Hi && y.Lo <= x.Hi) return true;
        return false;
    }

    /// <summary>Each pair of bands that share an id, and from what scale: the first count of a counter
    /// that is never reused (with the entity bands at their least), or else the first entity id.</summary>
    private static List<string> Overlaps()
    {
        var bands = Bands();
        const long Unbounded = int.MaxValue;
        var found = new List<string>();
        for (int i = 0; i < bands.Count; i++)
            for (int j = i + 1; j < bands.Count; j++)
            {
                var (a, b) = (bands[i], bands[j]);
                bool counter = a.Scale == Scale.Counter || b.Scale == Scale.Counter;
                bool entity = a.Scale == Scale.Entity || b.Scale == Scale.Entity;
                if (!Meet(a.At(HighestEntityId, Unbounded), b.At(HighestEntityId, Unbounded))) continue;
                string from;
                if (counter && Meet(a.At(1, Unbounded), b.At(1, Unbounded)))
                    from = "after " + First(n => Meet(a.At(1, n), b.At(1, n)), Unbounded) + " counted";
                else if (entity)
                    from = "from entity id " + First(e => Meet(a.At(e, Unbounded), b.At(e, Unbounded)), HighestEntityId)
                         + (counter ? ", once enough are counted" : "");
                else from = "always";
                found.Add($"{a.Name} / {b.Name}: {from}");
            }
        return found;
    }

    /// <summary>The least s in [1, top] at which a test that stays true once true holds.</summary>
    private static long First(Func<long, bool> holds, long top)
    {
        long lo = 1, hi = top;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            if (holds(mid)) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// What P3 makes pass: one allocator for every voice id (docs/SPECIAL_CASES_AUDIT.md, P3c), after
    /// which this becomes "no two live voices share an id".
    /// </summary>
    [Fact(Skip = "Known failure, finding S6 (docs/SPECIAL_CASES_AUDIT.md): the voice-id bands overlap. "
               + "TheVoiceIdBandsOverlapWhereS6Says holds today's overlaps exactly; part P3 makes this pass.")]
    public void NoTwoVoiceIdBandsShareAnId()
    {
        var overlaps = Overlaps();
        foreach (var o in overlaps) _o.WriteLine(o);
        Assert.Empty(overlaps);
    }

    /// <summary>
    /// Today's overlaps, every one, so a part that adds a band or moves one is told. P3 changes this
    /// list in the commit that changes the bands, and empties it when ids are allocated.
    /// </summary>
    [Fact]
    public void TheVoiceIdBandsOverlapWhereS6Says()
    {
        foreach (var b in Bands())
            _o.WriteLine($"{b.Name,-18} {string.Join(", ", b.At(HighestEntityId, 0).Select(r => $"{r.Lo}..{r.Hi}"))}");
        var overlaps = Overlaps();
        _o.WriteLine("");
        foreach (var o in overlaps) _o.WriteLine(o);
        Assert.Equal(TodaysOverlaps, overlaps);
    }

    /// <summary>
    /// The overlaps at 22423a5b. The ones S6 ranks: reflection copies into the transients from entity id
    /// 14,000 (Albany's join is 18,024, Magnolia's whole map 32,598); sirens onto the driving aids, beacons
    /// and birds from 61,000 to 70,047; engine echoes and train voices, never reused, into every band below
    /// them (the borrowed band after 100,002 echoes, about ten hours on the city; rain after 16,667 trains).
    /// </summary>
    private static readonly string[] TodaysOverlaps =
    {
        "woods / engine echoes: after 19400001 counted",
        "woods / trains: after 2950001 counted",
        "reflection copies / transients: from entity id 14000",
        "reflection copies / engine echoes: from entity id 114000, once enough are counted",
        "reflection copies / borrowed far cars: from entity id 134000",
        "reflection copies / front outlets: from entity id 154000",
        "reflection copies / sirens: from entity id 174000",
        "reflection copies / driving aids: from entity id 186200",
        "reflection copies / beacons: from entity id 187400",
        "reflection copies / birds: from entity id 188000",
        "reflection copies / horns: from entity id 234000",
        "engine echoes / borrowed far cars: after 100002 counted",
        "engine echoes / front outlets: after 200002 counted",
        "engine echoes / sirens: after 300002 counted",
        "engine echoes / driving aids: after 361001 counted",
        "engine echoes / beacons: after 367001 counted",
        "engine echoes / birds: after 370001 counted",
        "engine echoes / horns: after 600002 counted",
        "engine echoes / own voice: after 700001 counted",
        "engine echoes / talkers: after 800002 counted",
        "engine echoes / cabin paths: after 1000002 counted",
        "engine echoes / talker copies: after 1100001 counted",
        "engine echoes / step echoes: after 1200001 counted",
        "engine echoes / held horns: after 1500002 counted",
        "engine echoes / trains: after 1700001 counted",
        "engine echoes / rain: after 1800001 counted",
        "engine echoes / places: after 4400014 counted",
        "engine echoes / wood places: after 244400002 counted",
        "engine echoes / reference: after 7300001 counted",
        "borrowed far cars / front outlets: from entity id 100001",
        "borrowed far cars / sirens: from entity id 200001",
        "front outlets / sirens: from entity id 100001",
        "front outlets / driving aids: from entity id 161000",
        "front outlets / beacons: from entity id 167000",
        "front outlets / birds: from entity id 170000",
        "sirens / driving aids: from entity id 61000",
        "sirens / beacons: from entity id 67000",
        "sirens / birds: from entity id 70000",
        "horns / own voice: from entity id 100000",
        "horns / talkers: from entity id 200001",
        "talkers / cabin paths: from entity id 200001",
        "held horns / trains: from entity id 200000, once enough are counted",
        "trains / rain: after 16667 counted",
        "trains / places: after 450003 counted",
        "trains / wood places: after 40450001 counted",
        "trains / reference: after 933334 counted",
        "places / reference: from entity id 241666",
    };
}
