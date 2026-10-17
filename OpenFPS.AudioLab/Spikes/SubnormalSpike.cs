using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Signals;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// --subnormals [sec=N]: the flush state FMOD's mixer thread runs our DSP callbacks with (any platform), then
/// which models reach subnormal numbers ringing down to silence and what flushing them saves
/// (docs/AUDIO_QUALITY_2026-10-06.md, row 14). The counting is x86-64 Linux only: it reads MXCSR's sticky
/// denormal-operand and underflow flags around every 64-sample block, so no model is touched to count.
/// An AMD processor slows less on subnormal numbers than an Intel one, where the timings are the measurement.
/// </summary>
public static class SubnormalSpike
{
    private const int Rate = 48000, Block = 64;
    private const uint Denormal = 1u << 1, Underflow = 1u << 4, Flags = 0x3f;

    private sealed record Model(string Name, string Thread, Func<(Action Excite, Action<float[]> Ring)> Make);

    public static int Run(string[] args)
    {
        int seconds = 30;
        foreach (var a in args) if (a.StartsWith("sec=")) seconds = int.Parse(a[4..]);
        Mixer(OUTPUTTYPE.NOSOUND, "no output");
        Mixer(OUTPUTTYPE.AUTODETECT, "the sound device");
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.WriteLine("Counting reads MXCSR's flags through glibc: x86-64 Linux only.");
            return 0;
        }

        Console.WriteLine($"\nEach model sounded, then {seconds} s of ring-down. 'operand' and 'result' are the shares of 64-sample blocks of the"
                          + " ring-down that read a subnormal number (MXCSR DE) or made a result too small for a normal one (UE);"
                          + " the times are the ring-down alone, best of three.");
        Console.WriteLine($"  {"model",-52} {"thread",-7} {"operand",8} {"result",8} {"first",7} {"plain",9} {"flushed",9} {"same bits",9}");
        foreach (var m in Models())
        {
            var (operand, result, first) = OnThread(false, () => Count(m, seconds));
            double plain = double.MaxValue, flushed = double.MaxValue;
            string hashPlain = "", hashFlushed = "";
            for (int i = 0; i < 3; i++)
            {
                var p = OnThread(false, () => Time(m, seconds));
                var f = OnThread(true, () => Time(m, seconds));
                plain = Math.Min(plain, p.Ms); flushed = Math.Min(flushed, f.Ms);
                hashPlain = p.Hash; hashFlushed = f.Hash;
            }
            Console.WriteLine($"  {m.Name,-52} {m.Thread,-7} {operand,8:P1} {result,8:P1} {(first < 0 ? "-" : $"{first:F1}s"),7} {plain,7:F0}ms {flushed,7:F0}ms {(hashPlain == hashFlushed ? "yes" : "no"),9}");
        }

        Console.WriteLine("\nWhole renders, made once each way (the doors keep their floor, DoorPhysics.Rest):");
        foreach (var (name, render) in Renders())
        {
            var plain = OnThread(false, () => Whole(render));
            var flushed = OnThread(true, () => Whole(render));
            float worst = 0f;
            for (int i = 0; i < Math.Min(plain.Out.Length, flushed.Out.Length); i++) worst = MathF.Max(worst, MathF.Abs(plain.Out[i] - flushed.Out[i]));
            Console.WriteLine($"  {name,-30} subnormal operand {(plain.Flags & Denormal) != 0,-5} result {(plain.Flags & Underflow) != 0,-5}"
                              + $" plain {plain.Ms,6:F0} ms, flushed {flushed.Ms,6:F0} ms, largest difference {worst:G3}");
        }
        return 0;
    }

    private static IEnumerable<(string, Func<float[]>)> Renders()
    {
        yield return ("door.knob.open", () => KnobDoor.RenderOpen(new KnobDoor.Door { Width = 1.1f, HingeWear = KnobDoor.WearOf(0), Seed = 1 }, Rate, 0.9));
        yield return ("door.glass.close", () => GlassDoor.RenderClose(new GlassDoor.Door { Kind = GlassDoor.Kind.Pull, Variant = 1, Seed = 2 }, Rate));
        yield return ("door.car.close", () => CarDoor.Render(true, Rate, 3));
        yield return ("clap.dry", () => Applause.RenderClap(Rate, 1));
    }

    private static (float[] Out, uint Flags, double Ms) Whole(Func<float[]> render)
    {
        FlushToZero.TryRead(out uint before);
        FlushToZero.TryWrite(before & ~Flags);
        var sw = Stopwatch.StartNew();
        var x = render();
        sw.Stop();
        FlushToZero.TryRead(out uint after);
        return (x, after, sw.Elapsed.TotalMilliseconds);
    }

    private static IEnumerable<Model> Models()
    {
        yield return new("ground reflection, the voice falls silent", "mixer", () =>
        {
            var g = new GroundReflection(Rate);
            g.Set(0.0004f, 0.6f, 0.4f);
            var rng = new Random(1);
            return (() => { for (int i = 0; i < Rate / 2; i++) g.Process(Noise(rng)); },
                    b => { for (int i = 0; i < b.Length; i++) b[i] = g.Process(0f); });
        });
        yield return new("ground reflection, the ground goes, the voice plays on", "mixer", () =>
        {
            var g = new GroundReflection(Rate);
            g.Set(0.0004f, 0.6f, 0.4f);
            var rng = new Random(2);
            return (() => { for (int i = 0; i < Rate / 2; i++) g.Process(Noise(rng)); g.Set(0.0004f, 0f, 0f); },
                    b => { for (int i = 0; i < b.Length; i++) b[i] = g.Process(Noise(rng)); });
        });
        yield return new("boundary taps, the mix falls silent", "mixer", () =>
        {
            var (s, rng) = Boundary(3);
            var inBuf = new float[Block * 2];
            var outBuf = new float[Block * 2];
            return (() => BoundaryNoise(s, rng, Rate / 2),
                    b => { Array.Clear(inBuf); BoundaryProximityProcessor.Process(s, inBuf, outBuf, 2, 2); for (int i = 0; i < b.Length; i++) b[i] = outBuf[2 * i]; });
        });
        yield return new("boundary taps, the walls go, the mix plays on", "mixer", () =>
        {
            var (s, rng) = Boundary(4);
            var inBuf = new float[Block * 2];
            var outBuf = new float[Block * 2];
            return (() => { BoundaryNoise(s, rng, Rate / 2); Array.Clear(s.TargetGainL); Array.Clear(s.TargetGainR); },
                    b =>
                    {
                        for (int i = 0; i < inBuf.Length; i++) inBuf[i] = Noise(rng);
                        BoundaryProximityProcessor.Process(s, inBuf, outBuf, 2, 2);
                        for (int i = 0; i < b.Length; i++) b[i] = outBuf[2 * i];
                    });
        });
        yield return new("ear decorrelator (all-passes), the tail falls silent", "mixer", () =>
        {
            var d = new EarDecorrelator(0, Rate);
            var rng = new Random(5);
            return (() => { for (int i = 0; i < Rate / 2; i++) d.Process(Noise(rng)); },
                    b => { for (int i = 0; i < b.Length; i++) b[i] = d.Process(0f); });
        });
        yield return new("echo diffuser (all-passes), the echo falls silent", "mixer", () =>
        {
            var d = new EchoDiffuser(0.5f, 1, Rate);
            var rng = new Random(6);
            return (() => { for (int i = 0; i < Rate / 2; i++) d.Process(Noise(rng)); },
                    b => { for (int i = 0; i < b.Length; i++) b[i] = d.Process(0f); });
        });
        yield return new("crossing gong, struck once (the bell's modes)", "pool", () =>
        {
            var bell = new StruckBell(ModelLibrary.Bell("crossing_gong"), Rate, 7);
            return (() => { bell.Strike(); for (int i = 0; i < Rate / 2; i++) bell.Step(); },
                    b => { for (int i = 0; i < b.Length; i++) { bell.Step(); b[i] = bell.Out; } });
        });
        yield return new("crossing bell voice, switched off (Produce/Consume)", "pool", () =>
        {
            var v = new BellVoiceState(ModelLibrary.Bell("crossing_gong"), Rate, 8);
            v.SetListener(new Vector3(3f, 1.6f, 8f));
            var buf = new float[Block];
            return (() => { for (int i = 0; i < 2 * Rate / Block; i++) { v.Produce(); v.Consume(buf); } v.Running = false; },
                    b => { v.Produce(); v.Consume(b); });
        });
        yield return new("eight street engines, steady (Produce/Consume)", "pool", () =>
        {
            var list = Enumerable.Range(0, 8).Select(Street).ToList();
            var buf = new float[Block];
            return (() => { for (int i = 0; i < Rate / Block; i++) foreach (var v in list) { v.Produce(); v.Consume(buf); } },
                    b => { Array.Clear(b); foreach (var v in list) { v.Produce(); v.Consume(buf); for (int i = 0; i < b.Length; i++) b[i] += buf[i]; } });
        });
        yield return new("an engine faded out (Produce/Consume)", "pool", () =>
        {
            var v = Street(3);
            var buf = new float[Block];
            return (() => { for (int i = 0; i < Rate / Block; i++) { v.Produce(); v.Consume(buf); } v.TargetEnvelope = 0f; },
                    b => { v.Produce(); v.Consume(b); });
        });
    }

    private static readonly string[] StreetMix = { "i4_compact", "v6", "transit_bus", "pickup_v8", "school_bus", "diesel_truck", "sportbike", "police_v8" };

    private static EngineVoiceState Street(int i)
    {
        string key = StreetMix[i % StreetMix.Length];
        float speed = 4f + (i * 7 % 11) * 1.5f;
        var v = new EngineVoiceState(MachineRegistry.VehicleFor(key), Rate, 1000 + i) { TargetSpeed = speed, CompensateLevel = true };
        v.PlaceAtSpeed(speed);
        float d = 5f + (i * 13 % 56), a = i * 0.7f;
        v.SetListener(new Vector3(MathF.Sin(a) * d, 1.6f, MathF.Cos(a) * d));
        return v;
    }

    private static (BoundaryVoiceState, Random) Boundary(int seed)
    {
        var s = new BoundaryVoiceState(Rate);
        for (int t = 0; t < BoundaryVoiceState.MaxTaps; t++)
        {
            s.TargetDelayL[t] = s.TargetDelayR[t] = 0.002f + 0.003f * t;
            s.TargetGainL[t] = s.TargetGainR[t] = 0.3f;
            s.LowpassAlpha[t] = 0.4f;
        }
        return (s, new Random(seed));
    }

    private static void BoundaryNoise(BoundaryVoiceState s, Random rng, int samples)
    {
        var inBuf = new float[Block * 2];
        var outBuf = new float[Block * 2];
        for (int done = 0; done < samples; done += Block)
        {
            for (int i = 0; i < inBuf.Length; i++) inBuf[i] = Noise(rng);
            BoundaryProximityProcessor.Process(s, inBuf, outBuf, 2, 2);
        }
    }

    private static float Noise(Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0) * 0.3f;

    /// <summary>The shares of ring-down blocks whose arithmetic read or made a subnormal, and when the first did.</summary>
    private static (double Operand, double Result, double First) Count(Model m, int seconds)
    {
        var (excite, ring) = m.Make();
        excite();
        var buf = new float[Block];
        int blocks = seconds * Rate / Block, operand = 0, result = 0, first = -1;
        for (int k = 0; k < blocks; k++)
        {
            FlushToZero.TryRead(out uint before);
            FlushToZero.TryWrite(before & ~Flags);
            ring(buf);
            FlushToZero.TryRead(out uint after);
            if ((after & Denormal) != 0) operand++;
            if ((after & Underflow) != 0) result++;
            if ((after & (Denormal | Underflow)) != 0 && first < 0) first = k;
        }
        return ((double)operand / blocks, (double)result / blocks, first < 0 ? -1 : first * Block / (double)Rate);
    }

    private static (double Ms, string Hash) Time(Model m, int seconds)
    {
        var (excite, ring) = m.Make();
        excite();
        var buf = new float[Block];
        int blocks = seconds * Rate / Block;
        var all = new float[blocks * Block];
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < blocks; k++) { ring(buf); buf.CopyTo(all, k * Block); }
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(MemoryMarshal.AsBytes(all.AsSpan())))[..16]);
    }

    /// <summary>Runs on a thread of its own, flushed or not, so neither leaves anything on this one.</summary>
    private static T OnThread<T>(bool flush, Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                if (flush && !FlushToZero.ThisThread("the lab")) throw new InvalidOperationException("could not flush");
                if (!flush && FlushToZero.IsOn) throw new InvalidOperationException("a plain thread was born flushed");
                result = work();
            }
            catch (Exception ex) { error = ex; }
        }, 16 << 20);
        t.Start();
        t.Join();
        if (error != null) throw error;
        return result;
    }

    // What FMOD's mixer thread hands our callbacks: read in a callback, not set.
    private static uint _mixerState;
    private static bool _mixerFlushes;
    private static int _mixerReads;
    private static float _mixerProduct = -1f;
    private static volatile float _tiny = 1e-30f, _small = 1e-10f;
    private static readonly DSP_READ_CALLBACK ReadCallback = Read;

    private static RESULT Read(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        if (FlushToZero.TryRead(out uint s)) _mixerState = s;
        _mixerFlushes = FlushToZero.IsOn;
        _mixerProduct = _tiny * _small;
        Interlocked.Increment(ref _mixerReads);
        unsafe { new Span<float>((void*)outbuffer, (int)length * outchannels).Clear(); }
        return RESULT.OK;
    }

    private static void Mixer(OUTPUTTYPE output, string what)
    {
        _mixerReads = 0;
        Factory.System_Create(out FMOD.System sys);
        sys.setOutput(output);
        var init = sys.init(32, INITFLAGS.NORMAL, IntPtr.Zero);
        if (init != RESULT.OK) { Console.WriteLine($"FMOD with {what}: could not start ({init})."); sys.release(); return; }
        var desc = new DSP_DESCRIPTION { pluginsdkversion = VERSION.number, numinputbuffers = 1, numoutputbuffers = 1, read = ReadCallback };
        sys.createDSP(ref desc, out DSP dsp);
        sys.playDSP(dsp, default, false, out Channel _);
        for (int i = 0; i < 50 && Volatile.Read(ref _mixerReads) < 20; i++) { sys.update(); System.Threading.Thread.Sleep(20); }
        sys.release();
        // MXCSR on x86-64 Linux, FPCR on ARM64, the C runtime's control word on Windows.
        Console.WriteLine($"FMOD's mixer thread with {what}: {_mixerReads} callbacks, control register 0x{_mixerState:x4}, "
                          + $"flushing {(_mixerFlushes ? "on" : "off")}; 1e-30 x 1e-10 there = {_mixerProduct:G3}.");
    }
}
