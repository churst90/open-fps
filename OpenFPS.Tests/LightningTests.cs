using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Lightning and its thunder: when and where the storm flashes (the same for everyone, from a seed,
/// at the rates measured storms flash at), what the channel looks like (Hill's 16 degrees), and the
/// laws the thunder follows on its way to the ear: three seconds a kilometre, the air taking the top
/// off, the shadow past which refraction lifts it over your head.
/// </summary>
public class LightningTests
{
    private static readonly StormSky Storm = new(WeatherType.Storm, 1f, new Vector3(12f, 0f, -6f));

    private static List<LightningStrike> Run(int seed, StormSky sky, float seconds, float dt = 0.5f)
    {
        var schedule = new LightningSchedule(seed);
        var strikes = new List<LightningStrike>();
        for (float t = 0f; t < seconds; t += dt) schedule.Advance(dt, sky, strikes);
        return strikes;
    }

    // ── The schedule ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSameSeedFlashesTheSameStorm()
    {
        var a = Run(11, Storm, 3600f);
        var b = Run(11, Storm, 3600f);
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, Run(12, Storm, 3600f));
    }

    [Fact]
    public void AStormFlashesAtAMeasuredStormsRate()
    {
        // Thirty hours of storm: about forty cells, each flashing at its peak rate times sin^2 of its
        // age, which averages a half.
        var strikes = Run(5, Storm, 30f * 3600f, dt: 1f);
        float perMinute = strikes.Count / (30f * 60f);
        float expected = LightningPhysics.StormCellPeakFlashesPerMinute * 0.5f;
        Assert.InRange(perMinute, expected * 0.85f, expected * 1.15f);
        // A quarter reach the ground (Boccippio et al. 2001: 2.94 in the cloud for each one).
        float ground = strikes.Count(s => s.Kind == FlashKind.CloudToGround) / (float)strikes.Count;
        Assert.InRange(ground, LightningPhysics.GroundFlashShare - 0.04f, LightningPhysics.GroundFlashShare + 0.04f);
        // Ground flashes average about four strokes, a quarter single.
        var cg = strikes.Where(s => s.Kind == FlashKind.CloudToGround).ToList();
        Assert.InRange(cg.Average(s => s.Strokes), 3.4, 4.6);
        Assert.InRange(cg.Count(s => s.Strokes == 1) / (double)cg.Count, 0.18, 0.32);
    }

    [Fact]
    public void ClearSkiesAndSnowNeverFlashAndRainOnlyRarely()
    {
        Assert.Empty(Run(3, new StormSky(WeatherType.Clear, 0f, Vector3.Zero), 7200f));
        Assert.Empty(Run(3, new StormSky(WeatherType.Snow, 0.5f, Vector3.Zero), 7200f));
        Assert.Empty(Run(3, new StormSky(WeatherType.Rain, 0.4f, Vector3.Zero), 7200f));
        // The server's rain (0.6): now and then, about one flash in half an hour, against a storm's two a minute.
        var rain = Run(3, new StormSky(WeatherType.Rain, 0.6f, Vector3.Zero), 24f * 3600f, dt: 1f);
        Assert.InRange(rain.Count, 10, 90);
    }

    [Fact]
    public void TheCellDriftsWithTheWindAndMostStrikesAreKilometresAway()
    {
        var schedule = new LightningSchedule(9);
        var strikes = new List<LightningStrike>();
        schedule.Advance(0.5f, Storm, strikes);
        var start = schedule.CellCentre;
        for (int i = 0; i < 1200; i++) schedule.Advance(0.5f, Storm, strikes);
        var moved = schedule.CellCentre - start;
        var wind = Vector3.Normalize(new Vector3(Storm.Wind.X, 0f, Storm.Wind.Z));
        Assert.True(Vector3.Dot(Vector3.Normalize(moved), wind) > 0.99f);
        Assert.InRange(moved.Length(), 600f * 13f, 600f * 14f);   // 13.4 m/s for ten minutes

        var all = Run(21, Storm, 20f * 3600f, dt: 1f).Where(s => s.Kind == FlashKind.CloudToGround).ToList();
        var dist = all.Select(s => new Vector2(s.To.X, s.To.Z).Length()).ToList();
        Assert.True(dist.Count(d => d > 2000f) > dist.Count * 0.8, "most ground strikes are kilometres from the map's centre");
        Assert.True(dist.Count(d => d < 1000f) >= 1, "and now and then one is close");
    }

    [Fact]
    public void AStrikeTravelsAsItsKey()
    {
        foreach (var s in Run(2, Storm, 3600f).Take(20))
        {
            Assert.True(LightningStrike.TryParseKey(s.Key(), out var back));
            Assert.Equal(s.Seed, back.Seed);
            Assert.Equal(s.Kind, back.Kind);
            Assert.Equal(s.Strokes, back.Strokes);
            Assert.True(Vector3.Distance(s.From, back.From) < 0.1f && Vector3.Distance(s.To, back.To) < 0.1f);
            Assert.InRange(back.EnergyPerMetre, s.EnergyPerMetre - 1f, s.EnergyPerMetre + 1f);
        }
        Assert.False(LightningStrike.TryParseKey("thunder:xx:1", out _));
        Assert.False(LightningStrike.TryParseKey("weapon:akm", out _));
        var sound = LightningSystem.SoundFor(Run(2, Storm, 3600f)[0]);
        Assert.True(LightningStrike.TryParseKey(sound.SynthKey, out _));
    }

    // ── The channel ───────────────────────────────────────────────────────────────────────────

    private static LightningStrike Ground(float d, int seed, int strokes = 1)
        => new(seed, FlashKind.CloudToGround, new Vector3(d + 300f, 5000f, -200f), new Vector3(d, 0f, 0f),
               LightningPhysics.GroundFlashEnergyMedian, strokes);

    [Fact]
    public void ChannelDeflectionIsHills()
    {
        var defl = new List<float>(); var ratio = new List<float>();
        for (int i = 0; i < 100; i++)
        {
            var s = Ground(1000f, 100 + i);
            var ch = LightningChannel.Build(s);
            var main = ch.Paths[0];
            // Walked up from the strike point, which is exact, to the top of the channel.
            Assert.Equal(s.To, main[0]);
            Assert.True(Vector3.Distance(main[^1], s.From) <= LightningPhysics.StepMetres + 0.01f);
            Assert.All(ch.Paths, p => Assert.All(p, q => Assert.True(q.Y >= s.To.Y - 1e-3f)));
            defl.Add(ch.MeanDeflectionDegrees());
            float len = 0f;
            for (int k = 1; k < main.Length; k++) len += Vector3.Distance(main[k - 1], main[k]);
            ratio.Add(len / Vector3.Distance(s.From, s.To));
        }
        Assert.InRange(defl.Average(), LightningPhysics.MeanDeflectionDegrees - 1.5, LightningPhysics.MeanDeflectionDegrees + 1.5);
        // About 8 km of channel for a 5 km drop (Lacroix et al. 2019).
        Assert.InRange(ratio.Average(), 1.3, 1.8);
    }

    [Fact]
    public void TheChannelIsTheSameForEveryone()
    {
        var s = Ground(2000f, 77, strokes: 5);
        var a = LightningChannel.Build(s);
        var b = LightningChannel.Build(s);
        Assert.Equal(a.Paths.Count, b.Paths.Count);
        for (int i = 0; i < a.Paths.Count; i++) Assert.Equal(a.Paths[i], b.Paths[i]);
        Assert.Equal(a.StrokeTimes, b.StrokeTimes);
        Assert.Equal(5, a.StrokeTimes.Length);
        Assert.True(a.StrokeTimes.Zip(a.StrokeTimes.Skip(1)).All(p => p.Second > p.First));
    }

    // ── The thunder's laws ────────────────────────────────────────────────────────────────────

    [Fact]
    public void FewsPeakFrequency()
    {
        // Rakov and Uman's worked example: 1e6 J/m, about 68 Hz.
        Assert.InRange(LightningPhysics.PeakFrequencyHz(1e6f), 67f, 69f);
        Assert.InRange(LightningPhysics.PeakFrequencyHz(LightningPhysics.GroundFlashEnergyMedian), 135f, 148f);
        // Four times the energy, half the frequency, a wave twice as long.
        Assert.Equal(2f, LightningPhysics.NWaveSeconds(4e5f) / LightningPhysics.NWaveSeconds(1e5f), 3);
    }

    [Fact]
    public void ThreeSecondsAKilometre()
    {
        // A straight vertical channel, so the nearest point is its foot: the thunder starts after
        // that distance over the speed of sound at the air's temperature, and not before.
        var air = new Thunder.Air(15f, 0.8f, 1013.25f, Vector3.Zero);
        float c = AudioPhysics.SpeedOfSoundAt(15f);
        Assert.InRange(1000f / c, 2.9f, 3.0f);
        foreach (float d in new[] { 300f, 1000f, 3000f })
        {
            var (strike, channel) = Straight(d);
            var parts = Thunder.Render(strike, channel, new Vector3(0f, 1.7f, 0f), air,
                                       new Thunder.Options { Ground = false, Plain = true, MaxParts = 1, Threads = 2, Turbulence = false });
            var part = Assert.Single(parts);
            int first = Array.FindIndex(part.Pressure, v => MathF.Abs(v) > part.PeakPa * 0.05f);
            float heard = part.StartSeconds + first / (float)part.SampleRate;
            float nearest = MathF.Sqrt(d * d + 1.7f * 1.7f);
            Assert.InRange(heard, nearest / c - 0.004f, nearest / c + 0.004f);
        }
    }

    [Fact]
    public void TheAirTakesTheTopOffWithDistance()
    {
        var air = new Thunder.Air(15f, 0.8f, 1013.25f, Vector3.Zero);
        float Centroid(float d)
        {
            var (strike, channel) = Straight(d);
            var parts = Thunder.Render(strike, channel, new Vector3(0f, 1.7f, 0f), air,
                                       new Thunder.Options { Ground = false, Plain = true, MaxParts = 1, SampleRate = 24000, Threads = 2, Turbulence = false });
            var p = parts[0];
            var bands = Spectrum.BandEnergy(p.Pressure, p.SampleRate, 4096);
            double num = 0, den = 0;
            for (int i = 0; i < bands.Length; i++)
            {
                double mid = Math.Sqrt(Spectrum.BandEdges[i] * Spectrum.BandEdges[i + 1]);
                num += mid * bands[i]; den += bands[i];
            }
            return (float)(num / den);
        }
        float c300 = Centroid(300f), c3000 = Centroid(3000f), c10000 = Centroid(10000f);
        Assert.True(c300 > c3000 && c3000 > c10000, $"centroids {c300:F0}, {c3000:F0}, {c10000:F0} Hz");
    }

    [Fact]
    public void FurtherIsQuieterAndTheFarFieldIsSpherical()
    {
        // A wind from the channel toward the listener, so refraction's shadow is out of it: spreading
        // and the air alone.
        var air = new Thunder.Air(15f, 0.8f, 1013.25f, new Vector3(-15f, 0f, 0f));
        float Peak(float d)
        {
            var (strike, channel) = Straight(d);
            return Thunder.Render(strike, channel, new Vector3(0f, 1.7f, 0f), air,
                                  new Thunder.Options { Ground = false, Plain = true, MaxParts = 1, Threads = 2, Turbulence = false })[0].PeakPa;
        }
        float p1 = Peak(1000f), p3 = Peak(3000f), p9 = Peak(9000f);
        Assert.True(p1 > p3 && p3 > p9);
        // Each threefold distance takes between a cylinder's 4.8 dB and a sphere's 9.5, plus the air.
        float db13 = 20f * MathF.Log10(p1 / p3), db39 = 20f * MathF.Log10(p3 / p9);
        Assert.InRange(db13, 4f, 14f);
        Assert.InRange(db39, 4f, 16f);
    }

    [Fact]
    public void ARealStrikeIsInTheRangeMeasured()
    {
        // HyMeX (Lacroix's thesis 2018, sec. 3): ground strokes 2-10 Pa at 2-4 km.
        var air = new Thunder.Air(15f, 0.8f, 1013.25f, Vector3.Zero);
        var parts = Thunder.Render(Ground(3000f, 7, strokes: 4), new Vector3(0f, 1.7f, 0f), air, new Thunder.Options { Threads = 2 });
        Assert.InRange(parts.Max(p => p.PeakPa), 1.5f, 15f);
    }

    [Fact]
    public void TheShadowLiftsFarThunderOverYourHead()
    {
        // Fleagle (1949): a 5 km source's shadow starts about 30 km out in still standard air.
        Assert.InRange(Thunder.ShadowEdgeMetres(5000f, 15f), 28000f, 31000f);
        var air = new Thunder.Air(15f, 0.8f, 1013.25f, Vector3.Zero);
        var ear = new Vector3(0f, 1.7f, 0f);
        Assert.Equal(1f, Thunder.ShadowGain(new Vector3(20000f, 5000f, 0f), ear, 0f, air, 340f));
        float deep = Thunder.ShadowGain(new Vector3(50000f, 5000f, 0f), ear, 0f, air, 340f);
        Assert.Equal(-Thunder.ShadowMaxDb, 20f * MathF.Log10(deep), 2);
        // Wind blowing from the source toward the listener bends the sound down: no shadow there.
        var downwind = air with { Wind = new Vector3(-15f, 0f, 0f) };
        Assert.Equal(1f, Thunder.ShadowGain(new Vector3(35000f, 5000f, 0f), ear, 0f, downwind, 340f));
        var upwind = air with { Wind = new Vector3(15f, 0f, 0f) };
        Assert.True(Thunder.ShadowGain(new Vector3(20000f, 5000f, 0f), ear, 0f, upwind, 340f) < 1f);
    }

    [Fact]
    public void LengtheningIsSlow()
    {
        float c = 340f;
        float t0 = LightningPhysics.NWaveSeconds(LightningPhysics.GroundFlashEnergyMedian, c);
        float a2 = Thunder.AmplitudeAt2m(LightningPhysics.GroundFlashEnergyMedian, c);
        float t1 = Thunder.LengthenedSeconds(t0, a2, 1000f, c), t10 = Thunder.LengthenedSeconds(t0, a2, 10000f, c);
        Assert.True(t0 < t1 && t1 < t10);
        Assert.InRange(t10 / t0, 1.02f, 1.2f);
    }

    [Fact]
    public void EveryClientRendersTheSameThunder()
    {
        var air = new Thunder.Air(12f, 0.9f, 1013.25f, new Vector3(8f, 0f, 3f));
        var s = Ground(1500f, 31, strokes: 3);
        var a = Thunder.Render(s, new Vector3(5f, 1.7f, 5f), air, new Thunder.Options { Threads = 3 });
        var b = Thunder.Render(s, new Vector3(5f, 1.7f, 5f), air, new Thunder.Options { Threads = 3 });
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].StartSeconds, b[i].StartSeconds);
            Assert.Equal(a[i].Pressure, b[i].Pressure);
        }
    }

    [Fact]
    public void APartIsPlacedAtItsPeakAtTheEar()
    {
        // Placed 40 m out, declared at its peak carried out there: the law gives back that peak at the ear.
        Assert.Equal(40f, OpenFPS.Client.Core.WorldAudioPlayer.SkyProxyMetres);
        Assert.Equal(100f + 20f * MathF.Log10(40f), OpenFPS.Client.Core.WorldAudioPlayer.SkyLevelDb(100f), 3);
    }

    // ── Round 2: the rumble does not break up ────────────────────────────────────────────────

    /// <summary>Runs of near silence (under -90 dB of the peak) of 5 ms or more, between the first and
    /// last sound: what Cody heard as "crackly and breaks up" in round 1 (up to 361 in a cloud flash).
    /// One running to the end of the buffer is its end, not a gap.</summary>
    private static int Gaps(Thunder.Part p)
    {
        float floor = p.PeakPa * MathF.Pow(10f, -90f / 20f);
        int first = Array.FindIndex(p.Pressure, v => MathF.Abs(v) > floor);
        int last = Array.FindLastIndex(p.Pressure, v => MathF.Abs(v) > floor);
        int gaps = 0, run = 0, min = (int)(0.005f * p.SampleRate);
        for (int i = first; i <= last; i++)
        {
            if (MathF.Abs(p.Pressure[i]) < floor) run++;
            else { if (run >= min) gaps++; run = 0; }
        }
        return gaps;
    }

    private static readonly Thunder.Air StormAir = new(15f, 0.95f, 1013.25f, new Vector3(15f, 0f, -10f), 0.8f);

    [Fact]
    public void TheRumbleHasNoHolesInIt()
    {
        var ear = new Vector3(0f, 1.7f, 0f);
        var cloud = new LightningStrike(7, FlashKind.IntraCloud, new Vector3(-3000f, 6000f, 8000f), new Vector3(3000f, 9000f, 8000f),
                                        LightningPhysics.GroundFlashEnergyMedian * LightningPhysics.CloudFlashEnergyShare, 1);
        foreach (var strike in new[] { Ground(3000f, 7, strokes: 4), cloud })
            foreach (var part in Thunder.Render(strike, ear, StormAir, new Thunder.Options { Threads = 2 }))
                Assert.Equal(0, Gaps(part));
    }

    [Fact]
    public void TurbulenceScattersTheTopFirstAndFurtherOffMore()
    {
        float c = 340f;
        float mu2 = Thunder.TurbulenceVariance(StormAir, c);
        Assert.InRange(mu2, 5e-6f, 5e-5f);
        // Chernov: the coherent wave goes as k^2, so ten times the frequency is a hundred times the loss.
        Assert.Equal(100f, Thunder.ScatterPerMetre(1000f, mu2, c) / Thunder.ScatterPerMetre(100f, mu2, c), 2);
        // The spread grows as the square of the distance, to a ceiling.
        Assert.Equal(4f, Thunder.ScatterSpreadSeconds(2000f, mu2, c) / Thunder.ScatterSpreadSeconds(1000f, mu2, c), 2);
        Assert.Equal(Thunder.MaxScatterSeconds, Thunder.ScatterSpreadSeconds(1e6f, mu2, c));
        // A crack 100 m off is still a crack; a kilometre off its top is half scattered; from 8 km the
        // rumble's own octaves are.
        Assert.True(Thunder.ScatteredShare(2000f, 100f, mu2, c) < 0.1f);
        Assert.InRange(Thunder.ScatteredShare(1000f, 1000f, mu2, c), 0.4f, 0.9f);
        Assert.True(Thunder.ScatteredShare(125f, 8000f, mu2, c) > 0.8f);
        // Calm air scatters less than a storm.
        Assert.True(Thunder.TurbulenceVariance(Thunder.Air.Standard, c) < mu2 / 5f);
    }

    [Fact]
    public void ScatteringMovesEnergyItDoesNotMakeIt()
    {
        var ear = new Vector3(0f, 1.7f, 0f);
        double Energy(bool turbulence)
        {
            double sum = 0;
            foreach (var p in Thunder.Render(Ground(5000f, 9, strokes: 2), ear, StormAir, new Thunder.Options { Threads = 2, Turbulence = turbulence, MaxParts = 1 }))
                foreach (float v in p.Pressure) sum += (double)v * v / p.SampleRate;
            return sum;
        }
        double db = 10 * Math.Log10(Energy(true) / Energy(false));
        Assert.InRange(db, -1.5, 1.5);
    }

    [Fact]
    public void ThunderIsNeverRenderedBelow24kHz()
    {
        // The mixer resamples linearly; from 12 kHz its images land across the top.
        Assert.Equal(24000, Thunder.ChooseRate(20000f, Thunder.Air.Standard));
        Assert.Equal(48000, Thunder.ChooseRate(50f, Thunder.Air.Standard));
    }

    private static (LightningStrike Strike, LightningChannel Channel) Straight(float d)
    {
        var strike = new LightningStrike(1, FlashKind.CloudToGround, new Vector3(d, 5000f, 0f), new Vector3(d, 0f, 0f),
                                         LightningPhysics.GroundFlashEnergyMedian, 1);
        var ch = new LightningChannel();
        var line = new List<Vector3>();
        for (float y = 0f; y <= 5000f; y += LightningPhysics.StepMetres) line.Add(new Vector3(d, y, 0f));
        ch.Paths.Add(line.ToArray());
        ch.EnergyShares.Add(1f);
        return (strike, ch);
    }
}
