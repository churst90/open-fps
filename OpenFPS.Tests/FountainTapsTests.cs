using System.Numerics;
using OpenFPS.Audio.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Models;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using Xunit.Abstractions;
using OpenFPS.Sound;
using OpenFPS.Materials;

namespace OpenFPS.Tests;

/// <summary>
/// A fountain from its spec (docs/RUNNING_WATER.md 15.8): placed once, the server lays a voice where each of its waters
/// lands from the spec's own taps, the rim jets where they come down; near, every tap its own voice, and far enough off
/// for its taps to be one point to the ear, the whole heard from one voice carrying every tap's water.
/// </summary>
public class FountainTapsTests
{
    private readonly ITestOutputHelper _o;
    public FountainTapsTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    /// <summary>The park fountain's taps are where its waters land, the rim jets on taps of their own between the rocks
    /// and the kerb; every fall lands on a tap and the bowl's jet on the bowl.</summary>
    [Fact]
    public void TheFountainsTapsAreWhereItsWatersLand()
    {
        var spec = ModelLibrary.Presets.Water("park_fountain");
        Assert.True(spec.HeardFromTaps);
        Assert.Equal(9, spec.Taps.Length);
        Assert.All(spec.Falls, f => Assert.InRange(f.Tap, 0, spec.Taps.Length - 1));
        foreach (var f in spec.Falls)
        {
            var tap = spec.Taps[f.Tap];
            float out_ = MathF.Sqrt(tap.EastMetres * tap.EastMetres + tap.NorthMetres * tap.NorthMetres);
            if (f.Name.Contains("rim jets")) Assert.InRange(out_, 2.5f, 3.2f);
            else if (f.Name.Contains("central jet")) Assert.True(out_ < 0.1f && tap.UpMetres > 1f);
            else Assert.InRange(out_, 1.5f, 2.5f);
        }
        Assert.Equal(4, spec.Falls.Count(f => f.Name.Contains("rim jets")));
        Assert.Equal(4, spec.Falls.Where(f => f.Name.Contains("rim jets")).Select(f => f.Tap).Distinct().Count());
    }

    /// <summary>Placed once, the server lays a voice at every tap from the spec, turned and moved with the feature, and
    /// the feature itself has no voice of its own.</summary>
    [Fact]
    public void ThePlacedFountainIsLaidAsItsTaps()
    {
        var world = Arch.Core.World.Create();
        try
        {
            var at = new Vector3(-325f, 0.35f, 227f);
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
            var feature = world.Create(new Transform { Position = at, Rotation = turn, Scale = Vector3.One },
                new SoundEmitterComponent { IsSynth = true, SoundId = "water:park_fountain/elm_park", Range = 160f, MinDistance = 1.5f, Volume = 1f },
                new IdentityComponent { Name = "Elm Park fountain" });
            var taps = MapManager.SpawnTaps(world, feature);
            var spec = ModelLibrary.Presets.Water("park_fountain");
            Assert.Equal(spec.Taps.Length, taps.Count);
            Assert.False(world.Has<SoundEmitterComponent>(feature));
            for (int i = 0; i < taps.Count; i++)
            {
                var em = world.Get<SoundEmitterComponent>(taps[i]);
                Assert.Equal($"water:park_fountain/elm_park/{i}", em.SoundId);
                Assert.Equal(160f, em.Range);
                var want = at + Vector3.Transform(new Vector3(spec.Taps[i].EastMetres, spec.Taps[i].UpMetres, spec.Taps[i].NorthMetres), turn);
                Assert.True(Vector3.Distance(want, world.Get<Transform>(taps[i]).Position) < 1e-4f);
            }
            Assert.Equal("Elm Park fountain, north rim jets", world.Get<IdentityComponent>(taps[5]).Name);
            // Anything else is left as it is.
            var other = world.Create(new Transform { Position = at }, new SoundEmitterComponent { IsSynth = true, SoundId = "water:park_fountain/elm_park/0" });
            Assert.Empty(MapManager.SpawnTaps(world, other));
            Assert.True(world.Has<SoundEmitterComponent>(other));
        }
        finally { Arch.Core.World.Destroy(world); }
    }

    /// <summary>Heard whole, the tap that plays it carries every tap's water, every place of each, and the others are
    /// silent: the same streams, summed, never a copy.</summary>
    [Fact]
    public void HeardWholeOneTapCarriesEveryTapsWater()
    {
        var spec = ModelLibrary.Presets.Water("park_fountain");
        var apart = new WaterFeatureVoice("park_fountain/a", spec, Rate, 4, placesPerTap: 4);
        var whole = new WaterFeatureVoice("park_fountain/a", spec, Rate, 4, placesPerTap: 4) { WholeFrom = 0 };
        double worst = 0, power = 0;
        for (long at = 0; at < Rate * 2; at++)
        {
            float sum = 0f;
            for (int t = 0; t < spec.Taps.Length; t++)
                for (int p = 0; p < apart.PlacesPerTap; p++) sum += apart.Sample(t, p, at);
            float one = whole.Sample(0, 0, at);
            worst = Math.Max(worst, Math.Abs(one - sum));
            power += sum * (double)sum;
            Assert.Equal(0f, whole.Sample(5, 0, at));
            Assert.Equal(0f, whole.Sample(0, 2, at));
        }
        _o.WriteLine($"every tap's water summed against the tap heard whole: {worst:E2} Pa apart at worst");
        Assert.True(power > 0);
        Assert.True(worst < 1e-5, $"{worst} Pa apart");
    }

    /// <summary>The fountain's taps as the server lays them, as a client holds them.</summary>
    private static ClientAudioHarness Fountain()
    {
        var h = new ClientAudioHarness();
        var spec = ModelLibrary.Presets.Water("park_fountain");
        for (int i = 0; i < spec.Taps.Length; i++)
        {
            var t = spec.Taps[i];
            var def = new EntityDefinition
            {
                EntityId = 1200 + i,
                Type = EntityType.StaticObject,
                Transform = new Transform { Position = new Vector3(t.EastMetres, 0.35f + t.UpMetres, t.NorthMetres), Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = $"water:park_fountain/elm_park/{i}";
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.MinDistance = 1.5f;
            def.SoundEmitter.Range = 400f;
            h.World.RegisterDefinition(def);
        }
        return h;
    }

    /// <summary>
    /// By the cut's own angles: beside the fountain each of its nine taps is a voice; from 200 m, where its taps cover
    /// under 4°, one voice plays it; walking back in, it parts again past 7° (30 m), and not before (60 m: TrainVoicing's
    /// hysteresis).
    /// </summary>
    [Fact]
    public void FarOffTheFountainIsOneVoiceAndNearItsTapsPart()
    {
        var h = Fountain();
        int Voiced() => h.Mixer.Live.Count(id => h.Mixer.Latest.TryGetValue(id, out var e) && e.Place == 0 && (e.PhysicalKey ?? "").StartsWith("water:"));
        h.StandAt(new Vector3(0f, 0f, -12f));
        h.Tick(120);
        int near = Voiced();
        h.StandAt(new Vector3(0f, 0f, -200f));
        h.Tick(240);
        int far = Voiced();
        // Its rim jets' taps 5.6 m apart across it: 7° at about 46 m, 4° at about 80 m.
        h.StandAt(new Vector3(0f, 0f, -60f));
        h.Tick(240);
        int between = Voiced();
        h.StandAt(new Vector3(0f, 0f, -30f));
        h.Tick(240);
        int back = Voiced();
        _o.WriteLine($"tap voices: {near} at 12 m, {far} at 200 m, {between} at 60 m coming back, {back} at 30 m");
        Assert.Equal(9, near);
        Assert.Equal(1, far);
        Assert.Equal(1, between);
        Assert.Equal(9, back);
    }
}
