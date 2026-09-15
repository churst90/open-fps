using System;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The engine synthesis, checked against the physics it claims rather than against a recording:
/// firing patterns, pipe acoustics, and the behaviour of the whole machine on a bench.
/// </summary>
public class EngineSynthTests
{
    private const int Sr = 44100;

    // ── Firing patterns ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CrossPlaneV8_EachBankFiresUnevenly()
    {
        var e = EngineProfile.V8MuscleBigBlock;
        Assert.Equal("1-8-4-3-6-5-7-2", e.FiringOrder);
        // Every bank sees the famous 90/180/180/270 in some rotation — that unevenness is the burble.
        for (int g = 0; g < 2; g++)
        {
            var intervals = e.GroupIntervals(g).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 90f, 180f, 180f, 270f }, intervals);
        }
    }

    [Fact]
    public void FlatPlaneV8_EachBankFiresEvenly()
    {
        var e = EngineProfile.V8FlatPlane;
        for (int g = 0; g < 2; g++)
            Assert.All(e.GroupIntervals(g), i => Assert.Equal(180f, i));
    }

    [Fact]
    public void VTwin_FiresAt315And405()
    {
        var e = EngineProfile.VTwin45;
        Assert.Equal(new[] { 0f, 315f }, e.FiringAngles);
    }

    [Fact]
    public void EveryPreset_FiringAnglesCoverTheCycleOnce()
    {
        foreach (var (key, make) in EngineProfile.Presets)
        {
            var e = make();
            Assert.Equal(e.Cylinders, e.FiringAngles.Length);
            Assert.Equal(e.Cylinders, e.Bank.Length);
            Assert.All(e.FiringAngles, a => Assert.InRange(a, 0f, e.CycleDegrees - 1e-3f));
            Assert.Equal(e.Cylinders, e.FiringAngles.Distinct().Count());
            Assert.True(e.OverlapDegrees >= 0f && e.OverlapDegrees < 150f, $"{key}: overlap {e.OverlapDegrees}");
        }
    }

    // ── Pipe acoustics ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Junction_ConservesEnergy_WhenLossless()
    {
        // Kelly-Lochbaum: incoming power equals outgoing power at a lossless junction.
        float[] a = { 1f, -0.5f, 0.25f };
        float[] y = { 1f, 2f, 0.5f };
        var b = new float[3];
        Junction.Scatter(a, y, b, 0f);
        float pin = 0f, pout = 0f;
        for (int i = 0; i < 3; i++) { pin += y[i] * a[i] * a[i]; pout += y[i] * b[i] * b[i]; }
        Assert.Equal(pin, pout, 4);
    }

    [Fact]
    public void Junction_LosesEnergy_WithFlowLoss()
    {
        float[] a = { 1f, -0.5f, 0.25f };
        float[] y = { 1f, 2f, 0.5f };
        var b = new float[3];
        Junction.Scatter(a, y, b, 0.5f);
        float pin = 0f, pout = 0f;
        for (int i = 0; i < 3; i++) { pin += y[i] * a[i] * a[i]; pout += y[i] * b[i] * b[i]; }
        Assert.True(pout < pin);
    }

    [Fact]
    public void WaveLine_IsAPlainDelay_WithoutSteepening()
    {
        var w = new WaveLine(64);
        w.SetDelay(10f);
        w.SetSteepening(0f);
        var got = new List<float>();
        for (int i = 0; i < 40; i++)
        {
            got.Add(w.Read());
            w.Write(i == 5 ? 1f : 0f);
        }
        // The impulse written at step 5 comes out ten steps later; nothing else does.
        int peakAt = got.IndexOf(got.Max());
        Assert.Equal(15, peakAt);
        Assert.Equal(1f, got.Sum(), 3);
    }

    [Fact]
    public void WaveLine_Steepening_BringsCrestsForwardAndKeepsTroughsBack()
    {
        // A slow half-sine of +2 kPa arrives earlier than the same wave at -2 kPa when the line
        // steepens — crests travel faster than troughs. That is finite-amplitude acoustics.
        float Centroid(float sign)
        {
            var w = new WaveLine(200);
            w.SetDelay(100f);
            w.SetSteepening(1.165f / (1.33f * 101325f) * 6f);   // exaggerated, to make it measurable
            double num = 0, den = 0;
            for (int i = 0; i < 300; i++)
            {
                float v = w.Read();
                num += i * MathF.Abs(v); den += MathF.Abs(v);
                float x = i < 40 ? sign * 20000f * MathF.Sin(MathF.PI * i / 40f) : 0f;
                w.Write(x);
            }
            return (float)(num / Math.Max(1e-9, den));
        }
        Assert.True(Centroid(+1f) < Centroid(-1f) - 3f);
    }

    [Fact]
    public void OpenEnd_ReflectsLowFrequenciesInverted_AndLetsHighOnesOut()
    {
        var end = new OpenEnd(Sr);
        end.Configure(radius: 0.03f, soundSpeed: 450f, density: 0.6f, area: 0.0028f, mach: 0f);
        float Response(float hz)
        {
            double sumIn = 0, sumOut = 0;
            for (int i = 0; i < 8000; i++)
            {
                float x = MathF.Sin(2f * MathF.PI * hz * i / Sr);
                var (r, _) = end.Process(x);
                if (i > 2000) { sumIn += x * x; sumOut += r * r; }
            }
            return (float)Math.Sqrt(sumOut / sumIn);
        }
        Assert.InRange(Response(100f), 0.95f, 1.01f);      // a mirror at engine frequencies
        Assert.InRange(Response(6000f), 0.05f, 0.5f);      // transparent up top
        // And the low-frequency reflection is inverted.
        float x0 = 0f, r0 = 0f;
        for (int i = 0; i < 4000; i++) { x0 = MathF.Sin(2f * MathF.PI * 100f * i / Sr); (r0, _) = end.Process(x0); }
        Assert.True(MathF.Sign(x0) != MathF.Sign(r0) || MathF.Abs(x0) < 0.05f);
    }

    [Fact]
    public void ExpansionChamber_AttenuatesAtItsQuarterWave_MostForALargerCan()
    {
        // Munjal: TL = 10 log(1 + (m - 1/m)^2 sin^2(kL) / 4) — peaks at kL = pi/2, larger for a
        // larger area ratio m. Built from two junctions and a chamber pipe, fed from a long pipe and
        // read at the far end of another long pipe, with neither end ever reflecting: what arrives is
        // the chamber's transmission alone, internal reflections included.
        float Transmission(float m, float hz)
        {
            const float area = 0.003f, L = 0.25f, kelvin = 500f;
            var inPipe = new Pipe(6f, area, Sr, 0.2f, 0f);
            var chamber = new Pipe(L, area * m, Sr, 0.2f, 0f);
            var outPipe = new Pipe(6f, area, Sr, 0.2f, 0f);
            foreach (var p in new[] { inPipe, chamber, outPipe }) p.SetGas(kelvin, Gas.GammaExhaust, 0f);
            double sum = 0; int count = 0;
            for (int i = 0; i < 4000; i++)
            {
                inPipe.PushForward(MathF.Sin(2f * MathF.PI * hz * i / Sr));
                var (backA, onC) = Junction.Two(inPipe.ArriveFar(), chamber.ArriveNear(), inPipe.Admittance, chamber.Admittance, 0f);
                inPipe.PushBackward(backA);
                chamber.PushForward(onC);
                var (backC, onB) = Junction.Two(chamber.ArriveFar(), outPipe.ArriveNear(), chamber.Admittance, outPipe.Admittance, 0f);
                chamber.PushBackward(backC);
                outPipe.PushForward(onB);
                float y = outPipe.ArriveFar();
                inPipe.ArriveNear();                          // swallowed: no source-end reflection
                outPipe.PushBackward(0f);                     // no far-end reflection
                if (i > 2500) { sum += y * y; count++; }
            }
            return (float)Math.Sqrt(sum / count);
        }
        float c = Gas.SoundSpeed(500f, Gas.GammaExhaust);
        float quarterWave = c / (4f * 0.25f);
        float small = Transmission(3f, quarterWave);
        float big = Transmission(9f, quarterWave);
        float passband = Transmission(9f, quarterWave * 2f);   // kL = pi: a chamber is transparent here
        Assert.True(big < small * 0.6f, $"m=9 should attenuate more than m=3: {big} vs {small}");
        Assert.True(passband > big * 2f, $"half-wave should pass: {passband} vs {big}");
    }

    // ── The whole engine ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPreset_StartsIdlesAndRevs_WithoutBlowingUp()
    {
        foreach (var (key, make) in VehicleProfile.Presets)
        {
            var v = make();
            var orders = new List<DriveOrder>
            {
                new(DriverAction.Cranking, 0.6f),
                new(DriverAction.Idling, 3.0f),
                new(DriverAction.Revving, 1.5f, v.Engine.RedlineRpm * 0.8f),
                new(DriverAction.Idling, 1.0f),
            };
            var r = VehicleSynth.Render(v, orders, seed: 3);
            Assert.All(r.Exhaust, s => Assert.True(float.IsFinite(s), $"{key}: non-finite exhaust"));
            Assert.All(r.Intake, s => Assert.True(float.IsFinite(s), $"{key}: non-finite intake"));
            // It caught: the last second of the first idle sits within 60% of the idle speed.
            int a = r.OrderStart[1] + Sr * 2, b = r.OrderStart[2];
            float mean = 0f; for (int i = a; i < b; i++) mean += r.Rpm[i]; mean /= (b - a);
            Assert.InRange(mean, v.Engine.IdleRpm * 0.6f, v.Engine.IdleRpm * 1.9f);
            // And it revved.
            float peak = 0f; for (int i = r.OrderStart[2]; i < r.OrderStart[3]; i++) peak = MathF.Max(peak, r.Rpm[i]);
            Assert.True(peak > v.Engine.RedlineRpm * 0.55f, $"{key}: only reached {peak}");
            // Loud enough to be an engine, and no louder than that kind of engine really is at a
            // metre. The ceiling has to know about the muffler: a road car with a can on it is 95-115
            // and anything past about 128 is a bug, but an unsilenced race engine measures 130-140 at
            // the pipe mouth and capping it at a road car's figure would be asserting the wrong thing.
            bool silenced = v.Engine.Exhaust.Muffler.Kind != MufflerKind.None;
            float ceiling = silenced ? 128f : 142f;
            Assert.True(r.ExhaustDb >= 55f && r.ExhaustDb <= ceiling,
                $"{key}: {r.ExhaustDb:F1} dB at 1 m, outside 55-{ceiling:F0} for a"
                + (silenced ? " muffled" : "n unsilenced") + " engine");
        }
    }

    /// <summary>
    /// Every preset must declare the level it actually measures, and must not peak so far above it
    /// that the synthesis clips.
    ///
    /// This is the test that would have caught the speedway sounding "overloaded, crackling and
    /// breaking up". VehicleProfile.SourceLevelDb decides two things — where the emitter is placed,
    /// and what one full-scale sample means inside EngineVoiceState — and the second is unforgiving:
    /// anything past the reference goes through a tanh. An unsilenced V10 peaks thirteen times over a
    /// road car's reference, so with one shared number it did not arrive loud, it arrived square.
    ///
    /// Both halves are asserted. The declared level has to match the measurement, or placement is
    /// wrong. And the measured PEAK has to sit inside the headroom, or the waveform is clipped
    /// however right the declared level is.
    /// </summary>
    [Fact]
    public void DeclaredSourceLevelMatchesWhatThePresetMeasures()
    {
        foreach (var (key, make) in VehicleProfile.Presets)
        {
            var v = make();
            var r = VehicleSynth.Render(v, new List<DriveOrder>
            {
                new(DriverAction.Cranking, 0.5f),
                new(DriverAction.Idling, 1.2f),
                new(DriverAction.Holding, 3.5f, v.Engine.RedlineRpm * 0.85f, 1f),
            }, seed: 5);

            Assert.True(MathF.Abs(r.ExhaustDb - v.SourceLevelDb) <= 3.5f,
                $"{key}: declares {v.SourceLevelDb:F0} dB but measures {r.ExhaustDb:F1} — "
                + "re-measure with `--engine-levels` and update the preset");

            // Two thresholds, because they mean different things. What the waveform does over and
            // over (the 99.9th percentile) must fit inside the headroom, or the engine is clipped.
            // The single largest sample is allowed well past it — that is a backfire, and a tanh
            // rounding a backfire is a limiter doing its job.
            var amps = new List<float>(r.Exhaust.Length - r.OrderStart[2]);
            for (int i = r.OrderStart[2]; i < r.Exhaust.Length; i++)
                amps.Add(MathF.Abs(r.Exhaust[i] + r.Intake[i] * 0.35f) * r.PascalsPerUnit);
            amps.Sort();
            float sustained = amps[(int)(amps.Count * 0.999f)];
            float peak = amps[^1];
            float reference = v.PascalsAtFullScale;

            float sustainedOver = 20f * MathF.Log10(MathF.Max(1e-6f, sustained) / reference);
            Assert.True(sustainedOver <= 1f,
                $"{key}: sustained level is {sustainedOver:F1} dB over full scale — the tanh will "
                + $"square the waveform, not round a transient. Raise VehicleProfile.PeakHeadroomDb "
                + $"or re-measure with `--engine-levels`.");

            float peakOver = 20f * MathF.Log10(MathF.Max(1e-6f, peak) / reference);
            Assert.True(peakOver <= 8f,
                $"{key}: peaks {peakOver:F1} dB over full scale, which is past what a soft knee "
                + $"can absorb ({20f * MathF.Log10(peak / 20e-6f):F0} dB SPL against a "
                + $"{20f * MathF.Log10(reference / 20e-6f):F0} dB reference).");
        }
    }

    // ── The live voice: how it starts and how it stops ──────────────────────────────────────────

    /// <summary>
    /// A voice placed at speed must already BE at that speed, not chase it.
    ///
    /// A car entering the listener's voice budget at three hundred kilometres an hour used to start
    /// from a dead engine and a stopped driveline, and the virtual driver then floored it to catch
    /// up — an entire spin-up compressed into the eighty milliseconds the speed filter takes. Heard
    /// once per car per pass, that is the "slight popping as they drive around".
    /// </summary>
    [Fact]
    public void AVoicePlacedAtSpeedStartsAtTheRightRevsRatherThanSpinningUp()
    {
        var v = VehicleProfile.StockCar;
        float speed = 280f / 3.6f;
        var voice = new EngineVoiceState(v, 48000f, 3) { TargetSpeed = speed };
        voice.PlaceAtSpeed(speed);

        float expected = v.Gearbox.RpmFor(speed, voice.Driveline.Gear);
        Assert.InRange(voice.Engine.Rpm, expected * 0.9f, expected * 1.1f);
        Assert.True(voice.Engine.Rpm > v.Engine.IdleRpm * 3f,
            $"placed at {speed * 3.6f:F0} km/h but the crank is only turning {voice.Engine.Rpm:F0} rpm");

        // And it must not then swoop: half a second in, the revs are still where they started.
        var buf = new float[512];
        for (int i = 0; i < 48; i++) voice.Render(buf);
        Assert.InRange(voice.Engine.Rpm, expected * 0.75f, expected * 1.25f);
    }

    /// <summary>
    /// The mixer callback must never synthesize and never wait, whatever state the ring is in.
    ///
    /// This is the fault that made a map load cut out for seconds, and it is invisible from the
    /// outside: Consume used to take the producer's lock and, holding it, render whatever the worker
    /// had not got to yet. The producer held that same lock for a WHOLE top-up — a warm-up plus up
    /// to seven hundred milliseconds of audio, which at load-time speed is around a hundred and
    /// seventy-five milliseconds of wall clock — and FMOD's buffer is ninety-three. So the deeper
    /// the buffer got, the longer the mixer could be frozen by it, which is why every attempt to fix
    /// this with buffer depth made it worse.
    ///
    /// What the callback must do instead is take what is there and ramp out of the rest. The test
    /// drains the ring dry and asks for another block anyway: it has to come back promptly, and it
    /// has to come back without the engine having been advanced a single sample.
    /// </summary>
    [Fact]
    public void TheMixerNeverSynthesizesAndNeverWaitsOnTheProducer()
    {
        var v = VehicleProfile.StockCar;
        float speed = 240f / 3.6f;
        var voice = new EngineVoiceState(v, 48000f, 5) { TargetSpeed = speed };
        voice.PlaceAtSpeed(speed);

        // Cold: not primed, so the block is silence and the crank has not moved. The position still
        // advances, because the voice keeps wall clock whether or not it has audio to play.
        var buf = new float[512];
        float rpmBefore = voice.Engine.Rpm;
        long playedBefore = voice.Played;
        voice.Consume(buf);
        Assert.Equal(0f, Rms(buf), 6);
        Assert.Equal(rpmBefore, voice.Engine.Rpm);
        Assert.Equal(playedBefore + buf.Length, voice.Played);

        // Prime it, and drain the ring dry a block at a time.
        voice.Produce();
        Assert.True(voice.Primed, "the producer did not prime the voice");
        int blocks = 0;
        while (voice.Lead >= buf.Length && blocks < 4096) { voice.Consume(buf); blocks++; }
        Assert.True(blocks > 0, "the producer rendered nothing to consume");

        // Now the ring is short of a block and no producer is running. The call must come back
        // anyway, without integrating anything itself.
        rpmBefore = voice.Engine.Rpm;
        int starvesBefore = voice.Starves;
        playedBefore = voice.Played;
        voice.Consume(buf);

        Assert.Equal(rpmBefore, voice.Engine.Rpm);
        Assert.True(voice.Starves > starvesBefore, "a starved block was not reported");
        // Ramped out of the last sample, not stepped off it, and silent by the end of the block.
        Assert.Equal(0f, buf[^1], 6);
        // And — the part that matters — the voice did not fall behind. A starved block is a GAP.
        Assert.Equal(playedBefore + buf.Length, voice.Played);
    }

    /// <summary>
    /// A voice that starves must lose audio, never lose TIME.
    ///
    /// Taking only the samples that were there and keeping your place is the obvious thing to write,
    /// and it does not sound like a dropout at all: a voice handed 900 samples of a 1024-sample block
    /// that keeps its place is playing at 88 % speed — a tone and a half flat — and thirty cars all
    /// doing it sounds like the whole field winding down together. It also walks each car's sound
    /// further and further behind where the car actually is, which smears a grid into a wash. Both
    /// were heard on the speedway, and neither is recognisable as missing audio.
    ///
    /// So: starve a voice repeatedly and check that its play position still tracks the number of
    /// samples the mixer asked for, exactly.
    /// </summary>
    [Fact]
    public void AStarvedVoiceLosesAudioButNeverFallsBehindWallClock()
    {
        var v = VehicleProfile.StockCar;
        float speed = 260f / 3.6f;
        var voice = new EngineVoiceState(v, 48000f, 13) { TargetSpeed = speed };
        voice.PlaceAtSpeed(speed);
        voice.Produce();

        var buf = new float[1024];
        long start = voice.Played;
        const int blocks = 600;                 // ~12.8 s of wall clock at 48 kHz
        for (int i = 0; i < blocks; i++)
        {
            voice.Consume(buf);
            // Top up only occasionally, so most blocks come up short — the load condition.
            if (i % 40 == 0) voice.Produce();
        }

        Assert.True(voice.Starves > 0, "the voice was never actually starved, so this proves nothing");
        Assert.Equal(start + (long)blocks * buf.Length, voice.Played);

        // And once the producer is allowed to catch up, the voice is making a sound again rather
        // than sitting in the hole it dug.
        for (int i = 0; i < 4; i++) voice.Produce();
        voice.Consume(buf);
        Assert.True(Rms(buf) > 1e-4f, "the voice never recovered after starving");
    }

    /// <summary>
    /// A voice that was fading and then wins its slot back must come BACK.
    ///
    /// Fading out is half a mechanism; without the other half a car that loses its slot for a moment
    /// and immediately regains it keeps its engine, keeps its position, keeps being updated every
    /// frame — and is silent for the rest of its life. In a field where cars trade places constantly
    /// that happens to one car after another, so the traffic thins out from the inside while the
    /// distant pack still circulates. It is a silent failure in the most literal sense.
    /// </summary>
    [Fact]
    public void AFadingVoiceComesBackWhenItIsWanted()
    {
        var v = VehicleProfile.StockCar;
        float speed = 250f / 3.6f;
        var voice = new EngineVoiceState(v, 48000f, 9) { TargetSpeed = speed };
        voice.PlaceAtSpeed(speed);

        var buf = new float[512];
        for (int i = 0; i < 40; i++) voice.Render(buf);
        float loud = Rms(buf);
        Assert.True(loud > 1e-4f, "the voice was not making a sound to begin with");

        // Start a fade, let it get most of the way down, then change our mind.
        voice.TargetEnvelope = 0f;
        for (int i = 0; i < 6; i++) voice.Render(buf);
        Assert.True(Rms(buf) < loud * 0.5f, "the fade did not take hold");

        voice.Revive();
        for (int i = 0; i < 40; i++) voice.Render(buf);

        Assert.False(voice.FadedOut, "a revived voice still reports itself faded out");
        Assert.True(Rms(buf) > loud * 0.5f,
            $"the voice never came back: {Rms(buf):F5} against {loud:F5} before the fade");
    }

    private static float Rms(float[] b)
    {
        double sum = 0;
        foreach (float x in b) sum += (double)x * x;
        return MathF.Sqrt((float)(sum / b.Length));
    }

    /// <summary>
    /// Stopping a voice must be a fade, not a cut.
    ///
    /// There is no zero-crossing to stop a synthesized engine on — the waveform is wherever the crank
    /// happens to be — so releasing the voice mid-cycle leaves a step, and a step is a click. On a
    /// track where cars trade places in the voice budget every few seconds, that is a click every few
    /// seconds. This asserts the envelope actually reaches silence and that nothing on the way there
    /// is a bigger jump than the engine itself was already making.
    /// </summary>
    [Fact]
    public void AVoiceFadesToSilenceWithoutAStepInTheWaveform()
    {
        var v = VehicleProfile.StockCar;
        float speed = 260f / 3.6f;
        var voice = new EngineVoiceState(v, 48000f, 5) { TargetSpeed = speed };
        voice.PlaceAtSpeed(speed);

        var buf = new float[512];
        // Settle, and learn how big a step this engine takes all by itself.
        for (int i = 0; i < 40; i++) voice.Render(buf);
        float natural = 0f;
        float prev = 0f;
        for (int i = 0; i < 40; i++)
        {
            voice.Render(buf);
            foreach (float x in buf) { natural = MathF.Max(natural, MathF.Abs(x - prev)); prev = x; }
        }

        voice.TargetEnvelope = 0f;
        float worst = 0f;
        bool silent = false;
        for (int i = 0; i < 200 && !silent; i++)
        {
            voice.Render(buf);
            foreach (float x in buf) { worst = MathF.Max(worst, MathF.Abs(x - prev)); prev = x; }
            silent = voice.FadedOut;
        }

        Assert.True(silent, "the voice never reported itself faded out");
        Assert.True(MathF.Abs(prev) < 1e-3f, $"faded out but the last sample is {prev:F4}");
        // The fade may not introduce a discontinuity larger than the signal's own slew.
        Assert.True(worst <= natural * 1.05f + 1e-4f,
            $"fading stepped by {worst:F4}, against a natural slew of {natural:F4}");
    }

    [Fact]
    public void FullLoad_IsMuchLouderThanIdle_AndPulsesAreBiggerInThePipe()
    {
        var v = VehicleProfile.V8Muscle;
        var idle = VehicleSynth.Render(v, new List<DriveOrder> { new(DriverAction.Cranking, 0.5f), new(DriverAction.Idling, 5f) }, 3);
        var load = VehicleSynth.Render(v, new List<DriveOrder>
        {
            new(DriverAction.Cranking, 0.5f), new(DriverAction.Idling, 1.5f),
            new(DriverAction.Holding, 4f, 3500f, 1f),
        }, 3);
        // Measured on real cars: about 25-30 dB between idle and full load at speed.
        Assert.True(load.ExhaustDb - idle.ExhaustDb > 12f, $"idle {idle.ExhaustDb} dB, load {load.ExhaustDb} dB");
        string idleLine = idle.Log.Last(l => l.Contains("port peak"));
        string loadLine = load.Log.Last(l => l.Contains("port peak"));
        float PortPeak(string l) => float.Parse(l[(l.IndexOf("port peak") + 10)..].Split(' ')[0]);
        // Blair, Ricardo: ~0.1 bar idling, 0.5-1 bar at full load.
        Assert.InRange(PortPeak(idleLine), 0.02f, 0.3f);
        Assert.InRange(PortPeak(loadLine), 0.35f, 1.5f);
    }

    [Fact]
    public void BigCam_IdlesRougherThanStockCam()
    {
        float Roughness(float duration)
        {
            var baseE = EngineProfile.V8SportsFlowmaster40;
            var e = baseE with
            {
                ExhaustCam = baseE.ExhaustCam with { DurationDegrees = duration },
                IntakeCam = baseE.IntakeCam with { DurationDegrees = duration - 4f },
            };
            var v = VehicleProfile.V8Sports with { Engine = e };
            var r = VehicleSynth.Render(v, new List<DriveOrder> { new(DriverAction.Cranking, 0.5f), new(DriverAction.Idling, 6f) }, 5);
            // Cycle-to-cycle variation of the exhaust energy over the last three seconds.
            int from = r.Exhaust.Length - Sr * 3;
            float rpm = 0f; for (int i = from; i < r.Rpm.Length; i++) rpm += r.Rpm[i]; rpm /= Sr * 3;
            int cycle = (int)(Sr * 120f / MathF.Max(300f, rpm));
            var energy = new List<float>();
            for (int c = from; c + cycle <= r.Exhaust.Length; c += cycle)
            {
                float en = 0f; for (int i = c; i < c + cycle; i++) en += r.Exhaust[i] * r.Exhaust[i];
                energy.Add(MathF.Sqrt(en / cycle));
            }
            float mean = energy.Average();
            float diff = 0f; for (int i = 1; i < energy.Count; i++) diff += MathF.Abs(energy[i] - energy[i - 1]);
            return diff / (energy.Count - 1) / MathF.Max(1e-9f, mean);
        }
        float stock = Roughness(262f), big = Roughness(308f);
        Assert.True(big > stock * 1.3f, $"stock {stock:F3}, big cam {big:F3}");
    }

    [Fact]
    public void Driveline_LocksEngineToWheels_AndShiftsDropTheRevsByTheRatio()
    {
        var v = VehicleProfile.V8Sports;
        var orders = new List<DriveOrder>
        {
            new(DriverAction.Cranking, 0.5f), new(DriverAction.Idling, 1.5f),
            new(DriverAction.Accelerating, 9f, 40f),
        };
        var r = VehicleSynth.Render(v, orders, 3);
        Assert.True(r.Distance[^1] > 100f, $"only {r.Distance[^1]} m");
        var shifts = r.Log.Where(l => l.Contains("into 2")).ToList();
        Assert.NotEmpty(shifts);
        // Speed at the end is what the last gear and the revs say it is.
        Assert.Contains(r.Log, l => l.Contains("km/h"));
    }

    [Fact]
    public void VirtualDriver_FollowsAReportedSpeed()
    {
        var v = VehicleProfile.V8Muscle;
        var engine = new EngineSynth(v.Engine, Sr, 4);
        var dl = new Driveline(v);
        var drv = new VirtualDriver(dl, engine) { TargetSpeed = 0f };
        float dt = 1f / Sr;
        for (int i = 0; i < Sr * 3; i++) { drv.Apply(dt); dl.Step(engine, dt); }
        Assert.InRange(engine.Rpm, 300f, 1600f);
        drv.TargetSpeed = 16f;                          // 58 km/h
        for (int i = 0; i < Sr * 10; i++) { drv.Apply(dt); dl.Step(engine, dt); }
        Assert.InRange(dl.Speed, 12f, 20f);
        Assert.True(engine.Rpm > v.Engine.IdleRpm * 1.2f);
        drv.TargetSpeed = 0f;
        for (int i = 0; i < Sr * 8; i++) { drv.Apply(dt); dl.Step(engine, dt); }
        Assert.InRange(dl.Speed, 0f, 1.5f);
    }

    [Fact]
    public void EngineProcessor_RendersFiniteAudio_InRealTimeBudget()
    {
        var state = new OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState(VehicleProfile.V8Muscle, Sr, 9)
        {
            TargetSpeed = 12f,
        };
        var buf = new float[512];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int blocks = Sr * 2 / 512;
        for (int i = 0; i < blocks; i++) state.Render(buf);
        sw.Stop();
        Assert.All(buf, s => Assert.True(float.IsFinite(s) && MathF.Abs(s) <= 1f));
        // Two seconds of audio must render in well under two seconds, with room for a few cars.
        Assert.True(sw.Elapsed.TotalSeconds < 1.5, $"took {sw.Elapsed.TotalSeconds:F2}s for 2s of audio");
    }
}
