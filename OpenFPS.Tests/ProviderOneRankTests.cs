using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Engine;
using OpenFPS.Common;
using ActiveSound = OpenFPS.Audio.Fmod.FmodAudioProvider.ActiveSound;

namespace OpenFPS.Tests;

/// <summary>
/// The provider's own orderings read the one rank (docs/SPECIAL_CASES_AUDIT.md, S20): the render pool's order, which
/// engines run at full detail, who the late field traces and who gets the traced echoes, each by how loud a voice is
/// at the ear (VoiceManager.HeardLevel), as the budget ranks it. They ranked nearest first or on the played gain,
/// which carries the gain that only gives a quiet render its headroom back: a garden fire pit or a rain patch,
/// rendered 45 dB under full scale, ranked 29 dB over what it is.
/// </summary>
public class ProviderOneRankTests
{
    /// <summary>The law as a world with the shipped hearing places by.</summary>
    private static readonly OpenFPS.Sound.Hearing.HearingSettings Law = new();

    private const int Rate = 48000;
    private static readonly Vector3 Ear = Vector3.Zero;
    private static readonly float FireHeadroom = PhysicalVoiceState.HeadroomGain(45f);

    /// <summary>A voice that renders nothing: the orderings read only what it is.</summary>
    private sealed class Still : PhysicalVoiceState
    {
        public Still() : base(70f, Rate) { }
        protected override float StepSynth() => 0f;
        protected override void Control(float seconds, float dt) { }
        protected override void PushListener(Vector3 frame) { }
    }

    /// <summary>A voice at <paramref name="at"/> heard at <paramref name="volume"/> at a metre; with a render's headroom,
    /// played that much up, as the fires and the rain are.</summary>
    private static ActiveSound Voice(int id, Vector3 at, float volume, float headroom = 0f, PhysicalVoiceState? machine = null,
                                     EngineVoiceState? engine = null)
    {
        var a = new ActiveSound
        {
            EntityId = id, SoundId = "test", Position = at, CurrentApparentPosition = at, MinDistance = 1f, Range = 1000f,
            BaseVolume = headroom > 0f ? volume * headroom : volume,
            MachineState = engine == null ? machine ?? new Still() : null, EngineState = engine,
            LastVolume = 1f,
        };
        a.RankAs(new SpatialEmitter { HeadroomGain = headroom, IsSynth = true, PhysicalKey = "test" });
        return a;
    }

    private static Vector3 At(float metres) => new(0f, 0f, metres);

    /// <summary>
    /// The provider ranks a voice by the number the budget ranks it by (VoiceManager.Audibility), its render's
    /// headroom left out of both: a machine, a fire pit with 45 dB of headroom and a car, each with its declared
    /// level and its spectrum's key, at four distances.
    /// </summary>
    [Fact]
    public void The_provider_ranks_a_voice_by_the_number_the_budget_ranks_it_by()
    {
        var machine = new SpatialEmitter { Volume = 0.05f, MinDistance = 2f, Range = 300f, EarLevelDb = 70f, IsSynth = true, PhysicalKey = "machine:ac_window" };
        var fire = new SpatialEmitter
        {
            Volume = 0.004f * FireHeadroom, HeadroomGain = FireHeadroom, MinDistance = 1f, Range = 120f, EarLevelDb = 67f,
            IsSynth = true, PhysicalKey = "fire:fire_pit",
        };
        var car = new SpatialEmitter { Volume = 0.3f, MinDistance = 3f, Range = 400f, EarLevelDb = 80f, IsSynth = true, SoundId = "engine:i4_midsize", EngineKey = "i4_midsize" };
        foreach (var e in new[] { machine, fire, car })
            foreach (float d in new[] { 1f, 8f, 60f, 100f })
            {
                var a = new ActiveSound { Position = At(d), MinDistance = e.MinDistance, Range = e.Range, BaseVolume = e.Volume };
                a.RankAs(e);
                float provider = FmodAudioProvider.HeardHere(Law, a, Ear);
                float budget = VoiceManager.Audibility(Law, e, d, playing: false);
                Assert.True(budget > 0f, $"{e.PhysicalKey}{e.SoundId}: nothing at {d} m");
                Assert.InRange(provider / budget, 0.9999f, 1.0001f);
            }
    }

    /// <summary>
    /// The render pool renders the loudest first, so when it cannot keep every ring ahead the shortfall lands on the
    /// quietest voice. Nearest first, the fire pit's places beside you came before a car 8 m off and a bus 60 m off,
    /// both louder; on the played gain they still came first. Two as loud are in id order whatever order they were
    /// listed in.
    /// </summary>
    [Fact]
    public void The_render_pool_renders_the_loudest_first_not_the_nearest()
    {
        var car = Voice(20, At(8f), 0.05f);
        var bus = Voice(30, At(60f), 1f);
        var twinA = Voice(41, At(15f), 0.02f);
        var twinB = Voice(40, At(15f), 0.02f);
        var places = Enumerable.Range(1, 4).Select(i => Voice(i, At(3f), 0.002f, FireHeadroom)).ToList();
        foreach (var listed in new[] { places.Concat(new[] { car, twinA, bus, twinB }).ToList(), new[] { twinB, bus, twinA, car }.Concat(places).Reverse().ToList() })
        {
            FmodAudioProvider.RankVoices(Law, listed, Ear);
            var order = new List<FmodAudioProvider.RenderSlot>();
            FmodAudioProvider.RenderAhead(listed, order, new Dictionary<TrainVoiceState, (float, int)>());
            Assert.Equal(new[] { 30, 20, 40, 41, 1, 2, 3, 4 }, order.Select(o => o.Id));
            Assert.Equal(listed.Count, order.Count);
        }
    }

    /// <summary>A train's lanes render what its voices read: listed once, just ahead of its loudest voice.</summary>
    [Fact]
    public void A_trains_lanes_go_just_ahead_of_its_loudest_voice()
    {
        var t = new TrainVoiceState("light_rail/test", TrainProfile.ByName("light_rail"), Rate, 7) { Offline = true };
        float level = OpenFPS.Sound.Core.Rail.TrainVoicing.SlotLevelDb(t.Layout);
        var near = Voice(50, At(12f), 0.01f, machine: new TrainSlotState(t, 0, level, Rate, startSample: 0));
        var loud = Voice(51, At(40f), 0.5f, machine: new TrainSlotState(t, 1, level, Rate, startSample: 0));
        var car = Voice(20, At(20f), 0.05f);
        var listed = new List<ActiveSound> { near, car, loud };
        FmodAudioProvider.RankVoices(Law, listed, Ear);
        var order = new List<FmodAudioProvider.RenderSlot>();
        FmodAudioProvider.RenderAhead(listed, order, new Dictionary<TrainVoiceState, (float, int)>());
        Assert.True(t.Lanes.Length > 0);
        Assert.Equal(t.Lanes.Length + 3, order.Count);
        for (int l = 0; l < t.Lanes.Length; l++) Assert.Same(t.Lanes[l], order[l].Voice);
        Assert.Same(loud.MachineState, order[t.Lanes.Length].Voice);
        Assert.Equal(new[] { 20, 50 }, order.Skip(t.Lanes.Length + 1).Select(o => o.Id));
    }

    /// <summary>
    /// A garden fire pit 3 m off, as loud at the ear as a car 8 m off, leaves the car at full detail; a car 20 dB
    /// under both goes to reduced detail. On the played gain the fire pit was the loudest machine by 29 dB and
    /// every car on the street went to reduced detail beside it, the near one too.
    /// </summary>
    [Fact]
    public void A_fire_beside_you_leaves_the_cars_at_full_detail()
    {
        static EngineVoiceState Engine() => new(MachineRegistry.VehicleFor("i4_midsize"), Rate, 1001);
        var near = Voice(20, At(8f), 0.08f, engine: Engine());
        var far = Voice(21, At(80f), 0.08f, engine: Engine());
        var fire = Voice(1, At(3f), 0.03f, FireHeadroom);
        var listed = new List<ActiveSound> { fire, near, far };
        FmodAudioProvider.RankVoices(Law, listed, Ear);
        FmodAudioProvider.ChooseEngineDetail(listed, Ear, reduceFar: true);
        Assert.Equal(EngineDetail.Full, near.EngineState!.Detail);
        Assert.Equal(EngineDetail.Reduced, far.EngineState!.Detail);
    }

    /// <summary>
    /// The late field traces the sixteen loudest sources of the listener's place: eight cars before sixteen rain
    /// patches round the ear that are each quieter than any of them. On the played gain the patches took fifteen of
    /// the sixteen slots.
    /// </summary>
    [Fact]
    public void The_late_field_traces_the_loudest_not_the_rain_round_you()
    {
        var sending = new FMOD.DSPConnection(new IntPtr(1));
        var listed = new List<ActiveSound>();
        for (int i = 0; i < 16; i++) listed.Add(Voice(-100 - i, At(1f + i * 0.25f), 0.001f, FireHeadroom));
        for (int i = 0; i < 8; i++) listed.Add(Voice(1000 + i, At(8f + i * 12f), 0.5f));
        foreach (var a in listed) { a.TargetRegionId = 7; a.SourceReverbConnection = sending; }
        // Another place's source and a cabin path are not the listener's place's.
        var elsewhere = Voice(2000, At(5f), 1f);
        elsewhere.SourceReverbConnection = sending;
        elsewhere.TargetRegionId = 8;
        listed.Add(elsewhere);
        FmodAudioProvider.RankVoices(Law, listed, Ear);
        var chosen = new List<ActiveSound>();
        FmodAudioProvider.LateCandidates(listed, 7, chosen);
        Assert.Equal(24, chosen.Count);
        Assert.Equal(Enumerable.Range(1000, 8), chosen.Take(8).Select(a => a.EntityId));
        for (int i = 1; i < chosen.Count; i++) Assert.True(chosen[i - 1].Heard >= chosen[i].Heard);
    }

    /// <summary>
    /// The traced echoes go to the loudest far sources: a fire pit 35 m off, quieter at the ear than four cars
    /// beyond it, gets none of their four rigs. On the played gain it took one.
    /// </summary>
    [Fact]
    public void The_traced_echoes_go_to_the_loudest_far_sources()
    {
        var binaural = new FMOD.DSP(new IntPtr(1));
        var listed = new List<ActiveSound> { Voice(1, At(35f), 0.02f, FireHeadroom) };
        for (int i = 0; i < 4; i++) listed.Add(Voice(1000 + i, At(45f + i * 20f), 0.5f));
        // Too near to be traced (the comb of a second set of early reflections close to).
        listed.Add(Voice(999, At(10f), 1f));
        foreach (var a in listed) a.SaDsp = binaural;
        FmodAudioProvider.RankVoices(Law, listed, Ear);
        var keep = new HashSet<ActiveSound>();
        FmodAudioProvider.ChooseEchoSources(listed, Ear, 4, new List<(ActiveSound, float)>(), keep);
        Assert.Equal(new[] { 1000, 1001, 1002, 1003 }, keep.Select(a => a.EntityId).Order());
    }

    /// <summary>
    /// The late field's lean weighs each source by its level at the ear: a fire pit's place with 45 dB of headroom
    /// pulls the tail as hard as a machine heard as loud, not 29 dB harder.
    /// </summary>
    [Fact]
    public void The_tail_leans_toward_a_source_by_how_loud_it_is_heard()
    {
        var machine = Voice(1, At(10f), 0.05f);
        var fire = Voice(2, At(10f), 0.05f, FireHeadroom);
        FmodAudioProvider.RankVoices(Law, new List<ActiveSound> { machine, fire }, Ear);
        Assert.True(FmodAudioProvider.LeanWeight(machine) > 0f);
        Assert.InRange(FmodAudioProvider.LeanWeight(fire) / FmodAudioProvider.LeanWeight(machine), 0.999f, 1.001f);
    }
}
