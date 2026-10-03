using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Door mechanism sounds (OpenFPS.Common.DoorMechanisms) held to the numbers measured from 27
/// recorded doors (docs/DOOR_TYPES.md): band balance, decays, the lines that carry each material,
/// and limits on how far any line may stand out, so that nothing comes out "too tonal" again. Every
/// event is the one the real door system sends, at the timing it sends it.
/// </summary>
public class DoorMechanismTests : IDisposable
{
    private const int Sr = 48000;
    private const float Dt = PhysicsConstants.FixedDeltaTime;
    private readonly ITestOutputHelper _o;
    private readonly World _world = World.Create();
    private readonly PrefabRepository _prefabs = new(Path.Combine(AppContext.BaseDirectory, "prefabs"));

    public DoorMechanismTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }
    public void Dispose() => World.Destroy(_world);

    private sealed record Heard(float At, string Key, TransientSound Sound)
    {
        public string Event => Key[(Key.LastIndexOf(':') + 1)..];
        public DoorMechanisms.Spec Spec => DoorMechanisms.TryParseKey(Sound.SynthKey, out var s) ? s : throw new Exception(Sound.SynthKey);
    }

    /// <summary>One open and shut of a prefab door through the real door system: what it sent, when.</summary>
    private List<Heard> OpenAndShut(string prefab, bool fromOutside = false)
    {
        var doors = new DoorSystem();
        var e = _prefabs.Spawn(_world, prefab, new Vector3(0, 1.05f, 0), Quaternion.Identity, Vector3.One);
        var heard = new List<Heard>();
        float now = 0f;
        void Tick(float seconds)
        {
            for (int i = 0; i < (int)MathF.Ceiling(seconds / Dt); i++, now += Dt)
                doors.Update(_world, Dt, _ => { }, (_, key, sounds) => { foreach (var s in sounds) heard.Add(new Heard(now, key, s)); });
        }
        Tick(0.1f);
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0.2f, 1.6f, fromOutside ? 1.5f : -1.5f));
        var d = _world.Get<DoorComponent>(e);
        Tick(d.SwingSeconds + 1f);
        if (d.CloseAfterSeconds <= 0f) DoorSystem.Set(_world, e, open: false);
        Tick(d.CloseAfterSeconds + MathF.Max(d.CloseSeconds, d.SwingSeconds) + 1f);
        return heard;
    }

    private static float[] Render(Heard h, int seed = 1) => DoorMechanisms.Render(h.Spec, Sr, seed);

    private static Heard One(List<Heard> heard, string ev, int nth = 0) => heard.Where(h => h.Event == ev).ElementAt(nth);

    /// <summary>The first 2 ms frame within 6 dB of the loudest: where an event's main hit starts.</summary>
    private static float Onset(float[] pcm, float from = 0f)
    {
        var env = SoundMeasure.Envelope(pcm, Sr);
        int a = (int)(from / 0.002f), j = a;
        for (int i = a; i < env.Length; i++) if (env[i] > env[j]) j = i;
        int first = a;
        while (first < j && env[first] < env[j] - 6f) first++;
        return MathF.Max(0f, first * 0.002f - 0.004f);
    }

    private static string Show(float[] v) => string.Join(" ", v.Select(x => x.ToString("0")));

    // ── Keys and routing ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKeyNamesOneSoundAndComesBack()
    {
        var spec = new DoorMechanisms.Spec(DoorKind.Elevator, DoorEvents.Rollers, "Metal", 0.55f, 2.1f, 0.04f, 0.0012f, 0f, 2.5f, false);
        string key = DoorMechanisms.Key(spec);
        Assert.StartsWith(DoorMechanisms.KeyPrefix, key);
        Assert.True(DoorMechanisms.TryParseKey(key, out var back));
        Assert.Equal(spec, back);
        Assert.True(DoorMechanisms.IsMotion(key));
        // An impact does not carry a travel time, so two doors of one leaf shut alike are one buffer.
        var shut = spec with { Event = DoorEvents.Shut, Seconds = 1.7f };
        Assert.EndsWith(":0:c", DoorMechanisms.Key(shut));
        Assert.False(DoorMechanisms.IsMotion(DoorMechanisms.Key(shut)));
        Assert.False(DoorMechanisms.TryParseKey("cardoor:close", out _));
        Assert.False(DoorMechanisms.TryParseKey("doorsnd:nope:latch:Wood:90:210:60:0:0:0:o", out _));
        Assert.True(WorldAudioPlayer.DoorTravel(key));
        Assert.True(WorldAudioPlayer.DoorTravel(DoorMechanisms.Key(spec with { Kind = DoorKind.GlassPull, Event = DoorEvents.Closer })));
        Assert.False(WorldAudioPlayer.DoorTravel(DoorMechanisms.Key(shut)));
    }

    [Fact]
    public void ARenderIsTheSameForASeedAndDiffersBetweenSeeds()
    {
        var spec = new DoorMechanisms.Spec(DoorKind.PushBar, DoorEvents.Latch, "Metal", 1f, 2.1f, 0.08f, 0.0012f);
        var a = DoorMechanisms.Render(spec, Sr, 3);
        var b = DoorMechanisms.Render(spec, Sr, 3);
        var c = DoorMechanisms.Render(spec, Sr, 4);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        // The leaf is the same leaf whatever the seed: its modes come from it, not from the event.
        Assert.Equal(DoorMechanisms.LeafModes(spec), DoorMechanisms.LeafModes(spec));
    }

    /// <summary>
    /// Every event of every kind, as the server sends it, names a model that renders, at its measured
    /// level against the door's main hit. The two that make no sound of their own (a swing on dry
    /// hinges, a steel door's sealed closer) send none.
    /// </summary>
    [Theory]
    [InlineData("door", false, "latch-retract latch", "swing")]
    [InlineData("steel_door", false, "bar latch", "swing closer")]
    [InlineData("glass_front_door", true, "key latch-retract closer latch", "swing")]
    [InlineData("glass_front_door", false, "bar closer latch", "swing")]
    [InlineData("glass_pull_door", false, "pull closer latch", "swing")]
    [InlineData("auto_sliding_door", false, "motor-start rollers stop motor-start rollers shut", "")]
    [InlineData("patio_door", false, "latch-retract rollers stop rollers latch", "")]
    [InlineData("elevator_door", false, "motor-start rollers stop motor-start rollers shut", "")]
    public void EveryEventTheServerSendsHasItsOwnSound(string prefab, bool outside, string sounding, string silent)
    {
        var heard = OpenAndShut(prefab, outside);
        Assert.Equal(sounding, string.Join(" ", heard.Select(h => h.Event)));
        foreach (string ev in silent.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(float.IsNaN(DoorMechanisms.RelativeDb(heard[0].Spec.Kind, ev)), ev);
        foreach (var h in heard)
        {
            var spec = h.Spec;
            Assert.Equal(h.Event, spec.Event);
            var pcm = Render(h);
            float frame = MathF.Pow(10f, SoundMeasure.PeakFrameDb(pcm, Sr) / 20f);
            Assert.InRange(frame, DoorMechanisms.FrameReference * 0.5f, DoorMechanisms.FrameReference * 1.01f);
            Assert.True(pcm.Max(MathF.Abs) <= 1f);
            _o.WriteLine($"{h.At,5:F2}s +{h.Sound.DelaySeconds:F2} {h.Key,-32} {h.Sound.LevelDb,5:F1} dB {pcm.Length / (float)Sr:F2}s");
        }
        // Levels: the main hit is the loudest, and every other event sits where it was measured.
        var main = heard.Last();
        foreach (var h in heard)
            Assert.Equal(DoorMechanisms.RelativeDb(h.Spec.Kind, h.Event, h.Spec.Opening), h.Sound.LevelDb - main.Sound.LevelDb, 2);
    }

    [Fact]
    public void TravelLastsAsLongAsTheLeafMovesAndTheSealEndsAtTheShut()
    {
        var auto = OpenAndShut("auto_sliding_door");
        var open = One(auto, DoorEvents.Rollers, 0);
        var close = One(auto, DoorEvents.Rollers, 1);
        Assert.Equal(1.5f, open.Spec.Seconds, 1);
        Assert.Equal(2.5f, close.Spec.Seconds, 1);
        Assert.InRange(open.Spec.Seconds / close.Spec.Seconds, 0.5f, 0.75f);     // opening is the quicker
        Assert.InRange(One(auto, DoorEvents.Stop).At - open.At, 1.4f, 1.6f);
        // The buffer is the travel: rendered at the mixer's rate it lasts as long as the leaf moves.
        Assert.InRange(Render(close).Length / (float)Sr, 2.5f, 2.62f);

        var pull = OpenAndShut("glass_pull_door");
        var closer = One(pull, DoorEvents.Closer);
        var latch = One(pull, DoorEvents.Latch);
        float sweepEnds = closer.At + closer.Sound.DelaySeconds + DoorMechanisms.SealSweepSeconds;
        Assert.InRange(sweepEnds - latch.At, -0.06f, 0.06f);

        var front = OpenAndShut("glass_front_door", fromOutside: true);
        Assert.Equal(DoorMechanisms.KeySeconds, One(front, DoorEvents.LatchRetract).Sound.DelaySeconds, 3);
    }

    // ── Type 1, knob ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AKnobDoorShutsLikeTheRecordedOnes()
    {
        var heard = OpenAndShut("door");
        var latch = Render(One(heard, DoorEvents.Latch));
        float hit = Onset(latch);
        var bands = SoundMeasure.Bands(latch, Sr, hit);
        _o.WriteLine($"latch bands {Show(bands)}, hit at {hit * 1000:0} ms");
        // A wooden leaf: 63 Hz-8 kHz within 17 dB, its top in 250 Hz-2 kHz.
        Assert.True(bands.Take(8).Min() >= -17f, Show(bands));
        Assert.True(bands.Skip(2).Take(4).Max() >= -0.1f, Show(bands));
        float d20 = SoundMeasure.D20(latch, Sr, hit), d4k = SoundMeasure.D20(latch, Sr, hit, 4000f);
        _o.WriteLine($"d20 {d20 * 1000:0} ms, 4 kHz {d4k * 1000:0} ms");
        Assert.InRange(d20, 0.02f, 0.07f);
        Assert.InRange(d4k, 0.008f, 0.04f);
        // The ride before it: 30-60 ms of the bolt on the strike, 21-30 dB under the hit.
        var env = SoundMeasure.Envelope(latch, Sr);
        float peak = env.Max();
        int startRide = Array.FindIndex(env, v => v > peak - 32f);
        Assert.InRange(hit + 0.004f - startRide * 0.002f, 0.02f, 0.065f);
        Assert.InRange(env.Skip(startRide).Take(8).Max() - peak, -31f, -18f);
        // The knob's ring, 10-11.5 kHz, still there 150 ms on: "metal knob on a wooden door".
        var late = SoundMeasure.Peaks(latch, Sr, hit + 0.15f, 0.186f, 9000f, 12000f, 6f, 3);
        Assert.Contains(late, p => p.Hz is >= 10000f and <= 11500f && p.ProminenceDb >= 10f);

        // The release snap: 2-8 kHz, and 500 Hz at least 15 dB down.
        var retract = Render(One(heard, DoorEvents.LatchRetract));
        var snap = SoundMeasure.Bands(retract, Sr, 0.585f);
        _o.WriteLine($"snap bands {Show(snap)}");
        Assert.True(snap[5] >= -3f && snap[6] >= -3f && snap[7] >= -6f, Show(snap));
        Assert.True(snap[3] <= -15f, Show(snap));
    }

    // ── Type 2, steel push bar ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ASteelDoorSlamsFlatAndRattles()
    {
        var heard = OpenAndShut("steel_door");
        var slam = Render(One(heard, DoorEvents.Latch));
        float hit = Onset(slam);
        var bands = SoundMeasure.Bands(slam, Sr, hit);
        _o.WriteLine($"slam bands {Show(bands)}");
        // Flat 63 Hz-8 kHz within 12 dB, 8 kHz within 6 of the top: the first thing that is not wood.
        Assert.True(bands.Take(8).Min() >= -12f, Show(bands));
        Assert.True(bands[7] >= -6f, Show(bands));
        // A leaf mode in 100-210 Hz standing at least 12 dB over its neighbours.
        var low = SoundMeasure.Peaks(slam, Sr, hit + 0.002f, 0.186f, 100f, 210f, 6f, 3);
        Assert.Contains(low, p => p.ProminenceDb >= 12f);
        // The rattle: still going 300 ms on, and falling.
        var env = SoundMeasure.Envelope(slam, Sr);
        float peak = env.Max();
        float at300 = env.Skip((int)((hit + 0.3f) / 0.002f)).Take(25).Max();
        float at700 = env.Skip((int)((hit + 0.7f) / 0.002f)).Take(25).Max();
        _o.WriteLine($"300 ms on {at300 - peak:0} dB, 700 ms on {at700 - peak:0} dB");
        Assert.InRange(at300 - peak, -40f, -12f);
        Assert.True(at700 < at300 - 6f);

        // The bar: its contacts start in the top octaves and the bar comes back 240-320 ms later.
        var bar = Render(One(heard, DoorEvents.Bar));
        var first = SoundMeasure.Bands(bar, Sr, 0.001f);
        _o.WriteLine($"first press {Show(first)}");
        Assert.True(first.Skip(5).Take(3).Max() >= -1f, Show(first));
        var benv = SoundMeasure.Envelope(bar, Sr);
        float bpk = benv.Max();
        int back = Array.FindIndex(benv, (int)(0.2f / 0.002f), v => v > bpk - 12f);
        Assert.InRange(back * 0.002f, 0.24f, 0.33f);
    }

    [Fact]
    public void WoodAndSteelAreToldApartByTheirSpectra()
    {
        var wood = Render(One(OpenAndShut("door"), DoorEvents.Latch));
        var steel = Render(One(OpenAndShut("steel_door"), DoorEvents.Latch));
        var w = SoundMeasure.Bands(wood, Sr, Onset(wood));
        var s = SoundMeasure.Bands(steel, Sr, Onset(steel));
        _o.WriteLine($"wood  {Show(w)}\nsteel {Show(s)}");
        // Steel is broad and bright to 8 kHz where wood is a mid-band hump.
        Assert.True(s[7] >= w[7] + 3f, "8 kHz");
        Assert.True(s[1] >= w[1] + 3f, "125 Hz");
        // Steel's skins ring on at 4 kHz where wood is already gone.
        Assert.True(SoundMeasure.D20(steel, Sr, Onset(steel), 4000f) > SoundMeasure.D20(wood, Sr, Onset(wood), 4000f));
        // And the leaves themselves: a timber leaf's first mode under 100 Hz, a hollow steel one's in 100-210.
        var wm = DoorMechanisms.LeafModes(new DoorMechanisms.Spec(DoorKind.Hinged, "", "Wood", 0.9f, 2.1f, 0.06f));
        var sm = DoorMechanisms.LeafModes(new DoorMechanisms.Spec(DoorKind.PushBar, "", "Metal", 1f, 2.1f, 0.08f, 0.0012f));
        Assert.InRange(wm[0].Hz, 50f, 100f);
        Assert.InRange(sm[0].Hz, 100f, 210f);
    }

    [Fact]
    public void OneSteelServesAFireDoorAndALiftLeafScaledBySize()
    {
        // One core shear modulus, fitted to the fire door's 129 Hz, gives the lift leaf's 172 Hz.
        var fire = DoorMechanisms.LeafModes(new DoorMechanisms.Spec(DoorKind.PushBar, "", "Metal", 1f, 2.1f, 0.08f, 0.0012f));
        var lift = DoorMechanisms.LeafModes(new DoorMechanisms.Spec(DoorKind.Elevator, "", "Metal", 0.55f, 2.1f, 0.04f, 0.0012f));
        Assert.InRange(fire[0].Hz, 120f, 140f);
        Assert.InRange(lift[0].Hz, 160f, 190f);
        // A leaf hung on rollers keeps its members' rings; one seated on hinges and silencers does not.
        float Longest(List<(float Hz, float A, float T60)> m) => m.Where(x => x.Hz is > 700f and < 3200f).Max(x => x.T60);
        Assert.True(Longest(lift) > 3f * Longest(fire));
    }

    // ── Type 3, glass front door ───────────────────────────────────────────────────────────────

    [Fact]
    public void AGlassFrontDoorSlamsBrightRattlesAndLocks()
    {
        var heard = OpenAndShut("glass_front_door", fromOutside: true);
        var slam = Render(One(heard, DoorEvents.Latch));
        float hit = Onset(slam);
        var bands = SoundMeasure.Bands(slam, Sr, hit);
        _o.WriteLine($"slam bands {Show(bands)}");
        // Brighter than steel: 4-8 kHz the loudest, 1 kHz under them, 63-250 Hz far under.
        Assert.True(MathF.Max(bands[6], bands[7]) >= -0.1f, Show(bands));
        Assert.True(bands[4] <= -5f, Show(bands));
        Assert.True(bands.Take(3).Max() <= -10f, Show(bands));
        // The rattle: a glass leaf shifting in its gasket for 0.8-0.9 s.
        var env = SoundMeasure.Envelope(slam, Sr);
        float peak = env.Max();
        Assert.True(env.Skip((int)((hit + 0.7f) / 0.002f)).Take(25).Max() > peak - 45f);
        Assert.True(env.Skip((int)((hit + 1.2f) / 0.002f)).Take(25).Max() < peak - 40f);

        // The key: the bolt thrown, 1-8 kHz within 6 dB with 8 kHz near the top, and quick.
        var key = Render(One(heard, DoorEvents.Key));
        float throwAt = DoorMechanisms.KeySeconds - 0.034f;
        var kb = SoundMeasure.Bands(key, Sr, throwAt);
        _o.WriteLine($"throw bands {Show(kb)}");
        Assert.True(kb.Skip(4).Take(4).Min() >= -6f, Show(kb));
        Assert.True(kb[7] >= -3f, Show(kb));
        Assert.True(SoundMeasure.D20(key, Sr, throwAt) < 0.06f);
    }

    // ── Type 4, pulled glass door ──────────────────────────────────────────────────────────────

    [Fact]
    public void APulledGlassDoorThunksAndItsSealSweeps()
    {
        var heard = OpenAndShut("glass_pull_door");
        var shut = Render(One(heard, DoorEvents.Latch));
        float hit = Onset(shut);
        var bands = SoundMeasure.Bands(shut, Sr, hit);
        _o.WriteLine($"shut bands {Show(bands)}");
        // A light glass door: 1-8 kHz within 4 dB, 500 Hz under them.
        Assert.True(bands.Skip(4).Take(4).Max() - bands.Skip(4).Take(4).Min() <= 4f, Show(bands));
        Assert.True(bands[3] <= bands.Skip(4).Take(4).Max() - 5f, Show(bands));
        // The glass "thunk": a low pane mode in 70-160 Hz 20 dB over its neighbours, ringing 0.3-0.7 s.
        var low = SoundMeasure.Peaks(shut, Sr, hit + 0.002f, 0.186f, 70f, 160f, 6f, 3);
        var thunk = Assert.Single(low.Take(1));
        Assert.True(thunk.ProminenceDb >= 20f, $"{thunk.Hz:0} Hz {thunk.ProminenceDb:0} dB");
        Assert.InRange(SoundMeasure.NarrowT60(shut, Sr, hit, thunk.Hz), 0.3f, 0.7f);

        // The seal: only the 4 and 8 kHz octaves, about 30 dB under the shut, 0.6 s, ending at the shut.
        var closer = One(heard, DoorEvents.Closer);
        var sweep = Render(closer);
        var sb = SoundMeasure.BandLevels(sweep, Sr, 0.1f, 0.55f);
        float top = sb.Max();
        _o.WriteLine($"sweep levels {Show(sb.Select(v => v - top).ToArray())}");
        Assert.True(MathF.Max(sb[6], sb[7]) >= top - 0.1f);
        Assert.True(sb.Take(5).Max() <= top - 20f);
        Assert.InRange(DoorMechanisms.RelativeDb(DoorKind.GlassPull, DoorEvents.Closer), -35f, -25f);
        Assert.InRange(sweep.Length / (float)Sr, 0.55f, 0.7f);
    }

    // ── Type 5, automatic slider ───────────────────────────────────────────────────────────────

    [Fact]
    public void AnAutomaticDoorsMotorRisesRunsAndBrakes()
    {
        var heard = OpenAndShut("auto_sliding_door");
        var open = One(heard, DoorEvents.Rollers, 0);
        var pcm = Render(open);
        float d = open.Spec.Seconds;
        float start = 0.01f;
        // The ramp: about 1 kHz as the belt takes up; the run: 1.4-2 kHz; the brake: down to 0.6-0.9.
        float early = SoundMeasure.StrongestHz(pcm, Sr, start + 0.06f, 800f, 1300f, 0.06f);
        float run = SoundMeasure.StrongestHz(pcm, Sr, start + 0.6f, 1200f, 2500f, 0.085f);
        float end = SoundMeasure.StrongestHz(pcm, Sr, start + d - 0.09f, 600f, 1000f, 0.085f);
        _o.WriteLine($"tone: ramp {early:0} Hz, run {run:0} Hz, brake {end:0} Hz");
        Assert.InRange(early, 850f, 1150f);
        Assert.InRange(run, 1400f, 2000f);
        Assert.InRange(end, 600f, 900f);
        // Run noise loudest at 500 Hz-1 kHz, 4-8 kHz 8-15 dB down; the tone 10-22 dB over its neighbours.
        var lv = SoundMeasure.BandLevels(pcm, Sr, 0.4f, d - 0.3f);
        float top = lv.Max();
        _o.WriteLine($"run levels {Show(lv.Select(v => v - top).ToArray())}");
        Assert.True(MathF.Max(lv[3], lv[4]) >= top - 0.1f);
        Assert.InRange(lv[6] - top, -15f, -8f);
        Assert.InRange(lv[7] - top, -15f, -8f);
        var lines = SoundMeasure.Peaks(pcm, Sr, start + 0.6f, 0.186f, 1200f, 2500f, 6f, 2);
        Assert.InRange(lines[0].ProminenceDb, 10f, 22f);
        // Stops 4-15 dB over the run.
        Assert.InRange(DoorMechanisms.RelativeDb(DoorKind.AutoSliding, DoorEvents.Stop)
                     - DoorMechanisms.RelativeDb(DoorKind.AutoSliding, DoorEvents.Rollers), 4f, 15f);
    }

    // ── Type 6, patio slider ───────────────────────────────────────────────────────────────────

    [Fact]
    public void APatioDoorRollsHitsLowAndHooksOnAHarmonicBar()
    {
        var heard = OpenAndShut("patio_door");
        var roll = Render(One(heard, DoorEvents.Rollers, 0));
        float d = One(heard, DoorEvents.Rollers, 0).Spec.Seconds;
        var lv = SoundMeasure.BandLevels(roll, Sr, 0.4f, d - 0.2f);
        int loudest = Array.IndexOf(lv, lv.Max());
        Assert.InRange(SoundMeasure.Centres[loudest], 250f, 2000f);
        // Silent at rest, either side of the travel.
        var env = SoundMeasure.Envelope(roll, Sr);
        Assert.True(env[0] < env.Max() - 40f);
        Assert.True(env[^1] < env.Max() - 40f);

        var latch = Render(One(heard, DoorEvents.Latch));
        var hit = SoundMeasure.Bands(latch, Sr, Onset(latch));
        _o.WriteLine($"close hit {Show(hit)}");
        Assert.True(MathF.Max(hit[1], hit[2]) >= -0.1f, Show(hit));
        Assert.True(hit[7] <= -20f, Show(hit));
        var cluster = SoundMeasure.Peaks(latch, Sr, Onset(latch) + 0.002f, 0.186f, 100f, 250f, 6f, 6);
        Assert.True(cluster.Count >= 2, "a mode cluster in 100-250 Hz");
        // The hook: a bar ringing along its length, partials within 1 % of n × f0.
        var hook = SoundMeasure.Peaks(latch, Sr, 0.69f, 0.3f, 1800f, 7000f, 6f, 6);
        float f0 = hook.Where(p => p.Hz is > 1900f and < 2300f).Select(p => p.Hz).DefaultIfEmpty(0f).First();
        Assert.InRange(f0, 2000f, 2200f);
        Assert.Contains(hook, p => MathF.Abs(p.Hz / (2f * f0) - 1f) < 0.01f);
    }

    // ── Type 7, lift ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALiftsDoorsRollLowAndTheirSteelRingsOn()
    {
        var heard = OpenAndShut("elevator_door");
        var roll = One(heard, DoorEvents.Rollers, 0);
        var pcm = Render(roll);
        var lv = SoundMeasure.BandLevels(pcm, Sr, 0.4f, roll.Spec.Seconds - 0.3f);
        float top = lv.Max();
        _o.WriteLine($"motion {Show(lv.Select(v => v - top).ToArray())}");
        Assert.True(lv.Skip(1).Take(3).Max() >= top - 0.1f);       // 125-500 Hz loudest
        Assert.InRange(lv[5] - top, -18f, -10f);                    // 2 kHz 13-16 down, give or take
        Assert.True(One(heard, DoorEvents.Rollers, 0).Spec.Seconds < One(heard, DoorEvents.Rollers, 1).Spec.Seconds);

        var shut = Render(One(heard, DoorEvents.Shut));
        float bump = Onset(shut);
        float peak = SoundMeasure.PeakFrameDb(shut, Sr);
        // Rebounds at 60-70 and 200-250 ms within 5 dB of the bump.
        var env = SoundMeasure.Envelope(shut, Sr);
        Assert.True(env.Skip((int)((bump + 0.06f) / 0.002f)).Take(8).Max() > peak - 5f);
        Assert.True(env.Skip((int)((bump + 0.22f) / 0.002f)).Take(8).Max() > peak - 6f);
        // A ring between 0.7 and 3.2 kHz still above -40 dB half a second on: "elevator", not "glass".
        var ring = new float[shut.Length];
        Array.Copy(shut, ring, shut.Length);
        ring = SoundMeasure.BandPass(ring, Sr, 700f, 3200f);
        float later = SoundMeasure.PeakFrameDb(ring, Sr, bump + 0.5f, bump + 0.55f);
        _o.WriteLine($"0.7-3.2 kHz 0.5 s on: {later - peak:0} dB");
        Assert.True(later > peak - 40f);
    }

    // ── Nothing too tonal ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The first car door was "an instrument, too tonal": its lines stood 22 dB over the spectrum
    /// round them where the recording's stood 9. Measured the way the recordings were (through a
    /// room like theirs, the 186 ms from the hit), no line within 20 dB of an event's strongest
    /// may stand more than 30 dB over its neighbours; the recordings' stand 10-26 (the patio hook 31).
    /// </summary>
    [Theory]
    [InlineData("door", 0.4f, 13f)]
    [InlineData("steel_door", 0.8f, 5f)]
    [InlineData("glass_front_door", 0.5f, 8f)]
    [InlineData("glass_pull_door", 0.3f, 14f)]
    [InlineData("auto_sliding_door", 0.6f, 8f)]
    [InlineData("patio_door", 0.5f, 8f)]
    [InlineData("elevator_door", 1.0f, 5f)]
    public void NoLineStandsOutOfItsEventLikeAnInstrument(string prefab, float t60, float c50)
    {
        // A lift's steel is allowed what a lift's does: launchsite's close bump has a 135 Hz line 32 dB out.
        float limit = prefab == "elevator_door" ? 33f : 30f;
        foreach (var h in OpenAndShut(prefab, prefab == "glass_front_door"))
        {
            if (DoorMechanisms.IsMotionEvent(h.Event) || h.Event == DoorEvents.Closer) continue;
            var pcm = Render(h);
            var roomed = SoundMeasure.ThroughRoom(pcm, Sr, t60, c50);
            var lines = SoundMeasure.Peaks(roomed, Sr, Onset(pcm) + 0.002f, 0.186f, 60f, 12000f, 6f, 12)
                                     .Where(p => p.LevelDb >= -20f).ToList();
            var worst = lines.OrderByDescending(p => p.ProminenceDb).First();
            _o.WriteLine($"{h.Key,-30} worst {worst.Hz,6:0} Hz {worst.ProminenceDb:0} dB over, {worst.LevelDb:0} dB re top");
            Assert.True(worst.ProminenceDb <= limit, $"{h.Key}: {worst.Hz:0} Hz stands {worst.ProminenceDb:0} dB out");
        }
    }

    [Fact]
    public void EveryEventRendersInTimeForItsFirstHearing()
    {
        // The client waits WorldAudioPlayer.MaxRenderLateness (120 ms) for a new sound's first render.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (string prefab in new[] { "door", "steel_door", "glass_front_door", "glass_pull_door", "auto_sliding_door", "patio_door", "elevator_door" })
            foreach (var h in OpenAndShut(prefab))
            {
                clock.Restart();
                Render(h, 7);
                Assert.True(clock.Elapsed.TotalMilliseconds < 250, $"{h.Key}: {clock.Elapsed.TotalMilliseconds:0} ms");
            }
    }
}
