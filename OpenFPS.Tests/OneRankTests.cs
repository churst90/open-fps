using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// One rank (docs/SPECIAL_CASES_AUDIT.md, P4): whoever chooses what gets a voice asks how loud it is at the ear
/// (VoiceManager.HeardLevel), with the numbers its voice is played with. There were four laws: cars by distance,
/// standing machines by loudness on numbers of their own, their places nearest first, and the budget by the
/// played gain, which carried the gain that gives a quiet render its headroom back.
/// </summary>
public class OneRankTests
{
    private readonly ITestOutputHelper _o;
    public OneRankTests(ITestOutputHelper o) => _o = o;

    private static readonly Vector3 Ear = new(0f, LocalPlayerState.StandingEyeHeight, 0f);

    private static void AddSource(ClientAudioHarness h, int id, string soundId, Vector3 at, Vector3? size = null)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        if (size is { } s) def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = s, IsSolid = false };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = soundId;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 50f;
        h.World.RegisterDefinition(def);
    }

    /// <summary>
    /// A voice that renders with more headroom than the shared one is played that much up to give it back
    /// (PhysicalVoiceState.HeadroomGain), and is no louder for it. The budget ranked on the played gain: a rain
    /// patch or a fire rendering 45 dB under full scale ranked 29 dB over a machine as loud.
    /// </summary>
    [Fact]
    public void A_renders_headroom_is_not_loudness()
    {
        var machine = new SpatialEmitter
        {
            Volume = 0.05f, MinDistance = 2f, Range = 300f, EarLevelDb = 70f, IsSynth = true, PhysicalKey = "machine:ac_window",
        };
        var fire = machine;
        fire.HeadroomGain = PhysicalVoiceState.HeadroomGain(45f);
        fire.Volume = machine.Volume * fire.HeadroomGain;
        _o.WriteLine($"45 dB of headroom is played {20 * MathF.Log10(fire.HeadroomGain):F1} dB up");
        Assert.InRange(20 * MathF.Log10(fire.HeadroomGain), 28.9f, 29.1f);
        foreach (float distance in new[] { 1f, 8f, 60f, 250f })
        {
            float plain = VoiceManager.Audibility(machine, distance, playing: false);
            float made = VoiceManager.Audibility(fire, distance, playing: false);
            Assert.True(plain > 0f, $"nothing at {distance} m");
            Assert.InRange(made / plain, 0.9999f, 1.0001f);
        }
    }

    public static IEnumerable<object[]> Kinds() => ClientAudioSelectionTests.Kinds().Where(k => !((string)k[0]).StartsWith("rail:", StringComparison.Ordinal));

    /// <summary>
    /// Every kind of standing model is ranked for a voice by the number the budget ranks its voice by: the same
    /// gain, reference distance, range, declared level and spectrum key (a shore's and a fire's carry their
    /// geometry), the headroom's return left out of both (S19: the ranking read the spectrum by another key and
    /// the budget counted the headroom). So nothing is picked on one set of numbers and played at another.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_standing_model_is_chosen_and_budgeted_by_one_number(string soundId)
    {
        var h = new ClientAudioHarness();
        h.Facade.Weather.Wind = WindWeather.Steady(6f, 270f, 0f);
        h.StandAt(Vector3.Zero);
        const int Id = 77;
        // Far enough that a tree or a fire is heard from its middle alone (its places share its gain out).
        AddSource(h, Id, soundId, new Vector3(0f, 1f, 120f), new Vector3(4f, 1f, 4f));
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(Id), 60), $"{soundId} was never voiced");
        h.Tick(2);
        var e = h.Mixer.Latest[Id];
        e.Occlusion = 0f;
        float chosenBy = h.Audio.HeardInRanking(Id);
        float budgetedBy = VoiceManager.Audibility(e, Vector3.Distance(e.Position, Ear), playing: false);
        _o.WriteLine($"{soundId} ({e.PhysicalKey}): chosen by {20 * MathF.Log10(chosenBy):F2} dB, budgeted by {20 * MathF.Log10(budgetedBy):F2} dB; "
                   + $"headroom's return {(e.HeadroomGain > 0f ? 20 * MathF.Log10(e.HeadroomGain) : 0f):F1} dB");
        Assert.True(chosenBy > 0f && float.IsFinite(chosenBy), $"{soundId} was not ranked");
        // A source still heard from its places from here (a creek's run is long) has shared its gain out among
        // them, which the one gain that keeps them together as loud as the whole puts back: within that.
        if (e.Spread > 0f) Assert.InRange(budgetedBy / chosenBy, 0.7f, 1.4f);
        else Assert.InRange(budgetedBy / chosenBy, 0.999f, 1.001f);
        // The return of its headroom is in its volume, and said, so that whoever ranks it can take it out.
        float headroom = PhysicalVoiceState.HeadroomGain(ModelKindTable.Headroom(soundId));
        if (headroom > 1.0001f) Assert.InRange(e.HeadroomGain / headroom, 0.9999f, 1.0001f);
    }

    private static void ShedEnginesTo(ClientAudioHarness h, int engines)
        => typeof(ClientAudioSystem).GetField("_adaptiveBudget", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(h.Audio, engines);

    private const string Quiet = "i4_economy", Loud = "v8_muscle";

    /// <summary>
    /// Engines go to the cars loudest at the ear, not the nearest (S12: by distance a 132 dB stock car lost its
    /// engine to a quiet electric car a metre nearer). Two engines to give, two hatchbacks 30 and 36 m off and a
    /// muscle car at 80 m, which is the loudest of the three from here: it and the nearer hatchback have the
    /// engines, and the further hatchback borrows. By distance the two hatchbacks had them.
    /// </summary>
    [Fact]
    public void A_loud_car_further_off_has_the_engine_the_quieter_of_two_nearer_cars_had()
    {
        float quiet = MachineRegistry.VehicleFor(Quiet).SourceLevelDb, loud = MachineRegistry.VehicleFor(Loud).SourceLevelDb;
        _o.WriteLine($"{Quiet} {quiet:F1} dB, {Loud} {loud:F1} dB at a metre; 80 m against 30 m is {20 * MathF.Log10(80f / 30f):F1} dB");
        Assert.True(loud - quiet > 12f, "the muscle car is not the louder by enough for this test");
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        ShedEnginesTo(h, 2);
        h.AddCar(1, Quiet, new Vector3(0f, 0f, 30f));
        h.AddCar(2, Quiet, new Vector3(0f, 0f, 36f));
        h.AddCar(3, Loud, new Vector3(0f, 0f, 80f));
        h.Tick(12);
        _o.WriteLine($"ranked at {string.Join(", ", new[] { 1, 2, 3 }.Select(id => $"{id}: {20 * MathF.Log10(h.Audio.HeardInRanking(id)):F1} dB"))}");
        Assert.True(h.Audio.HeardInRanking(3) > h.Audio.HeardInRanking(1) && h.Audio.HeardInRanking(1) > h.Audio.HeardInRanking(2));
        Assert.Contains(3, h.Mixer.Live);
        Assert.Contains(1, h.Mixer.Live);
        Assert.DoesNotContain(2, h.Mixer.Live);
        Assert.Contains(h.DistantVoice(2), h.Mixer.Live);
    }

    /// <summary>
    /// A car is ranked for an engine by the number the budget ranks its engine's voice by: the same gain,
    /// reference distance, range, declared level and spectrum. From 60 m, where it is one source (nearer, its
    /// exhaust and its front are placed apart).
    /// </summary>
    [Theory]
    [InlineData(Quiet)]
    [InlineData(Loud)]
    [InlineData("diesel_truck")]
    [InlineData("vtwin")]
    public void A_car_is_chosen_and_budgeted_by_one_number(string preset)
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        h.AddCar(5, preset, new Vector3(0f, 0f, 60f));
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(5), 30), $"{preset} was never voiced");
        h.Tick(2);
        var e = h.Mixer.Latest[5];
        e.Occlusion = 0f;
        float chosenBy = h.Audio.HeardInRanking(5);
        float budgetedBy = VoiceManager.Audibility(e, Vector3.Distance(e.Position, Ear), playing: false);
        _o.WriteLine($"{preset}: chosen by {20 * MathF.Log10(chosenBy):F2} dB, budgeted by {20 * MathF.Log10(budgetedBy):F2} dB");
        Assert.True(chosenBy > 0f && float.IsFinite(chosenBy), $"{preset} was not ranked");
        Assert.InRange(budgetedBy / chosenBy, 0.999f, 1.001f);
    }

    /// <summary>
    /// The keep bias and the hold are as they were, in decibels now: a car with an engine keeps it against one
    /// up to 2.5 dB louder (as if at three quarters of its distance), and an engine just started is not dropped
    /// for 2.5 s whatever is louder.
    /// </summary>
    [Fact]
    public void A_car_with_an_engine_keeps_it_against_one_a_little_louder()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        ShedEnginesTo(h, 1);
        h.AddCar(1, Loud, new Vector3(0f, 0f, 60f));
        h.Tick(4);
        Assert.Contains(1, h.Mixer.Live);
        // Past its hold. Then a second car of its kind 1.9 dB louder (at 0.8 of its distance): the first keeps it.
        h.Wait(3.0);
        h.AddCar(2, Loud, new Vector3(0f, 0f, 48f));
        h.Tick(6);
        _o.WriteLine($"the newcomer is {20 * MathF.Log10(h.Audio.HeardInRanking(2) / h.Audio.HeardInRanking(1)):F1} dB louder");
        Assert.Contains(1, h.Mixer.Live);
        Assert.DoesNotContain(2, h.Mixer.Live);
        // A third, 6 dB louder (at half the distance), takes it.
        h.AddCar(3, Loud, new Vector3(0f, 0f, 30f));
        h.Tick(6);
        Assert.Contains(3, h.Mixer.Live);
        // And is held: a fourth, louder still, does not have it within the hold.
        h.AddCar(4, Loud, new Vector3(0f, 0f, 12f));
        h.Tick(6);
        Assert.Contains(3, h.Mixer.Live);
        Assert.DoesNotContain(4, h.Mixer.Live);
        h.Wait(3.0);
        h.Tick(6);
        Assert.Contains(4, h.Mixer.Live);
    }

    /// <summary>
    /// A car out of the budget borrows the ring of the first car of its kind with an engine. While engines were
    /// held (2.5 s from their start) the held cars were in the order of their ids, so with two of a kind held
    /// the first was whichever had the lower id, and when the hold ended the louder: every borrowed voice of
    /// the kind was stopped and started on another ring twice. The held are loudest first among themselves.
    /// </summary>
    [Fact]
    public void Borrowed_voices_stay_on_the_loudest_car_of_their_kind_while_engines_are_held()
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        ShedEnginesTo(h, 2);
        h.AddCar(9, Loud, new Vector3(0f, 0f, 10f));
        h.AddCar(4, Loud, new Vector3(0f, 0f, 50f));
        h.AddCar(20, Loud, new Vector3(0f, 0f, 90f));
        h.AddCar(21, Loud, new Vector3(0f, 0f, 100f));
        int[] borrowed = { h.DistantVoice(20), h.DistantVoice(21) };
        Assert.True(h.TickUntil(() => borrowed.All(h.Mixer.Live.Contains), 20), "the far cars never borrowed");
        void OnTheNearCarsRing(string when)
        {
            foreach (int voice in borrowed)
            {
                Assert.True(h.Mixer.Latest[voice].EchoOfEntity == 9, $"{when}: a borrowed voice reads car {h.Mixer.Latest[voice].EchoOfEntity}'s ring");
                Assert.DoesNotContain(voice, h.Mixer.Stopped);
            }
        }
        for (int f = 0; f < 10; f++) { h.Tick(); OnTheNearCarsRing($"held, frame {f}"); }
        h.Wait(3.0);
        for (int f = 0; f < 10; f++) { h.Tick(); OnTheNearCarsRing($"after the hold, frame {f}"); }
        Assert.Contains(9, h.Mixer.Live);
        Assert.Contains(4, h.Mixer.Live);
    }
}
