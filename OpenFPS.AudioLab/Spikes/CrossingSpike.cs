using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Pneumatics;
using OpenFPS.Client.AudioEngine.Core.Rail;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Air, and a grade crossing to hear it at.
///
///   --airbrake [tractor_trailer|transit_bus|locomotive]
///   --crossing [train preset] [speed=m/s] [sec=s]
///
/// The crossing is the scene this whole session was for, because it is where all of it has to work
/// at once and at the right relative levels: a bronze gong two seconds into its ring, a lorry and a
/// bus standing on their brakes with their air going, a five-chime horn a quarter of a mile out, and
/// then a hundred and seventy metres of train going through the middle of it. Nothing in it is a
/// recording and nothing in it is sequenced against anything else — the gong rings because the
/// circuit is down, the horn sounds because the rule says a quarter mile, and the clatter is where
/// the axles are.
/// </summary>
public static class CrossingSpike
{
    private const int Sr = VehicleSynth.SampleRate;

    // ═══ air brakes on their own ═══════════════════════════════════════════════════════════════

    public static int RunAirBrake(string[] args)
    {
        AcousticRegistry.Initialize();
        var keys = args.Where(a => ModelLibrary.Knows(ModelLibrary.Kinds.Air, a)).ToList();
        if (keys.Count == 0) keys = ModelLibrary.Ids(ModelLibrary.Kinds.Air).ToList();
        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "AIR");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  Compressed air, at three metres.\n");

        foreach (var key in keys)
        {
            var spec = ModelLibrary.Air(key);
            var sys = new AirSystem(spec, Sr, 61) { EngineRpm = 700f };
            Console.WriteLine($"  {key}");
            foreach (var l in sys.Describe()) Console.WriteLine($"    {l}");

            // One of everything, with room to hear each die away.
            var script = new List<(float At, string Port, string What)>();
            float t = 0.6f;
            foreach (var port in spec.Ports)
            {
                script.Add((t, port.Name, port.Name));
                t += Math.Max(2.2f, new AirPort(port, spec.JetTrimDb, Sr, 1).BlowdownSeconds * 2.2f + 1.4f);
            }
            float seconds = t + 1.5f;

            int n = (int)(seconds * Sr);
            var pcm = new float[n];
            int next = 0;
            double p2 = 0; int pn = 0;
            for (int i = 0; i < n; i++)
            {
                float now = i / (float)Sr;
                if (next < script.Count && now >= script[next].At)
                {
                    sys.Vent(script[next].Port);
                    Console.WriteLine($"    {script[next].At,5:F1}s  {script[next].What}");
                    next++;
                }
                float s = sys.Step() / 3f;    // heard at three metres
                pcm[i] = s;
                p2 += s * (double)s; pn++;
            }
            float peak = 0f; foreach (var x in pcm) peak = MathF.Max(peak, MathF.Abs(x));
            Console.WriteLine($"    at 3 m: peak {Db(peak):F0} dB, rms over the whole sequence {Db((float)Math.Sqrt(p2 / pn)):F0} dB");
            var offsets = Enumerable.Range(1, 30).Select(i => (int)(n * i / 32f)).ToArray();
            Console.WriteLine("    bands: " + string.Join("  ", Spectrum.AverageBandsDb(pcm, Sr, offsets).Select((d, i) => $"{Spectrum.BandEdges[i]:F0}:{d:F0}")));
            Write(dir, $"air_{key}", pcm);
            Console.WriteLine();
        }
        return 0;
    }

    private static float Db(float p) => 20f * MathF.Log10(MathF.Max(1e-9f, p) / 2e-5f);

    private static void Write(string dir, string name, float[] pcm)
    {
        float peak = 0f; foreach (var x in pcm) peak = MathF.Max(peak, MathF.Abs(x));
        var wav = new float[pcm.Length];
        float g = peak > 1e-12f ? 0.89f / peak : 0f;
        for (int i = 0; i < wav.Length; i++) wav[i] = pcm[i] * g;
        File.WriteAllBytes(Path.Combine(dir, name + ".wav"), VehicleSynth.ToWav16(wav));
        Console.WriteLine($"    wrote {Path.Combine(dir, name + ".wav")}");
    }

    // ═══ the crossing ══════════════════════════════════════════════════════════════════════════

    /// <summary>Anything in the scene that makes a noise somewhere.</summary>
    private sealed class Placed
    {
        public required string Label { get; init; }
        public required Func<float, Vector3> Where { get; init; }   // of time
        public required Func<float> Render { get; init; }
        public float Extent { get; init; } = 1.5f;
        public float Lp1, Lp2, Gp1, Gp2;
    }

    public static int RunCrossing(string[] args)
    {
        AcousticRegistry.Initialize();
        string key = args.FirstOrDefault(a => ModelLibrary.Knows(ModelLibrary.Kinds.Train, a)) ?? "amtrak";
        var profile = ModelLibrary.Train(key);
        float speed = TrainSpike.Arg(args, "speed", profile.TypicalSpeedMps);
        float seconds = TrainSpike.Arg(args, "sec", 46f);

        // The geometry. You are STANDING AT THE GATES, which is the only place most people have
        // ever heard a crossing bell: a couple of metres from the mast it is bolted to, ten metres
        // back from the rails, with the road beside you. Put the listener on the far footway instead
        // and the gong is seventeen metres off and twenty decibels down, which is correct and is not
        // what anybody means by "a train passing with a crossing bell".
        float trackZ = TrainSpike.Arg(args, "track", 11f);   // rails, metres from the listener
        float mastDist = TrainSpike.Arg(args, "mast", 2.6f); // the signal mast, metres away
        var ear = new Vector3(0f, 1.6f, 0f);
        const float roadX = 7f;         // the road crosses the track this far along +x
        float stopLineZ = trackZ - 6f;  // where road vehicles wait, on the listener's side
        // The mast stands beside the road on the near side of the track, and the listener is on the
        // footway just short of it.
        var mastAt = new Vector3(MathF.Sqrt(MathF.Max(0.01f, mastDist * mastDist - 4f)) * 0.8f, 3.6f, 1.2f);

        var train = new TrainSynth(profile, Sr, 41) { Speed = speed, Notch = 7f };
        var gong = new StruckBell(ModelLibrary.Bell("crossing_gong"), Sr, 31);
        var truckAir = new AirSystem(ModelLibrary.Air("tractor_trailer"), Sr, 71) { EngineRpm = 650f };
        var busAir = new AirSystem(ModelLibrary.Air("transit_bus"), Sr, 83) { EngineRpm = 700f };
        var truckEngine = new EngineSynth(EngineProfile.ByName("diesel_truck"), Sr, 91) { Ignition = true };
        var busEngine = new EngineSynth(EngineProfile.ByName("diesel_bus"), Sr, 97) { Ignition = true };
        var truckHorn = new ChimeHorn(ModelLibrary.Horn("truck_dual"), Sr, 103);

        // When everything happens. The train is abeam the crossing half way through.
        float pass = seconds * 0.5f;
        float gongOn = pass - 24f, gongOff = pass + profile.LengthMetres / speed + 2.5f;
        float hornAt = pass - 400f / MathF.Max(1f, speed);

        Console.WriteLine($"\n  A grade crossing. {profile.Name}, {speed * 3.6f:F0} km/h, {seconds:F0} s.\n");
        Console.WriteLine($"    standing {Vector3.Distance(mastAt, ear):F1} m from the signal mast and {trackZ:F0} m from the rails");
        Console.WriteLine($"    the gong rings from {gongOn:F1}s to {gongOff:F1}s (the circuit is down, and it stays down until the train is clear)");
        Console.WriteLine($"    the horn starts at {hornAt:F1}s — a quarter of a mile out at this speed, which is what the rule says");
        Console.WriteLine($"    the train is abeam at {pass:F1}s and the tail clears at {pass + profile.LengthMetres / speed:F1}s");

        // Road vehicle positions: they roll up, stop at the line, and pull away when it is clear.
        float truckStop = gongOn + 3.0f, truckGo = gongOff + 1.2f;
        float busStop = gongOn + 6.5f, busGo = gongOff + 3.0f;
        Func<float, float, float, float, float> approach = (t, stopAt, goAt, len) =>
        {
            // Metres along the road from the stop line: negative is short of it (still coming).
            if (t < stopAt) return -Math.Max(0f, (stopAt - t) * (stopAt - t) * 1.4f);
            if (t < goAt) return 0f;
            float dt = t - goAt;
            return 0.55f * dt * dt;
        };

        var placed = new List<Placed>
        {
            new()
            {
                Label = "crossing gong",
                Where = _ => mastAt,
                Extent = 0.4f,
                Render = () => { gong.Step(); return gong.Out; },
            },
            new()
            {
                Label = "tractor unit",
                Where = t => new Vector3(roadX + 1.2f, 1.0f, stopLineZ - approach(t, truckStop, truckGo, 16f)),
                Extent = 2.5f,
                Render = () => truckEngine.Exhaust + 0.5f * truckEngine.Intake + 0.8f * truckEngine.Block,
            },
            new()
            {
                Label = "trailer air",
                Where = t => new Vector3(roadX + 1.2f, 0.8f, stopLineZ + 11f - approach(t, truckStop, truckGo, 16f)),
                Extent = 1.0f,
                Render = () => truckAir.Step(),
            },
            new()
            {
                Label = "bus",
                Where = t => new Vector3(roadX - 1.6f, 1.0f, stopLineZ + 5f - approach(t, busStop, busGo, 12f)),
                Extent = 2.5f,
                Render = () => busEngine.Exhaust + 0.5f * busEngine.Intake + 0.8f * busEngine.Block,
            },
            new()
            {
                Label = "bus air",
                Where = t => new Vector3(roadX - 1.6f, 0.6f, stopLineZ + 3f - approach(t, busStop, busGo, 12f)),
                Extent = 1.0f,
                Render = () => busAir.Step(),
            },
            new()
            {
                Label = "truck horn",
                Where = t => new Vector3(roadX + 1.2f, 3.4f, stopLineZ - approach(t, truckStop, truckGo, 16f)),
                Extent = 0.5f,
                Render = () => { truckHorn.Step(); return truckHorn.Out; },
            },
        };

        int n = (int)(seconds * Sr);
        var chL = new float[n + Sr * 3];
        var chR = new float[n + Sr * 3];
        train.Place(-speed * pass);
        train.BellRinging = true;

        // Facing the track, so the train comes from the left and goes to the right.
        var (earL, earR) = HeadShadow.Ears(ear, new Vector3(0f, 0f, 1f), Vector3.UnitY);
        var trainState = train.Sources.Select(_ => new EarState(Sr)).ToArray();
        var placedState = new Dictionary<Placed, EarState>();
        var log = new List<string>();
        bool[] done = new bool[16];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; k < n; k++)
        {
            float t = k / (float)Sr;

            // ── what is happening ───────────────────────────────────────────────────────────────
            gong.Ringing = t >= gongOn && t < gongOff;
            bool hornOn = t >= hornAt && ((t - hornAt) % 2.9f) < (((t - hornAt) < 8.7f) ? 2.2f : 4.6f);
            if (t - hornAt > 5.8f && t - hornAt < 6.5f) hornOn = true;    // the short one
            train.HornBlowing = t >= hornAt && t < hornAt + 12.5f && hornOn;
            train.WhistleBlowing = train.HornBlowing;

            // The truck: rolls up, brakes, stands with the engine idling, then releases and pulls away.
            float rpmTruck = t < truckStop ? 1150f - 350f * (t / Math.Max(0.1f, truckStop))
                           : t < truckGo ? 620f
                           : 620f + 900f * MathF.Min(1f, (t - truckGo) / 4f);
            truckEngine.Throttle = Math.Clamp((rpmTruck - truckEngine.Rpm) / 900f + 0.12f, 0.04f, 1f);
            truckEngine.LoadTorque = truckEngine.Profile.PeakTorqueNm * (t > truckGo ? 0.55f : 0.06f);
            truckEngine.Step();
            truckAir.EngineRpm = truckEngine.Rpm;
            Fire(ref done[0], t, truckStop - 1.6f, () => { truckAir.Apply(); log.Add($"{t,5:F1}s  truck brakes on"); });
            Fire(ref done[1], t, truckGo - 0.35f, () => { truckAir.Vent("service_release"); log.Add($"{t,5:F1}s  truck service release"); });
            Fire(ref done[2], t, truckGo + 0.9f, () => { truckAir.Vent("tractor_release"); log.Add($"{t,5:F1}s  truck tractor release"); });

            // The bus: rolls up, brakes, kneels and opens its door, shuts it, and goes.
            float rpmBus = t < busStop ? 1100f - 300f * (t / Math.Max(0.1f, busStop))
                         : t < busGo ? 700f
                         : 700f + 950f * MathF.Min(1f, (t - busGo) / 3.5f);
            busEngine.Throttle = Math.Clamp((rpmBus - busEngine.Rpm) / 900f + 0.12f, 0.04f, 1f);
            busEngine.LoadTorque = busEngine.Profile.PeakTorqueNm * (t > busGo ? 0.5f : 0.06f);
            busEngine.Step();
            busAir.EngineRpm = busEngine.Rpm;
            Fire(ref done[3], t, busStop - 1.2f, () => { busAir.Apply(); log.Add($"{t,5:F1}s  bus brakes on"); });
            Fire(ref done[4], t, busStop + 0.5f, () => { busAir.Vent("kneel"); log.Add($"{t,5:F1}s  bus kneels"); });
            Fire(ref done[5], t, busStop + 1.4f, () => { busAir.Vent("door"); log.Add($"{t,5:F1}s  bus door opens"); });
            Fire(ref done[6], t, busGo - 2.2f, () => { busAir.Vent("door"); log.Add($"{t,5:F1}s  bus door shuts"); });
            Fire(ref done[7], t, busGo - 0.4f, () => { busAir.Vent("service_release"); log.Add($"{t,5:F1}s  bus service release"); });
            Fire(ref done[8], t, busGo + 0.3f, () => { busAir.Vent("parking"); log.Add($"{t,5:F1}s  bus parking brake off"); });

            // Somebody leans on the horn when the gates take too long.
            truckHorn.Blowing = t > gongOff - 6.5f && t < gongOff - 5.9f;

            if ((k & 63) == 0)
            {
                float headX = (float)train.HeadMetres;
                train.SetListener(new Vector3(0f - headX, ear.Y - 4.8f, trackZ));
            }
            train.Step();
            float head = (float)train.HeadMetres;

            // ── deposit everything ──────────────────────────────────────────────────────────────
            for (int i = 0; i < train.Sources.Count; i++)
            {
                var src = train.Sources[i];
                float s = src.Out;
                if (s == 0f) continue;
                var at = new Vector3(head - src.AlongMetres, src.HeightMetres, trackZ);
                DepositBinaural(chL, chR, t, s, at, ear, earL, earR, MathF.Max(0.5f, src.ExtentMetres), trainState[i], Sr);
            }
            foreach (var p in placed)
            {
                float s = p.Render();
                if (s == 0f) continue;
                if (!placedState.TryGetValue(p, out var st)) placedState[p] = st = new EarState(Sr);
                DepositBinaural(chL, chR, t, s, p.Where(t), ear, earL, earR, p.Extent, st, Sr);
            }
        }

        foreach (var l in log.OrderBy(x => x)) Console.WriteLine("    " + l);

        float peak = 0f;
        for (int i = 0; i < chL.Length; i++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(chL[i]), MathF.Abs(chR[i])));
        Console.WriteLine($"    peak at the ear {Db(peak):F0} dB   ({sw.Elapsed.TotalSeconds:F0} s to render)");
        var offs = Enumerable.Range(1, 40).Select(i => (int)(chL.Length * i / 42f)).ToArray();
        Console.WriteLine("    bands: " + string.Join("  ", Spectrum.AverageBandsDb(chL, Sr, offs).Select((d, i) => $"{Spectrum.BandEdges[i]:F0}:{d:F0}")));

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "SCENE");
        Directory.CreateDirectory(dir);
        float g = peak > 1e-12f ? 0.89f / peak : 0f;
        var wl = new float[chL.Length]; var wr = new float[chR.Length];
        for (int i = 0; i < wl.Length; i++) { wl[i] = chL[i] * g; wr[i] = chR[i] * g; }
        string path = Path.Combine(dir, $"crossing_{key}.wav");
        File.WriteAllBytes(path, ToWav16Stereo(wl, wr, Sr));
        Console.WriteLine($"    wrote {path}  (binaural: true interaural time difference from the arrival times, "
                        + $"spherical-head shadow per ear, no pinna so no elevation)");
        return 0;
    }

    private static void Fire(ref bool done, float t, float at, Action a)
    {
        if (done || t < at) return;
        done = true;
        a();
    }

    private const float C = 343f;

    /// <summary>
    /// Everything one source needs to be heard by two ears: the air's loss along each path, and the
    /// head in the way of one of them. Four paths in all — direct and off the ground, left and
    /// right — because the ground bounce arrives from somewhere else and is shadowed differently.
    /// </summary>
    public sealed class EarState
    {
        public float Al1, Al2, Ar1, Ar2, Gl1, Gl2, Gr1, Gr2;
        public HeadShadow ShadowL, ShadowR, GroundL, GroundR;
        public int Tick;

        public EarState(float rate)
        {
            ShadowL = new HeadShadow(rate); ShadowR = new HeadShadow(rate);
            GroundL = new HeadShadow(rate); GroundR = new HeadShadow(rate);
        }
    }

    /// <summary>
    /// One source, one sample, two ears.
    ///
    /// The interaural TIME difference is not applied here, it HAPPENS: each ear is a different
    /// distance from the source, so each sample is deposited into each channel at its own arrival
    /// moment, and a source sweeping past produces exactly the continuous time difference it should,
    /// Doppler and all. The interaural LEVEL difference is the inverse distance plus the head in the
    /// way, and the head is a filter rather than a fader because diffraction is frequency-dependent.
    /// </summary>
    public static void DepositBinaural(float[] left, float[] right, float t, float s, Vector3 at,
                                       Vector3 head, Vector3 earL, Vector3 earR, float extent,
                                       EarState st, float rate)
    {
        var toSource = at - head;
        float hr = toSource.Length();
        if (hr > 1e-4f && (st.Tick++ & 63) == 0)
        {
            // The angle from each ear's outward axis to the source. The ear axis is the head's own
            // left and right, which here is the x axis.
            var dir = toSource / hr;
            float cosR = Math.Clamp(dir.X, -1f, 1f);
            st.ShadowR.SetAngle(MathF.Acos(cosR), rate);
            st.ShadowL.SetAngle(MathF.Acos(-cosR), rate);
            // The bounce comes up off the ground from the mirrored source, so it is shadowed by its
            // own angle and not by the direct path's.
            var gdir = Vector3.Normalize(new Vector3(toSource.X, -at.Y - head.Y, toSource.Z));
            float gcosR = Math.Clamp(gdir.X, -1f, 1f);
            st.GroundR.SetAngle(MathF.Acos(gcosR), rate);
            st.GroundL.SetAngle(MathF.Acos(-gcosR), rate);
        }

        // ── the direct path to each ear ─────────────────────────────────────────────────────────
        float rL = Vector3.Distance(at, earL), rR = Vector3.Distance(at, earR);
        float aL = 1f - MathF.Exp(-2f * MathF.PI * AirCorner(rL) / rate);
        float aR = 1f - MathF.Exp(-2f * MathF.PI * AirCorner(rR) / rate);
        st.Al1 += aL * (s - st.Al1); st.Al2 += aL * (st.Al1 - st.Al2);
        st.Ar1 += aR * (s - st.Ar1); st.Ar2 += aR * (st.Ar1 - st.Ar2);
        Deposit(left, (t + rL / C) * rate, st.ShadowL.Process(st.Al2) / MathF.Max(extent, rL));
        Deposit(right, (t + rR / C) * rate, st.ShadowR.Process(st.Ar2) / MathF.Max(extent, rR));

        // ── and the one off the ground ──────────────────────────────────────────────────────────
        var mirror = new Vector3(at.X, -at.Y, at.Z);
        float gL = Vector3.Distance(mirror, earL), gR = Vector3.Distance(mirror, earR);
        float bL = 1f - MathF.Exp(-2f * MathF.PI * (AirCorner(gL) * 0.55f) / rate);
        float bR = 1f - MathF.Exp(-2f * MathF.PI * (AirCorner(gR) * 0.55f) / rate);
        st.Gl1 += bL * (s - st.Gl1); st.Gl2 += bL * (st.Gl1 - st.Gl2);
        st.Gr1 += bR * (s - st.Gr1); st.Gr2 += bR * (st.Gr1 - st.Gr2);
        Deposit(left, (t + gL / C) * rate, 0.5f * st.GroundL.Process(st.Gl2) / MathF.Max(extent, gL));
        Deposit(right, (t + gR / C) * rate, 0.5f * st.GroundR.Process(st.Gr2) / MathF.Max(extent, gR));
    }

    private static float AirCorner(float r)
        => Math.Clamp(4000f * MathF.Pow(100f / MathF.Max(1f, r), 0.59f), 300f, 18000f);

    private static void Deposit(float[] buf, float at, float v)
    {
        int i = (int)at;
        if (i < 0 || i + 1 >= buf.Length) return;
        float f = at - i;
        buf[i] += v * (1f - f);
        buf[i + 1] += v * f;
    }

    /// <summary>Where the two ears are for a listener at this point facing the track.</summary>
    public static (Vector3 Left, Vector3 Right) HeadShadowEars(Vector3 head)
        => HeadShadow.Ears(head, new Vector3(0f, 0f, 1f), Vector3.UnitY);

    /// <summary>Two mono buffers as one interleaved 16-bit stereo WAV.</summary>
    public static byte[] ToWav16Stereo(float[] left, float[] right, int rate)
    {
        int frames = Math.Min(left.Length, right.Length);
        int dataBytes = frames * 4;
        var b = new byte[44 + dataBytes];
        void Str(int at, string v) { for (int i = 0; i < v.Length; i++) b[at + i] = (byte)v[i]; }
        void I32(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
        void I16(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
        Str(0, "RIFF"); I32(4, 36 + dataBytes); Str(8, "WAVE");
        Str(12, "fmt "); I32(16, 16); I16(20, 1); I16(22, 2);
        I32(24, rate); I32(28, rate * 4); I16(32, 4); I16(34, 16);
        Str(36, "data"); I32(40, dataBytes);
        for (int i = 0; i < frames; i++)
        {
            I16(44 + i * 4, (short)Math.Clamp((int)(left[i] * 32767f), -32768, 32767));
            I16(46 + i * 4, (short)Math.Clamp((int)(right[i] * 32767f), -32768, 32767));
        }
        return b;
    }
}
