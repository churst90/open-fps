using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --traced-reverb: the traced outdoor reverb, measured headless. Three places — open ground, a
/// street canyon (two rows of brick buildings 20 m apart on asphalt), a closed concrete room — are
/// traced from the listener's position, an impulse is played through the reflection effect, and the
/// omni (W) channel of what comes back is metered: energy in 50 ms windows, the level of the whole
/// tail against the impulse, how long a trace takes. The street should hand back more than open
/// ground and show its crossing period (2 x 20 m / 343 = 117 ms); the room most of all.
/// </summary>
public static class TracedReverbSpike
{
    /// <summary>Which block the impulse goes in: a fresh effect crossfades in from an empty response
    /// over its first block, so an impulse in block 0 measures the crossfade. SA_IMPULSE_BLOCK overrides.</summary>
    private static readonly int ImpulseBlock = int.TryParse(Environment.GetEnvironmentVariable("SA_IMPULSE_BLOCK"), out int ib) ? ib : 0;

    public static int Run()
    {
        AcousticRegistry.Initialize();
        bool parametric = Environment.GetEnvironmentVariable("SA_PARAMETRIC") == "1";
        if (parametric) TracedReverb.SimulatedType = Phonon.IPL_REFLECTIONEFFECTTYPE_HYBRID;
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("context failed"); return 1; }
        var q = Quaternion.Identity;
        var ground = new SteamAudioScene.Box(new Vector3(0, -0.25f, 0), new Vector3(600, 0.5f, 600), q, "Asphalt");

        var open = new List<SteamAudioScene.Box> { ground };
        var street = new List<SteamAudioScene.Box> { ground };
        for (int i = 0; i < 8; i++)
        {
            float z = -70f + i * 20f;
            street.Add(new(new Vector3(-15f, 9f, z), new Vector3(10f, 18f, 20f), q, "Brick"));
            street.Add(new(new Vector3(15f, 9f, z), new Vector3(10f, 18f, 20f), q, "Brick"));
        }
        var room = new List<SteamAudioScene.Box>
        {
            ground,
            new(new Vector3(0, 1.5f, -6), new Vector3(12, 3, 0.3f), q, "Concrete"), new(new Vector3(0, 1.5f, 6), new Vector3(12, 3, 0.3f), q, "Concrete"),
            new(new Vector3(-6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, "Concrete"), new(new Vector3(6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, "Concrete"),
            new(new Vector3(0, 3.15f, 0), new Vector3(12, 0.3f, 12), q, "Concrete"),
        };

        List<SteamAudioScene.Box> Room(string walls, string floor) => new()
        {
            new(new Vector3(0, -0.25f, 0), new Vector3(600, 0.5f, 600), q, floor),
            new(new Vector3(0, 1.5f, -6), new Vector3(12, 3, 0.3f), q, walls), new(new Vector3(0, 1.5f, 6), new Vector3(12, 3, 0.3f), q, walls),
            new(new Vector3(-6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, walls), new(new Vector3(6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, walls),
            new(new Vector3(0, 3.15f, 0), new Vector3(12, 0.3f, 12), q, walls),
        };
        List<SteamAudioScene.Box> Cabin(string preset)
        {
            var v = MachineRegistry.VehicleFor(preset);
            var g = VehicleCabin.Measure(v)!.Value;
            var b = new List<SteamAudioScene.Box>();
            foreach (var (prefab, at, size) in VehicleCabin.Shell(v, g))
                b.Add(new(at, size, q, VehicleCabin.MaterialOf(prefab)));
            return b;
        }
        Vector3 Seat(string preset)
        {
            var g = VehicleCabin.Measure(MachineRegistry.VehicleFor(preset))!.Value;
            return new Vector3(0.3f, g.FloorTop + 1.1f, g.Cz);
        }
        var places = new List<(string, List<SteamAudioScene.Box>, Vector3)>
        {
            ("open ground", open, new Vector3(2f, 1.6f, 0f)), ("street, 20 m", street, new Vector3(2f, 1.6f, 0f)),
            // What the listener's own trace is given in the game: the same places without their open
            // ground (SteamAudioScene.WithoutOpenGround). Nothing may come back inside 25 ms.
            ("open, as traced", SteamAudioScene.WithoutOpenGround(open), new Vector3(2f, 1.6f, 0f)),
            ("street, as traced", SteamAudioScene.WithoutOpenGround(street), new Vector3(2f, 1.6f, 0f)),
            ("concrete room", room, new Vector3(2f, 1.6f, 0f)), ("tiled room", Room("Tile", "Tile"), new Vector3(2f, 1.6f, 0f)),
            ("wood, carpet", Room("Wood", "Carpet"), new Vector3(2f, 1.6f, 0f)),
            // Marlow Tower flat 01F as the city builds it: /tp -14 -85 0.5.
            ("flat 01F", new List<SteamAudioScene.Box>
            {
                new(new Vector3(0, 0.04f, 0), new Vector3(8.65f, 0.04f, 17.86f), q, "Carpet"),
                new(new Vector3(0, -0.07f, 0), new Vector3(21f, 0.17f, 90f), q, "Concrete"),
                new(new Vector3(0, 2.735f, 0), new Vector3(20.5f, 0.03f, 89.3f), q, "Plaster"),
                new(new Vector3(4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 80f), q, "Brick"),
                new(new Vector3(-4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 17.86f), q, "Plaster"),
                new(new Vector3(0, 1.4f, -9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
                new(new Vector3(0, 1.4f, 9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
                new(new Vector3(3.2f, 0.32f, -3.4f), new Vector3(1.8f, 0.6f, 1.8f), q, "Audience"),
            }, new Vector3(0.175f, 1.7f, 0.16f)),
            ("bus cabin", Cabin("school_bus_na"), Seat("school_bus_na")), ("hatchback cabin", Cabin("i4_economy"), Seat("i4_economy")),
        };
        int failures = 0;
        foreach (var (name, boxes, ear) in places)
        {
            using var scene = new SteamAudioScene(ctx);
            scene.Build(boxes);
            int trFrame = int.TryParse(Environment.GetEnvironmentVariable("SA_FRAME"), out var tf) ? tf : TracedReverb.TracedFrame;
            using var tr = new TracedReverb(ctx, 44100, trFrame);
            tr.SetScene(scene);
            tr.SetListener(ear);
            var until = DateTime.UtcNow.AddSeconds(20);
            while (tr.Runs < 2 && DateTime.UtcNow < until) Thread.Sleep(50);
            if (!tr.TryGetParams(out var prm)) { Console.WriteLine($"{name}: no IR"); continue; }

            int fs = int.TryParse(Environment.GetEnvironmentVariable("SA_FRAME"), out var fsv) ? fsv : TracedReverb.TracedFrame;
            var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = fs };
            var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = tr.IrSize, numChannels = TracedReverb.Channels };
            Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr effect);
            var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
            Phonon.iplAudioBufferAllocate(ctx, 1, fs, ref inBuf);
            Phonon.iplAudioBufferAllocate(ctx, TracedReverb.Channels, fs, ref outBuf);
            var mono = new float[fs];
            var inter = new float[fs * TracedReverb.Channels];
            var w = new List<float>();
            // And what the ear gets of it: decoded round the head through an HRTF, as the game does.
            var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
            // The game makes its HRTF for the mixer's block (1024) and decodes in the trace's (256).
            var hau = au; if (int.TryParse(Environment.GetEnvironmentVariable("SA_HRTF_FRAME"), out int hf)) hau.frameSize = hf;
            Phonon.iplHRTFCreate(ctx, ref hau, ref hs, out IntPtr hrtf);
            var dsx = new Phonon.IPLAmbisonicsDecodeEffectSettings { speakerLayout = Phonon.StereoLayout(), hrtf = hrtf, maxOrder = TracedReverb.Order };
            Phonon.iplAmbisonicsDecodeEffectCreate(ctx, ref au, ref dsx, out IntPtr dec);
            var stBuf = new Phonon.IPLAudioBuffer(); Phonon.iplAudioBufferAllocate(ctx, 2, fs, ref stBuf);
            var stI = new float[fs * 2];
            var dp = new Phonon.IPLAmbisonicsDecodeEffectParams { order = TracedReverb.Order, hrtf = hrtf, orientation = Phonon.ListenerFrame(System.Numerics.Quaternion.Identity), binaural = Phonon.IPL_TRUE };
            double decL = 0, decR = 0; var decWin = new double[10]; int decAt = 0;
            var dirs = new[] { new List<float>(), new List<float>(), new List<float>(), new List<float>() };   // ACN W Y Z X
            for (int b = 0; b < 44100 * 3 / fs; b++)
            {
                Array.Clear(mono);
                if (b == ImpulseBlock) mono[0] = 1f;
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
                Phonon.iplAudioBufferInterleave(ctx, ref outBuf, inter);
                Phonon.iplAmbisonicsDecodeEffectApply(dec, ref dp, ref outBuf, ref stBuf);
                Phonon.iplAudioBufferInterleave(ctx, ref stBuf, stI);
                for (int k = 0; k < fs; k++, decAt++)
                {
                    double dl = stI[k * 2], dr = stI[k * 2 + 1];
                    decL += dl * dl; decR += dr * dr;
                    if (decAt / 441 < 10) decWin[decAt / 441] += (dl * dl + dr * dr) / 2;
                }
                for (int k = 0; k < fs; k++)
                {
                    w.Add(inter[k * TracedReverb.Channels]);
                    for (int c = 0; c < 4; c++) dirs[c].Add(inter[k * TracedReverb.Channels + c]);
                }
            }
            // What one stage costs the mixer: the effect applied to live-ish input, per block.
            var rngc = new Random(1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int b = 0; b < 200; b++)
            {
                for (int k = 0; k < fs; k++) mono[k] = (float)(rngc.NextDouble() * 2 - 1) * 0.1f;
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
            }
            double blockMs = sw.Elapsed.TotalMilliseconds / 200;
            // ...and with the trace refreshing underneath, as it does in the game: the worst block.
            double worst = 0; int runs0 = tr.Runs;
            for (int b = 0; b < 60; b++)
            {
                Thread.Sleep(23);
                tr.TryGetParams(out prm);
                for (int k = 0; k < fs; k++) mono[k] = (float)(rngc.NextDouble() * 2 - 1) * 0.1f;
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
                worst = Math.Max(worst, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
            Console.WriteLine($"   one stage: {blockMs:F2} ms per block steady, worst {worst:F2} ms with {tr.Runs - runs0} re-traces underneath (deadline 23 ms)");
            // Wobble: steady noise in, in real time, the IR re-fetched every block as the game does
            // while the tracer re-runs underneath. How much the room's level moves, 50 ms at a time.
            {
                var lv = new List<double>();
                double acc = 0; int cnt = 0, win = 2205, runs1 = tr.Runs;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long doneSamples = 0;
                while (clock.Elapsed.TotalSeconds < 3.0)
                {
                    if (doneSamples > clock.Elapsed.TotalSeconds * 44100) { Thread.Sleep(1); continue; }
                    tr.TryGetParams(out prm);
                    for (int k = 0; k < fs; k++) mono[k] = (float)(rngc.NextDouble() * 2 - 1) * 0.1f;
                    Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                    Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
                    Phonon.iplAudioBufferInterleave(ctx, ref outBuf, inter);
                    for (int k = 0; k < fs; k++)
                    {
                        acc += inter[k * TracedReverb.Channels] * (double)inter[k * TracedReverb.Channels]; cnt++;
                        if (cnt == win) { lv.Add(10 * Math.Log10(acc / win + 1e-20)); acc = 0; cnt = 0; }
                    }
                    doneSamples += fs;
                }
                var tail = lv.Skip(lv.Count / 3).ToList();      // after the tail has built up
                double mean = tail.Average(), sd = Math.Sqrt(tail.Average(v => (v - mean) * (v - mean)));
                double jump = 0; for (int k = 1; k < tail.Count; k++) jump = Math.Max(jump, Math.Abs(tail[k] - tail[k - 1]));
                Console.WriteLine($"   wobble under steady noise: sd {sd:F2} dB, biggest 50 ms step {jump:F2} dB, {tr.Runs - runs1} re-traces in 3 s");
            }
            double total = 0; foreach (var x in w) total += x * (double)x;
            var windows = new List<string>();
            for (int k = 0; k < 16; k++)
            {
                double e = 0; int a = k * 2205;
                for (int j = a; j < a + 2205 && j < w.Count; j++) e += w[j] * (double)w[j];
                windows.Add($"{10 * Math.Log10(e + 1e-20):F0}");
            }
            // Where the energy has fallen 20 dB from its peak window, as a rough decay.
            Console.WriteLine($"{name,-14} tail {10 * Math.Log10(total + 1e-20),6:F1} dB re impulse, trace {tr.LastRunMs,5:F0} ms");
            Console.WriteLine($"   50 ms windows (dB): {string.Join(" ", windows)}");
            // Where it comes from: the first-order channels' energy against W, early and late.
            // Y is left-right, Z up-down, X front-back (ACN/SN3D). A room heard round the head has Y
            // and X comparable to Z; a room that is all floor and ceiling is all Z.
            {
                string Dir(int a, int b)
                {
                    double E(int c) { double e = 0; for (int i = a; i < Math.Min(b, dirs[c].Count); i++) e += dirs[c][i] * (double)dirs[c][i]; return e + 1e-30; }
                    double ew = E(0);
                    return $"Y {10 * Math.Log10(E(1) / ew):F1}  Z {10 * Math.Log10(E(2) / ew):F1}  X {10 * Math.Log10(E(3) / ew):F1} dB re W";
                }
                Console.WriteLine($"   direction 0-50 ms: {Dir(0, 2205)};   50-300 ms: {Dir(2205, 13230)}");
            }
            // The first arrivals, to the tenth of a millisecond: when the place answers, and how loud.
            // An impulse sent in at unity; a copy within 20 ms is heard as colour, not as an echo.
            var arrivals = new List<(int At, double Db)>();
            double peakAll = 0; foreach (var x in w) peakAll = Math.Max(peakAll, Math.Abs(x));
            for (int j = 1; j < Math.Min(w.Count - 1, 44100 / 10); j++)
            {
                double v = Math.Abs(w[j]);
                if (v > Math.Abs(w[j - 1]) && v >= Math.Abs(w[j + 1]) && v > peakAll * 0.05) arrivals.Add((j, 20 * Math.Log10(v + 1e-20)));
            }
            Console.WriteLine("   first arrivals (ms: dB re impulse): "
                + string.Join(", ", arrivals.OrderByDescending(a => a.Db).Take(5).OrderBy(a => a.At).Select(a => $"{a.At / 44.1:F1}: {a.Db:F0}")));
            // The listener's trace plays every sound as if it came from the listener's head. Anything it
            // hands back inside 25 ms is a copy of a close sound right behind it: a small room.
            if (name.EndsWith("as traced"))
            {
                double early = 0; for (int j = 0; j < Math.Min(w.Count, 44100 * 25 / 1000); j++) early += w[j] * (double)w[j];
                double earlyDb = 10 * Math.Log10(early + 1e-20);
                bool ok = earlyDb < -40;
                if (!ok) failures++;
                Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}: {earlyDb:F0} dB back inside 25 ms (must be under -40)");
            }
            Console.WriteLine($"   decoded (what the ear gets): left {10 * Math.Log10(decL + 1e-20):F1}, right {10 * Math.Log10(decR + 1e-20):F1} dB re impulse, against omni {10 * Math.Log10(total + 1e-20):F1}");
            Console.WriteLine($"   decoded 10 ms windows, first 100 ms (dB): {string.Join(" ", decWin.Select(v => $"{10 * Math.Log10(v + 1e-20):F0}"))}");
            if (parametric && tr.TryGetParams(out var pp))
            {
                Console.WriteLine($"   reverb times (s): {pp.reverbTimes0:F2} {pp.reverbTimes1:F2} {pp.reverbTimes2:F2}; eq {pp.eq0:F2} {pp.eq1:F2} {pp.eq2:F2}; delay {pp.delay}");
                var pes = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_PARAMETRIC, irSize = tr.IrSize, numChannels = TracedReverb.Channels };
                Phonon.iplReflectionEffectCreate(ctx, ref au, ref pes, out IntPtr peff);
                pp.type = Phonon.IPL_REFLECTIONEFFECTTYPE_PARAMETRIC;
                double pW = 0, pL = 0, pR = 0; var pwin = new double[16]; var chE = new double[TracedReverb.Channels];
                Phonon.iplReflectionEffectReset(peff);
                for (int b = 0; b < 44100 * 3 / fs; b++)
                {
                    Array.Clear(mono); if (b == 0) mono[0] = 1f;
                    Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                    Phonon.iplReflectionEffectApply(peff, ref pp, ref inBuf, ref outBuf, IntPtr.Zero);
                    Phonon.iplAudioBufferInterleave(ctx, ref outBuf, inter);
                    Phonon.iplAmbisonicsDecodeEffectApply(dec, ref dp, ref outBuf, ref stBuf);
                    Phonon.iplAudioBufferInterleave(ctx, ref stBuf, stI);
                    for (int k = 0; k < fs; k++)
                    {
                        for (int ch = 0; ch < TracedReverb.Channels; ch++) chE[ch] += inter[k * TracedReverb.Channels + ch] * (double)inter[k * TracedReverb.Channels + ch];
                        double w0 = inter[k * TracedReverb.Channels];
                        pW += w0 * w0; pL += stI[k * 2] * (double)stI[k * 2]; pR += stI[k * 2 + 1] * (double)stI[k * 2 + 1];
                        int wi = (b * fs + k) / 2205; if (wi < 16) pwin[wi] += w0 * w0;
                    }
                }
                Console.WriteLine($"   parametric tail: omni {10 * Math.Log10(pW + 1e-20):F1}, left {10 * Math.Log10(pL + 1e-20):F1}, right {10 * Math.Log10(pR + 1e-20):F1} dB re impulse; channels {string.Join(" ", chE.Select(e => $"{10 * Math.Log10(e + 1e-20):F0}"))}");
                Console.WriteLine($"   parametric 50 ms windows (dB): {string.Join(" ", pwin.Select(v => $"{10 * Math.Log10(v + 1e-20):F0}"))}");
                Phonon.iplReflectionEffectRelease(ref peff);
            }
            Phonon.iplAudioBufferFree(ctx, ref stBuf); Phonon.iplAmbisonicsDecodeEffectRelease(ref dec); Phonon.iplHRTFRelease(ref hrtf);
            Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref outBuf);
            Phonon.iplReflectionEffectRelease(ref effect);
        }
        Phonon.iplContextRelease(ref ctx);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Which way the traced soundfield faces. A plaster wall 2 m to the LEFT (game -x) and one 3 m
    /// AHEAD (game +z) of a listener at the origin, no floor. In the traced response the left wall
    /// answers first (4.2 m round trip, about 12 ms) and the wall ahead second (6.2 m, about 18 ms).
    /// Ambisonic Y is "left" and X is "ahead", so at the first arrival Y must agree with W in sign and
    /// at the second X must; decoded through the HRTF with the listener facing +z, the first arrival
    /// must be louder in the left ear, and with the listener turned to face -x (west) the wall ahead
    /// is on the right and must be louder in the right ear. A scene handed to Steam Audio without
    /// Phonon.World's flip puts the wall ahead behind (X negative).
    /// </summary>
    public static int FrameCheck()
    {
        AcousticRegistry.Initialize();
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("context failed"); return 1; }
        var q = Quaternion.Identity;
        var boxes = new List<SteamAudioScene.Box>
        {
            new(new Vector3(-2.1f, 1.5f, 0f), new Vector3(0.2f, 3f, 6f), q, "Plaster"),   // left
            new(new Vector3(0f, 1.5f, 3.1f), new Vector3(6f, 3f, 0.2f), q, "Plaster"),    // ahead
        };
        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        int fs = TracedReverb.TracedFrame;
        using var tr = new TracedReverb(ctx, 44100, fs);
        tr.SetScene(scene);
        tr.SetListener(new Vector3(0f, 1.5f, 0f));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (tr.Runs < 2 && DateTime.UtcNow < until) Thread.Sleep(50);
        if (!tr.TryGetParams(out var prm)) { Console.WriteLine("no IR"); return 1; }

        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = fs };
        var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = tr.IrSize, numChannels = TracedReverb.Channels };
        Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr effect);
        var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(ctx, 1, fs, ref inBuf);
        Phonon.iplAudioBufferAllocate(ctx, TracedReverb.Channels, fs, ref outBuf);
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var dsx = new Phonon.IPLAmbisonicsDecodeEffectSettings { speakerLayout = Phonon.StereoLayout(), hrtf = hrtf, maxOrder = TracedReverb.Order };
        Phonon.iplAmbisonicsDecodeEffectCreate(ctx, ref au, ref dsx, out IntPtr decNorth);
        Phonon.iplAmbisonicsDecodeEffectCreate(ctx, ref au, ref dsx, out IntPtr decWest);
        var stBuf = new Phonon.IPLAudioBuffer(); Phonon.iplAudioBufferAllocate(ctx, 2, fs, ref stBuf);
        var stI = new float[fs * 2];
        var facingNorth = new Phonon.IPLAmbisonicsDecodeEffectParams { order = TracedReverb.Order, hrtf = hrtf, orientation = Phonon.ListenerFrame(Quaternion.Identity), binaural = Phonon.IPL_TRUE };
        var facingWest = facingNorth; facingWest.orientation = Phonon.ListenerFrame(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2));

        int total = 44100 / 10;   // the first 100 ms
        var ch = new float[4][]; for (int c = 0; c < 4; c++) ch[c] = new float[total];
        var north = new float[total, 2]; var west = new float[total, 2];
        var mono = new float[fs]; var inter = new float[fs * TracedReverb.Channels];
        int at = 0;
        for (int b = 0; at < total; b++)
        {
            Array.Clear(mono);
            if (b == 0) mono[0] = 1f;
            Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
            Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
            Phonon.iplAudioBufferInterleave(ctx, ref outBuf, inter);
            for (int k = 0; k < fs && at + k < total; k++)
                for (int c = 0; c < 4; c++) ch[c][at + k] = inter[k * TracedReverb.Channels + c];
            Phonon.iplAmbisonicsDecodeEffectApply(decNorth, ref facingNorth, ref outBuf, ref stBuf);
            Phonon.iplAudioBufferInterleave(ctx, ref stBuf, stI);
            for (int k = 0; k < fs && at + k < total; k++) { north[at + k, 0] = stI[k * 2]; north[at + k, 1] = stI[k * 2 + 1]; }
            Phonon.iplAmbisonicsDecodeEffectApply(decWest, ref facingWest, ref outBuf, ref stBuf);
            Phonon.iplAudioBufferInterleave(ctx, ref stBuf, stI);
            for (int k = 0; k < fs && at + k < total; k++) { west[at + k, 0] = stI[k * 2]; west[at + k, 1] = stI[k * 2 + 1]; }
            at += fs;
        }

        // The two arrivals: the loudest W in 8-15 ms and in 15-24 ms.
        int Peak(double fromMs, double toMs)
        {
            int i0 = (int)(fromMs * 44.1), i1 = (int)(toMs * 44.1), best = i0;
            for (int i = i0; i < i1; i++) if (MathF.Abs(ch[0][i]) > MathF.Abs(ch[0][best])) best = i;
            return best;
        }
        double Agree(int c, int centre)
        {
            double s = 0, e = 0;
            for (int i = Math.Max(0, centre - 44); i < Math.Min(total, centre + 44); i++) { s += ch[c][i] * (double)ch[0][i]; e += ch[0][i] * (double)ch[0][i]; }
            return e > 0 ? s / e : 0;
        }
        double Ear(float[,] st, int ear, int centre)
        {
            double e = 0;
            for (int i = Math.Max(0, centre - 66); i < Math.Min(total, centre + 132); i++) e += st[i, ear] * (double)st[i, ear];
            return 10 * Math.Log10(e + 1e-20);
        }
        int left = Peak(8, 15), ahead = Peak(15, 24);
        double yLeft = Agree(1, left), xAhead = Agree(3, ahead);
        double nL = Ear(north, 0, left), nR = Ear(north, 1, left);
        double wL = Ear(west, 0, ahead), wR = Ear(west, 1, ahead);
        Console.WriteLine($"Steam Audio world {(Phonon.MirrorZ ? "mirrored in z (Phonon.World)" : "UNFLIPPED (SA_MIRROR=0)")}; order {TracedReverb.Order}");
        Console.WriteLine($"  left wall  at {left / 44.1:F1} ms: Y/W {yLeft:+0.00;-0.00}  -> {(yLeft > 0.1 ? "LEFT   PASS" : "not left  FAIL")}");
        Console.WriteLine($"  wall ahead at {ahead / 44.1:F1} ms: X/W {xAhead:+0.00;-0.00}  -> {(xAhead > 0.1 ? "AHEAD  PASS" : xAhead < -0.1 ? "BEHIND FAIL" : "unclear FAIL")}");
        Console.WriteLine($"  decoded, facing north: left wall L {nL:F1} / R {nR:F1} dB -> {(nL - nR > 1 ? "left ear PASS" : "FAIL")}");
        Console.WriteLine($"  decoded, facing west:  wall ahead (now on the right) L {wL:F1} / R {wR:F1} dB -> {(wR - wL > 1 ? "right ear PASS" : "FAIL")}");
        bool ok = yLeft > 0.1 && xAhead > 0.1 && nL - nR > 1 && wR - wL > 1;
        Phonon.iplReflectionEffectRelease(ref effect);
        return ok ? 0 : 2;
    }
}
