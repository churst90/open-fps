using System.Numerics;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A recording's copy off a wall is split as the wall splits it: the mirror share a clean copy, the share
/// a rough surface scatters smeared by its roughness through the engines' EchoDiffuser (EchoWashState).
/// A smooth wall (glass, marble, still water) is left as it was. Recorded loops' copies carry the split in
/// one voice; your footsteps' copies get the wash as a voice of its own beside the mirror, as a one-off
/// sound's do (WorldAudioPlayer.QueueWash).
/// </summary>
public class EchoWashTests
{
    private readonly ITestOutputHelper _o;
    public EchoWashTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private const int Rate = 48000;

    private static float Scattering(string material) => AcousticRegistry.GetProperties(material).Scattering;

    private static float[] Impulse(float scattering, float mirror, int n = Rate / 5)
    {
        var s = new EchoWashState();
        s.Configure(scattering, mirror, 7, Rate);
        var x = new float[n];
        x[0] = 1f;
        var y = new float[n];
        s.Process(x, y, 1);
        return y;
    }

    /// <summary>Milliseconds until this share of the response's energy has arrived.</summary>
    private static double EnergyTimeMs(float[] y, double share)
    {
        double total = y.Sum(v => (double)v * v), run = 0;
        for (int i = 0; i < y.Length; i++)
        {
            run += (double)y[i] * y[i];
            if (run >= share * total) return i * 1000.0 / Rate;
        }
        return y.Length * 1000.0 / Rate;
    }

    [Fact]
    public void Smooth_surfaces_are_not_smeared_and_rough_ones_are()
    {
        foreach (var m in new[] { "Glass", "Marble", "Water" })
            Assert.False(EchoWashState.Applies(Scattering(m)), m);
        foreach (var m in new[] { "Concrete", "Brick", "Wood", "Foliage", "Audience" })
            Assert.True(EchoWashState.Applies(Scattering(m)), m);
    }

    /// <summary>The copy keeps its energy, its clean share arrives at once, and the rougher the wall the
    /// longer the rest is spread.</summary>
    [Fact]
    public void A_rough_walls_share_is_spread_by_its_roughness_and_the_energy_is_kept()
    {
        double last = 0;
        foreach (var m in new[] { "Concrete", "Brick", "Audience" })
        {
            float s = Scattering(m);
            var y = Impulse(s, MathF.Sqrt(1f - s));
            var wash = Impulse(s, 0f);
            double db = 10 * Math.Log10(y.Sum(v => (double)v * v));
            double washMs = EnergyTimeMs(wash, 0.9);
            double crest = 20 * Math.Log10(wash.Max(v => MathF.Abs(v)) / Math.Sqrt(wash.Take(Rate / 50).Average(v => (double)v * v)));
            _o.WriteLine($"{m} (s {s:F2}): copy {db:+0.00;-0.00} dB; the wash has 90 % of its energy by {washMs:F2} ms, crest {crest:F1} dB over 20 ms");
            Assert.InRange(db, -0.6, 0.6);
            // The clean share arrives at once (with the diffuser's own first tap, (-g)^4 of the wash).
            Assert.True(y[0] >= MathF.Sqrt(1f - s) - 1e-4f, $"{m}: the mirror share did not arrive at once");
            Assert.True(washMs > last, $"{m} is spread no longer than a smoother wall");
            last = washMs;
        }
        Assert.True(last > 5.0, "a crowd's share is not spread");
    }

    [Fact]
    public void The_mixer_callbacks_work_allocates_nothing()
    {
        var s = new EchoWashState();
        s.Configure(0.45f, 0.7f, 3, Rate);
        var x = new float[1024 * 2];
        var y = new float[1024 * 2];
        for (int i = 0; i < x.Length; i++) x[i] = MathF.Sin(i * 0.01f);
        s.Process(x, y, 2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 20; k++) s.Process(x, y, 2);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── In the game's choices ──────────────────────────────────────────────────────────────────────

    private static List<OpenFPS.Client.AudioEngine.Data.SpatialEmitter> StepCopies(ClientAudioHarness h, int from)
        => h.Mixer.Started.Skip(from)
               .Where(e => e.EntityId <= ClientAudioSystem.STEP_ECHO_BASE_ID && e.EntityId > ClientAudioSystem.STEP_ECHO_BASE_ID - 48)
               .ToList();

    private static List<OpenFPS.Client.AudioEngine.Data.SpatialEmitter> StepBesideA(string material)
    {
        var h = new ClientAudioHarness(Sounds());
        h.AddWall(1, new Vector3(3.25f, 1.5f, 0f), new Vector3(0.5f, 3f, 10f), material);
        h.StandAt(Vector3.Zero);
        h.Tick();
        int from = h.Mixer.Started.Count;
        h.Audio.OnOwnFootstep(new Vector3(0f, 0f, 0.2f), "Concrete", "0");
        h.Tick(3);
        return StepCopies(h, from);
    }

    /// <summary>Your step off a smooth wall is the clean copy alone; off a rough one, the clean copy and
    /// beside it the wall's wash, √(s / (1 − s)) of it.</summary>
    [Fact]
    public void Your_step_off_a_rough_wall_gets_its_wash_and_off_a_smooth_one_does_not()
    {
        var glass = StepBesideA("Glass");
        Assert.NotEmpty(glass);
        Assert.All(glass, e => Assert.Equal(0f, e.EchoScattering));

        var brick = StepBesideA("Brick");
        float s = Scattering("Brick");
        var wash = brick.Where(e => e.EchoScattering > 0f).ToList();
        var mirror = brick.Where(e => e.EchoScattering == 0f).ToList();
        foreach (var e in brick) _o.WriteLine($"copy at ({e.Position.X:F2}, {e.Position.Y:F2}, {e.Position.Z:F2}): volume {e.Volume:F4}, smear {e.EchoScattering:F2}");
        var w = Assert.Single(wash);
        Assert.Equal(s, w.EchoScattering, 3);
        Assert.Equal(0f, w.EchoMirrorShare);
        var m = mirror.Single(e => Vector3.Distance(e.Position, w.Position) < 1e-3f);
        Assert.Equal(MathF.Sqrt(s / (1f - s)), w.Volume / m.Volume, 3);
    }

    private const int Speaker = 50;

    private static void Solid(ClientAudioHarness h, int id, Vector3 centre, Vector3 size, string material)
    {
        var def = new EntityDefinition
        {
            EntityId = id, Type = EntityType.StaticObject,
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true };
        def.Material.Material = material;
        h.World.RegisterDefinition(def);
    }

    /// <summary>A recorded loop's copies in a yard walled in this, as ReflectionSlotTests's yard.</summary>
    private static List<OpenFPS.Client.AudioEngine.Data.SpatialEmitter> LoopCopiesIn(string material)
    {
        var h = new ClientAudioHarness();
        Solid(h, 2, new Vector3(15.25f, 3f, 0f), new Vector3(0.5f, 6f, 31f), material);
        Solid(h, 3, new Vector3(-15.25f, 3f, 0f), new Vector3(0.5f, 6f, 31f), material);
        Solid(h, 4, new Vector3(0f, 3f, 15.25f), new Vector3(31f, 6f, 0.5f), material);
        Solid(h, 5, new Vector3(0f, 3f, -15.25f), new Vector3(31f, 6f, 0.5f), material);
        var def = new EntityDefinition
        {
            EntityId = Speaker, Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(3f, 2f, 4f), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter = new SoundEmitterComponent();
        def.SoundEmitter.SoundId = "ANNOUNCE/st_louis_welcome";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = 400f;
        def.SoundEmitter.MinDistance = 12f;
        h.World.RegisterDefinition(def);
        h.StandAt(new Vector3(-12f, 0f, -12f));
        int first = -30000 - Speaker * (EarlyReflections.MaxArrivals + 1);
        bool Copies() => h.Mixer.Started.Any(e => e.EntityId <= first && e.EntityId > first - EarlyReflections.MaxArrivals);
        Assert.True(h.TickUntil(Copies, 300), $"no wall of {material} answered");
        return h.Mixer.Started.Where(e => e.EntityId <= first && e.EntityId > first - EarlyReflections.MaxArrivals).ToList();
    }

    /// <summary>A recorded loop's copy carries its wall's roughness and the clean share √(1 − s); a smooth
    /// wall's is not smeared at all (the mixer adds no unit below EchoWashState.SmoothScattering).</summary>
    [Fact]
    public void A_recorded_loops_copy_carries_its_walls_roughness()
    {
        float brick = Scattering("Brick");
        foreach (var e in LoopCopiesIn("Brick"))
        {
            Assert.Equal(brick, e.EchoScattering, 3);
            Assert.Equal(MathF.Sqrt(1f - brick), e.EchoMirrorShare, 3);
        }
        Assert.All(LoopCopiesIn("Glass"), e => Assert.False(EchoWashState.Applies(e.EchoScattering)));
    }

    private static string Sounds([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "OpenFPS.Client", "ASSETS", "SOUNDS"));
}
