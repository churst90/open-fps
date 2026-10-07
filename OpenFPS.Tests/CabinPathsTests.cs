using System;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Sitting in a vehicle, its sound arrives by the ways it really gets in (CabinPaths): the engine
/// through the firewall, each wheel through its own arch, the exhaust under the floor, the wind at each
/// A-pillar. What these hold: every way in is where it should be, the paths together are as loud as the
/// one interior signal was, a tap takes its path from the vehicle's own voice without losing or
/// doubling any of it, and the pieces the split is built from (the spray per wheel, the equaliser, the
/// clock that lines the taps up) do what they say.
/// </summary>
public class CabinPathsTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _o;
    public CabinPathsTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    [Fact]
    public void ACarHasEveryWayInWhereItIs()
    {
        var v = MachineRegistry.VehicleFor("i4_economy");
        var lay = CabinPaths.Build(v)!;
        Assert.NotNull(lay);
        var g = VehicleCabin.Measure(v)!.Value;
        var kinds = lay.Paths.Select(p => p.Kind).ToArray();
        Assert.Equal(CabinPaths.Kind.Bulkhead, kinds[0]);
        Assert.Equal(1, kinds.Count(k => k == CabinPaths.Kind.Exhaust));
        Assert.Equal(4, kinds.Count(k => k == CabinPaths.Kind.Wheel));
        Assert.Equal(2, kinds.Count(k => k == CabinPaths.Kind.Wind));
        // The tread tone once, under the floor between the axles.
        Assert.Equal(1, kinds.Count(k => k == CabinPaths.Kind.Tread));
        Assert.Equal(0f, lay.Paths[lay.Tread].At.X);
        Assert.Equal(-1, lay.Door);
        // The engine is in front, so the firewall is the front of the cabin; the pipe comes in behind.
        Assert.Equal(g.Front, lay.Paths[0].At.Z, 3);
        Assert.True(lay.Paths[lay.Exhaust].At.Z < lay.Paths[0].At.Z - 1f);
        // Each wheel on its own side, at its own end; the wind either side of the windscreen.
        foreach (var p in lay.Paths.Where(p => p.Kind == CabinPaths.Kind.Wheel))
            Assert.Equal(p.Side, MathF.Sign(p.At.X));
        Assert.True(lay.Paths[lay.WindLeft].At.X < 0f && lay.Paths[lay.WindRight].At.X > 0f);
        // Each wheel rolls at its share of its axle group: the shares of a group add to one.
        Assert.Equal(2f, lay.RollingShare.Sum(), 3);
    }

    [Fact]
    public void ABusHasItsDoorOnTheKerbSideAndAMotorcycleHasNoCabin()
    {
        var bus = CabinPaths.Build(MachineRegistry.VehicleFor("transit_bus"))!;
        Assert.True(bus.Door > 0);
        Assert.Equal(CabinPaths.Kind.Door, bus.Paths[bus.Door].Kind);
        Assert.Null(CabinPaths.Build(MachineRegistry.VehicleFor("sportbike")));
    }

    [Fact]
    public void EveryPathIsPlayedWhereTheOneVoiceWasOnlyTurned()
    {
        var v = MachineRegistry.VehicleFor("i4_economy");
        var lay = CabinPaths.Build(v)!;
        var ear = new Vector3(-0.36f, 1.25f, 0.28f);
        for (int p = 0; p < lay.Count; p++)
            Assert.Equal(CabinPaths.PlacedMetres, CabinPaths.Offset(lay, p, ear).Length(), 3);
        Assert.Equal(CabinPaths.OnePlace.Length(), CabinPaths.PlacedMetres, 2);
    }

    /// <summary>Mean square of the voice inside, pascals, over <paramref name="seconds"/> after one to settle.</summary>
    private static double Inside(bool paths, float kmh, float seconds = 3f)
    {
        bool was = CabinPaths.Enabled;
        CabinPaths.Enabled = paths;
        try
        {
            var v = MachineRegistry.VehicleFor("i4_economy");
            float speed = kmh / 3.6f;
            var voice = new EngineVoiceState(v, Rate, 7) { TargetSpeed = speed, CompensateLevel = false, Interior = true };
            Assert.Equal(paths, voice.CabinLayout != null);
            voice.PlaceAtSpeed(speed);
            voice.Revive();
            var body = new WheelDynamics(v);
            body.Hold(speed, 0f, 0f);
            voice.Wheels = body.Wheels.Select(w => OpenFPS.Common.Networking.WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand, 0f)).ToArray();
            var buf = new float[1024];
            for (int i = 0; i < Rate / 1024; i++) voice.Render(buf);
            double sum = 0; long n = 0;
            for (int b = 0; b < seconds * Rate / 1024; b++)
            {
                voice.Render(buf);
                foreach (float s in buf) { double pa = s * voice.PascalsAtFullScale; sum += pa * pa; n++; }
            }
            return sum / n;
        }
        finally { CabinPaths.Enabled = was; }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(50f)]
    [InlineData(100f)]
    public void ThePathsTogetherAreAsLoudAsTheOneSignal(float kmh)
    {
        double one = Inside(false, kmh), split = Inside(true, kmh);
        double db = 10 * Math.Log10(split / one);
        _o.WriteLine($"{kmh} km/h: paths {10 * Math.Log10(split / 4e-10):F1} dB, one signal {10 * Math.Log10(one / 4e-10):F1} dB, {db:+0.00;-0.00} dB");
        Assert.InRange(db, -0.5, 0.5);
    }

    [Fact]
    public void ATapTakesItsPathAndTheVoiceLetsGoOfIt()
    {
        var v = MachineRegistry.VehicleFor("i4_economy");
        float speed = 60f / 3.6f;
        var voice = new EngineVoiceState(v, Rate, 7) { TargetSpeed = speed, CompensateLevel = false, Interior = true };
        voice.PlaceAtSpeed(speed);
        voice.Revive();
        var buf = new float[1024];
        for (int i = 0; i < Rate / 1024; i++) voice.Render(buf);
        var lay = voice.CabinLayout!;
        for (int p = 1; p < lay.Count; p++) voice.SetCabinTapLive(p, true);
        // A tenth of a second for the hand-over's slew, then one block.
        for (int i = 0; i < Rate / 10 / 1024 + 1; i++) voice.Render(buf);
        long at = voice.Played;
        voice.Render(buf);
        double main = 0, taps = 0, worst = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            float tapped = 0f;
            for (int p = 1; p < lay.Count; p++) tapped += voice.ReadCabinAt(p, at + i);
            // What the voice plays and what its taps play is the whole vehicle, sample for sample.
            worst = Math.Max(worst, Math.Abs(buf[i] + tapped - voice.ReadAt(at + i)));
            main += buf[i] * (double)buf[i];
            taps += tapped * (double)tapped;
        }
        _o.WriteLine($"main {10 * Math.Log10(main + 1e-30):F1} dB, taps {10 * Math.Log10(taps + 1e-30):F1} dB, worst difference {worst:E2}");
        Assert.True(taps > 0);
        Assert.True(worst < 1e-5);
    }

    [Fact]
    public void EachWheelsSprayAddsUpToTheCabinsSpray()
    {
        var v = MachineRegistry.VehicleFor("i4_economy");
        var body = new WheelDynamics(v);
        var wet = new WetTyres(v, body.Wheels.Select(w => w.Front).ToArray(), body.Wheels.Select(w => w.Axle).ToArray(), Rate, 5);
        int n = body.Wheels.Length;
        var water = Enumerable.Repeat(1.2f, n).ToArray();
        var gain = Enumerable.Repeat(1f, n).ToArray();
        var texture = Enumerable.Repeat(0.7f, n).ToArray();
        double worst = 0, energy = 0;
        for (int b = 0; b < 40; b++)
        {
            if (b == 10) wet.EnableCorners();
            wet.Block(water, gain, texture, 20f, 512);
            for (int i = 0; i < 512; i++)
            {
                wet.Step();
                if (b < 10) continue;
                float sum = wet.CabinTail;
                for (int k = 0; k < n; k++) sum += wet.CabinWheel(k);
                worst = Math.Max(worst, Math.Abs(sum - wet.Cabin));
                energy += wet.Cabin * (double)wet.Cabin;
            }
        }
        Assert.True(energy > 0);
        Assert.True(worst < 1e-5);
    }

    [Fact]
    public void TheEqualiserMakesTheGainAskedForInEveryBand()
    {
        var rng = new Random(3);
        for (int trial = 0; trial < 5; trial++)
        {
            var want = Enumerable.Range(0, BandEq.Bands).Select(_ => (float)(rng.NextDouble() * 12 - 6)).ToArray();
            var eq = BandEq.Design(want, Rate);
            for (int k = 0; k < BandEq.Bands; k++)
                Assert.InRange(eq.BandDb(k, Rate) - want[k], -0.25f, 0.25f);
        }
        // Flat asked, flat given.
        var flat = BandEq.Design(new float[BandEq.Bands], Rate);
        var z = BandEq.State();
        Assert.Equal(0.5f, flat.Process(0.5f, z), 5);
    }

    [Fact]
    public void ATapReadsTheSamplesItsVoicePlaysAtTheSameMoment()
    {
        var voice = new EngineVoiceState(MachineRegistry.VehicleFor("i4_economy"), Rate, 7) { Interior = true };
        // The voice's channel started 239 samples into its parent's time line, the tap's 1 024 later.
        voice.ChannelClockOffset = 239;
        voice.NoteBlock(4096, 50_000);
        // The tap's block starts at parent time 4096 + 239 + 1024: the voice's next block.
        Assert.True(voice.BlockAt(4096 + 239 + 1024, out long pos));
        Assert.Equal(50_000 + 1024, pos);
        // The same block, whichever channel FMOD called first.
        Assert.True(voice.BlockAt(4096 + 239, out pos));
        Assert.Equal(50_000, pos);
        // Lost: the tap keeps its own clock.
        Assert.False(voice.BlockAt(4096 + 239 + 100_000, out _));
    }
}
