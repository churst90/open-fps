using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The whole live vehicle voice, both taps as the game plays them, heard from in front of the car
/// and from behind it while it idles, and as it creeps past at 10 km/h.
///
/// "The front of the car seems to be quiet ... it can't be just all exhaust" (Cody, 2026-10-05).
/// The yardsticks are NHTSA's quiet-vehicle measurements (FMVSS 141 final rule, 81 FR 90416,
/// 14 December 2016): a stationary ICE car is 6 to 10 dB quieter behind than directly in front, and
/// ICE cars passing at 10 km/h measure 56.6 to 59.9 dB(A) on the pass-by microphones (2 m from the
/// path's centreline, 1.2 m up).
///
///   --car-fronts [preset ...] [out=DIR] [raw=DIR] [tag=NAME] [ambient=C] [sec=S] [nowav] [level=game|physical]
///   --car-fronts tailpipe [preset ...]     the tailpipe alone at idle and at the ISO 5130 stationary-test speed
///   --car-fronts fan [preset ...]          the cooling fan alone
///
/// level=game (the default) renders what the game plays: the live voice's idle lift
/// (CompensateLevel) at the loudness law in force (Loudness.DynamicRangeCompression, 0.45 unless
/// OPENFPS_LEVEL_COMPRESSION says otherwise), each outlet placed as ClientAudioSystem places it
/// (Loudness.Place at the vehicle's declared level, widened to the distance between its outlets) and
/// rolled off by Loudness.RenderedGain, then the master's trim and makeup (+9 dB); samples are the
/// game's own full scale at its output, the WAVs unscaled. No room, no reflections, no other sound.
/// level=physical renders pascals with spherical spreading from each outlet, the WAVs at 1 Pa = 0.1
/// full scale (94 dB SPL is -20 dBFS).
///
/// Writes, per preset, the mono signals (float32, at the mixer's 48 kHz) to DIR/raw for measuring, and binaural
/// WAVs to DIR for listening:
///   {preset}_{tag}_idle_front2m.wav  standing 2 m in front of the bumper, facing the car
///   {preset}_{tag}_idle_rear2m.wav   2 m behind the rear bumper, facing it
///   {preset}_{tag}_passby10.wav      at the kerb, 2 m from the path, the car passing at 10 km/h
/// The raw files are the two taps at a metre (.front/.rear) and what reaches the listener (.heard).
/// Physical levels throughout: no loudness law, no idle lift (CompensateLevel is off), no room.
/// </summary>
public static class CarFrontSpike
{
    const int Rate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate, Block = 512;   // the rate the game runs it at
    const float WavScale = 0.1f;
    const float C = 343f;
    /// <summary>bay=X: every voice's engine-bay leak forced to X, to bracket a value before declaring it.</summary>
    static float BayOverride = float.NaN;
    /// <summary>Render what the game plays (true) or physical pascals (false).</summary>
    static bool Game = true;

    public static int Run(string[] args)
    {
        // FAN-MODE-BEGIN
        if (args.Contains("fan")) return Fan(args.Where(x => !x.StartsWith("--") && x != "fan").ToArray());
        // FAN-MODE-END
        if (args.Contains("tailpipe")) return Tailpipe(args.Where(x => !x.StartsWith("--") && x != "tailpipe").ToArray());
        Game = Arg(args, "level=") != "physical";
        float wavScale = Game ? 1f : WavScale;
        string outDir = Arg(args, "out=") ?? "/tmp/claude-1000/car-fronts";
        string tag = Arg(args, "tag=") ?? "now";
        float ambient = float.TryParse(Arg(args, "ambient="), out var a) ? a : float.NaN;
        float seconds = float.TryParse(Arg(args, "sec="), out var s) ? s : 12f;
        bool wav = !args.Contains("nowav");
        BayOverride = float.TryParse(Arg(args, "bay="), out var bo) ? bo : float.NaN;
        var names = args.Where(x => !x.StartsWith("--") && !x.Contains('=') && x != "nowav").ToArray();
        Console.WriteLine(Game ? $"Game level: loudness law at {Loudness.DynamicRangeCompression:F2}, idle lift on; numbers are dBFS-referenced (0 dB = full scale)"
                               : "Physical level: pascals, dB SPL");
        if (names.Length == 0) names = new[] { "i4_economy", "i4_midsize", "v8_mild", "diesel_i4", "police_interceptor" };
        string raw = Arg(args, "raw=") ?? Path.Combine(outDir, "raw");
        Directory.CreateDirectory(raw);

        foreach (var n in names)
        {
            var v = MachineRegistry.VehicleFor(n);
            foreach (bool front in new[] { true, false })
            {
                var (f, r, heard, st) = Stationary(v, front, seconds, ambient);
                string pos = front ? "front2m" : "rear2m";
                Dump(Path.Combine(raw, $"{n}.{tag}.idle_{pos}.front.f32"), f);
                Dump(Path.Combine(raw, $"{n}.{tag}.idle_{pos}.rear.f32"), r);
                Dump(Path.Combine(raw, $"{n}.{tag}.idle_{pos}.heard.f32"), heard);
                if (wav) PassBySpike.Wav(Path.Combine(outDir, $"{n}_{tag}_idle_{pos}.wav"), st.Select(x => x * wavScale).ToArray());
                Console.WriteLine($"{n,-20} idle {pos}: taps at 1 m rear {Db(r),5:F1} front {Db(f),5:F1}; heard {Db(heard),5:F1} dB (unweighted{(Game ? ", game: dBFS + 94" : "")})");
            }
            var (ph, pst, peak) = PassBy(v, 10f / 3.6f, 2f, ambient);
            Dump(Path.Combine(raw, $"{n}.{tag}.passby10.heard.f32"), ph);
            if (wav) PassBySpike.Wav(Path.Combine(outDir, $"{n}_{tag}_passby10.wav"), pst.Select(x => x * wavScale).ToArray());
            Console.WriteLine($"{n,-20} pass-by 10 km/h at 2 m: {Db(ph),5:F1} dB over 16 s, peak sample {peak:F3}{(Game ? " FS" : $" Pa x {WavScale}")}");
        }
        return 0;
    }

    // FAN-METHOD-BEGIN
    /// <summary>
    /// The cooling fan alone (no bay, no tyres) at a metre from the front voice, idling: off on a
    /// mild day, on low for the air conditioning on a hot one, and on high with the coolant hot.
    /// Unweighted and A-weighted (the A-weighting by octave, from the dumped file, is fronts.py's).
    /// </summary>
    static int Fan(string[] names)
    {
        if (names.Length == 0) names = new[] { "i4_economy" };
        foreach (var n in names)
        {
            var v = MachineRegistry.VehicleFor(n);
            if (v.ElectricFan is { } relay)
            {
                // Left idling on a mild day: when does the coolant bring the fan in, and for how long?
                var cs = new OpenFPS.Client.AudioEngine.Core.Engine.CoolingSystem(relay, v.Engine, 11) { AmbientCelsius = 20f };
                float on = -1f, off = -1f;
                for (float t = 0f; t < 3600f; t += 0.1f)
                {
                    cs.Step(0.1f, v.Engine.IdleRpm, 0f, 0f);
                    if (on < 0f && cs.FanSpeedFraction > 0.01f) on = t;
                    if (on >= 0f && off < 0f && cs.FanSpeedFraction <= 0.01f) { off = t; break; }
                }
                Console.WriteLine($"{n,-20} idling at 20 C: fan first on after {(on < 0f ? "never (an hour)" : $"{on:F0} s")}"
                                  + (off > 0f ? $", off again {off - on:F0} s later" : ""));
            }
            foreach (var (label, ambient, hot) in new[] { ("mild", 20f, false), ("aircon", 32f, false), ("hot coolant", 20f, true) })
            {
                var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = 0f, SplitVoices = true, AmbientCelsius = ambient };
                voice.BayLeakage = 0f; voice.TyreMix = 0f;
                voice.PlaceAtSpeed(0f); voice.Revive();
                var tap = new EngineTapState(voice);
                // On the axis in front, and in the plane of the fan beside the car.
                foreach (var (where, at) in new[] { ("ahead", new Vector3(0f, 0.7f, v.FrontTapZ + 1f)), ("beside", new Vector3(1f, 0.7f, v.FrontTapZ)) })
                {
                    voice.SetListener(at - v.ExhaustSlot);
                    var fb = new float[Block]; var rb = new float[Block];
                    double e = 0; long cnt = 0;
                    for (int b = 0; b < Rate * 6 / Block; b++)
                    {
                        if (hot) voice.Cooling?.SetCelsius(110f);
                        voice.Produce(); tap.Render(fb); voice.Consume(rb);
                        if (b < Rate * 3 / Block) continue;
                        foreach (var y in fb) { double p = y * voice.PascalsAtFullScale; e += p * p; cnt++; }
                    }
                    var c = voice.Cooling;
                    Console.WriteLine($"{n,-20} {label,-12} {where,-7} fan {c?.FanSpeedFraction ?? 0f:F2}: {10 * Math.Log10(e / Math.Max(1, cnt) / 4e-10):F1} dB at 1 m");
                }
            }
        }
        return 0;
    }

    // FAN-METHOD-END

    /// <summary>
    /// The tailpipe alone (VehicleSynth, the offline bench: the exhaust and the body it shakes, no
    /// directivity), at idle and held at the ISO 5130 / UN R51 stationary-test speed with the throttle
    /// barely open (no load): three quarters of the rated-power speed up to 5,000 rpm, 3,750 between
    /// 5,000 and 7,500, half above. The rated-power speed is taken as the redline, which puts the
    /// test speed a little high. Octave bands and dB(A) at a metre; the test's microphone is at
    /// 0.5 m, about 6 dB more.
    /// </summary>
    static int Tailpipe(string[] names)
    {
        if (names.Length == 0) names = new[] { "i4_economy", "i4_midsize", "v8_mild", "diesel_i4", "police_interceptor" };
        int[] oct = { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000 };
        Console.WriteLine($"{"preset",-20} {"state",-16} {"dBZ",5} {"dBA",5}  " + string.Join(" ", oct.Select(o => $"{o,5}")));
        foreach (var n in names)
        {
            var v = MachineRegistry.VehicleFor(n);
            float s = v.Engine.RedlineRpm;
            float test = s <= 5000f ? 0.75f * s : s < 7500f ? 3750f : 0.5f * s;
            foreach (var (label, orders) in new[]
            {
                ("idle", new List<OpenFPS.Client.AudioEngine.Core.Engine.DriveOrder>
                    { new(OpenFPS.Client.AudioEngine.Core.Engine.DriverAction.Cranking, 0.8f), new(OpenFPS.Client.AudioEngine.Core.Engine.DriverAction.Idling, 6f) }),
                ($"held {test:F0} rpm", new List<OpenFPS.Client.AudioEngine.Core.Engine.DriveOrder>
                    { new(OpenFPS.Client.AudioEngine.Core.Engine.DriverAction.Cranking, 0.8f), new(OpenFPS.Client.AudioEngine.Core.Engine.DriverAction.Idling, 1f),
                      new(OpenFPS.Client.AudioEngine.Core.Engine.DriverAction.Holding, 5f, test, 0.12f) }),
            })
            {
                var r = OpenFPS.Client.AudioEngine.Core.VehicleSynth.Render(v, orders, seed: 5);
                int from = r.Exhaust.Length - 3 * Rate;
                var x = new float[3 * Rate];
                for (int i = 0; i < x.Length; i++) x[i] = r.Exhaust[from + i] * r.PascalsPerUnit;
                var (z, aw, bands) = Bands(x, oct);
                Console.WriteLine($"{n,-20} {label,-16} {z,5:F1} {aw,5:F1}  " + string.Join(" ", bands.Select(b => $"{b,5:F1}")));
            }
        }
        return 0;
    }

    /// <summary>Unweighted and A-weighted level and octave bands, dB SPL, by FFT.</summary>
    static (double Z, double A, double[] Bands) Bands(float[] x, int[] oct)
    {
        int n = 1; while (n * 2 <= x.Length) n *= 2;
        var re = new double[n]; var im = new double[n];
        for (int i = 0; i < n; i++) re[i] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        Fft(re, im);
        double total = 0, a = 0; var bands = new double[oct.Length];
        double norm = 0; for (int i = 0; i < n; i++) norm += x[i] * (double)x[i];
        double ms = norm / n, sum = 0;
        var p = new double[n / 2];
        for (int k = 1; k < n / 2; k++) { p[k] = re[k] * re[k] + im[k] * im[k]; sum += p[k]; }
        for (int k = 1; k < n / 2; k++)
        {
            double e = p[k] / Math.Max(1e-30, sum) * ms, f = k * (double)Rate / n;
            total += e;
            double f2 = f * f;
            double ra = 12194.0 * 12194 * f2 * f2 / ((f2 + 20.6 * 20.6) * Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9)) * (f2 + 12194.0 * 12194));
            a += e * ra * ra * Math.Pow(10, 0.2);
            for (int b = 0; b < oct.Length; b++) if (f >= oct[b] / Math.Sqrt(2) && f < oct[b] * Math.Sqrt(2)) bands[b] += e;
        }
        double Db(double e) => 10 * Math.Log10(e / 4e-10 + 1e-30);
        return (Db(total), Db(a), bands.Select(Db).ToArray());
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < len / 2; k++)
                {
                    double wr = Math.Cos(ang * k), wi = Math.Sin(ang * k);
                    double xr = re[i + k + len / 2] * wr - im[i + k + len / 2] * wi, xi = re[i + k + len / 2] * wi + im[i + k + len / 2] * wr;
                    re[i + k + len / 2] = re[i + k] - xr; im[i + k + len / 2] = im[i + k] - xi;
                    re[i + k] += xr; im[i + k] += xi;
                }
        }
    }

    static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key))?[key.Length..];

    static float Db(float[] x)
    {
        double e = 0; foreach (var y in x) e += y * (double)y;
        return (float)(10 * Math.Log10(e / Math.Max(1, x.Length) / (20e-6 * 20e-6) + 1e-30));
    }

    static void Dump(string path, float[] x)
    {
        using var w = new BinaryWriter(File.Create(path));
        foreach (var y in x) w.Write(y);
    }

    static Vector3 FrontTap(VehicleProfile v) => new(0f, v.FrontTapHeight, v.FrontTapZ);

    static EngineVoiceState Voice(VehicleProfile v, float speed, float ambient)
    {
        var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed, SplitVoices = true, CompensateLevel = Game };
        // By name, so this instrument also builds against a tree from before the field existed.
        if (!float.IsNaN(ambient)) typeof(EngineVoiceState).GetField("AmbientCelsius")?.SetValue(voice, ambient);
        if (!float.IsNaN(BayOverride)) voice.BayLeakage = BayOverride;
        voice.PlaceAtSpeed(speed);
        voice.Revive();
        return voice;
    }

    /// <summary>
    /// The car standing at idle, the listener 2 m beyond one bumper at ear height (1.2 m) on the
    /// centreline, facing it. Returns both taps at a metre, the sum as it reaches the listener
    /// (spherical spreading and flight time from each tap), and that sum binaurally.
    /// </summary>
    static (float[] Front, float[] Rear, float[] Heard, float[] Stereo) Stationary(VehicleProfile v, bool inFront, float seconds, float ambient)
    {
        var voice = Voice(v, 0f, ambient);
        var tap = new EngineTapState(voice);
        float half = v.LengthMetres * 0.5f;
        var listener = new Vector3(0f, 1.2f, inFront ? half + 2f : -half - 2f);
        voice.SetListener(listener - v.ExhaustSlot);
        int warm = Rate / Block, blocks = (int)(seconds * Rate / Block);
        var fb = new float[Block]; var rb = new float[Block];
        var front = new float[blocks * Block]; var rear = new float[blocks * Block];
        float scale = Game ? 1f : voice.PascalsAtFullScale;
        for (int b = 0; b < warm + blocks; b++)
        {
            voice.Produce(); tap.Render(fb); voice.Consume(rb);
            if (b < warm) continue;
            for (int i = 0; i < Block; i++) { front[(b - warm) * Block + i] = fb[i] * scale; rear[(b - warm) * Block + i] = rb[i] * scale; }
        }
        Console.WriteLine($"  {(inFront ? "in front" : "behind  ")}: {State(voice)}");
        var ft = FrontTap(v); var rt = v.ExhaustSlot;
        float dF = Vector3.Distance(listener, ft), dR = Vector3.Distance(listener, rt);
        var fh = Delay(v, front, dF); var rh = Delay(v, rear, dR);
        var heard = new float[front.Length];
        for (int i = 0; i < heard.Length; i++) heard[i] = fh[i] + rh[i];
        // Facing the car: in the listener's frame (Steam Audio: +x right, -z ahead, y up) the taps
        // are straight ahead, a little below.
        Phonon.IPLVector3 Dir(Vector3 p)
        {
            var d = p - listener;
            float sign = inFront ? 1f : -1f;      // facing -z in front of the car, +z behind it
            var s = Vector3.Normalize(new Vector3(sign * d.X, d.Y, sign * d.Z));
            return new Phonon.IPLVector3 { x = s.X, y = s.Y, z = s.Z };
        }
        var st = Binaural(fh, Enumerable.Repeat(Dir(ft), fh.Length).ToArray(), rh, Enumerable.Repeat(Dir(rt), rh.Length).ToArray());
        return (front, rear, heard, st);
    }

    /// <summary>The flight time and the spreading for a fixed distance (see <see cref="Spread"/>).</summary>
    static float[] Delay(VehicleProfile v, float[] x, float d)
    {
        int lag = (int)MathF.Round(d / C * Rate);
        float g = Spread(v, d);
        var y = new float[x.Length];
        for (int i = lag; i < x.Length; i++) y[i] = x[i - lag] * g;
        return y;
    }

    /// <summary>
    /// What distance does to one outlet heard <paramref name="d"/> metres away. Physical: spherical
    /// spreading from a metre. Game: the placement ClientAudioSystem gives a vehicle's outlets
    /// (Loudness.Place at its declared level, widened to the distance between them, the same for
    /// both) and the mixer's rolloff, Loudness.RenderedGain.
    /// </summary>
    static float Spread(VehicleProfile v, float d)
    {
        if (!Game) return 1f / MathF.Max(0.5f, d);
        float extent = Vector3.Distance(v.ExhaustSlot, FrontTap(v));
        var (gain, reference) = Loudness.Place(v.SourceLevelDb, extent);
        return Loudness.RenderedGain(gain, reference, Loudness.AudibleRange(v.SourceLevelDb), d) * MasterGain;
    }

    /// <summary>The master: FmodAudioProvider's trim and the limiter's makeup, which at these levels
    /// never limits.</summary>
    static float MasterGain => MathF.Pow(10f, (FmodAudioProvider.MasterTrimDb + FmodAudioProvider.MasterMakeupDb) / 20f);

    /// <summary>The bay's shading and the cooling system's state, read by name so the instrument
    /// builds against older trees.</summary>
    static string State(EngineVoiceState voice)
    {
        string text = "";
        var bay = typeof(EngineVoiceState).GetProperty("BayRadiation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(voice);
        if (bay?.GetType().GetProperty("Target")?.GetValue(bay) is ValueTuple<float, float, float> t)
            text += $"bay through the body {20 * MathF.Log10(t.Item1):F1} / {20 * MathF.Log10(t.Item2):F1} / {20 * MathF.Log10(t.Item3):F1} dB (low/mid/high)";
        var c = voice.Cooling;
        if (c != null)
        {
            text += $"; coolant {c.Celsius:F1} C, fan {c.FanSpeedFraction:F2} of full speed";
            var ac = c.GetType().GetProperty("AirConditioning")?.GetValue(c);
            if (ac is bool on) text += $", air con {(on ? "on" : "off")}";
        }
        return text;
    }

    static float[] Binaural(float[] a, Phonon.IPLVector3[] da, float[] b, Phonon.IPLVector3[] db)
    {
        var sa = PassBySpike.Render(a, da, 256, Phonon.IPL_HRTFINTERPOLATION_BILINEAR);
        var sb = PassBySpike.Render(b, db, 256, Phonon.IPL_HRTFINTERPOLATION_BILINEAR);
        for (int i = 0; i < sa.Length; i++) sa[i] += sb[i];
        return sa;
    }

    /// <summary>
    /// The car creeping past at a steady speed, the listener at the kerb 2 m from the path's
    /// centreline, 1.2 m up, facing the road. Each tap is heard from where it is at the moment it
    /// emitted (a moving source's own flight time, so the Doppler is in it), spread from a metre.
    /// </summary>
    static (float[] Heard, float[] Stereo, float Peak) PassBy(VehicleProfile v, float speed, float lateral, float ambient)
    {
        const float seconds = 16f;
        var voice = Voice(v, speed, ambient);
        var tap = new EngineTapState(voice);
        int blocks = (int)(seconds * Rate / Block), warm = Rate / Block, n = blocks * Block;
        var fb = new float[Block]; var rb = new float[Block];
        var front = new float[n]; var rear = new float[n];
        float scale = Game ? 1f : voice.PascalsAtFullScale;
        // World: the road along +E, the car's centre at E = speed (t - T/2), N = 0; the listener at
        // E = 0, N = -lateral. The car faces +E, so its own frame is z = E, y = up, x = -N.
        Vector3 Listener(float tCar) => new(lateral, 1.2f, -speed * (tCar - seconds / 2f));
        for (int b = 0; b < warm + blocks; b++)
        {
            float t = (b - warm) * Block / (float)Rate;
            voice.SetListener(Listener(t) - v.ExhaustSlot);
            voice.Produce(); tap.Render(fb); voice.Consume(rb);
            if (b < warm) continue;
            for (int i = 0; i < Block; i++) { front[(b - warm) * Block + i] = fb[i] * scale; rear[(b - warm) * Block + i] = rb[i] * scale; }
        }
        // Received at the listener: for each output sample, find the emission time by iterating the
        // flight time from the tap's position (two passes is plenty at 3 m/s).
        var outF = new float[n]; var outR = new float[n];
        var dirF = new Phonon.IPLVector3[n]; var dirR = new Phonon.IPLVector3[n];
        var lis = new Vector3(0f, 1.2f, -lateral);      // (E, H, N)
        Vector3 World(Vector3 tapLocal, float t) => new(speed * (t - seconds / 2f) + tapLocal.Z, tapLocal.Y, -tapLocal.X);
        void Hear(float[] src, Vector3 local, float[] dst, Phonon.IPLVector3[] dir)
        {
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate, te = t;
                Vector3 p = default;
                for (int k = 0; k < 3; k++) { p = World(local, te); te = t - Vector3.Distance(p, lis) / C; }
                float d = Vector3.Distance(p, lis);
                float x = te * Rate;
                int i0 = (int)MathF.Floor(x);
                if (i0 < 0 || i0 + 1 >= n) continue;
                float fr = x - i0;
                dst[i] = (src[i0] * (1f - fr) + src[i0 + 1] * fr) * Spread(v, d);
                // Facing the road (+N): right is +E, ahead is +N.
                var rel = Vector3.Normalize(p - lis);
                dir[i] = new Phonon.IPLVector3 { x = rel.X, y = rel.Y, z = -rel.Z };
            }
        }
        Hear(front, FrontTap(v), outF, dirF);
        Hear(rear, v.ExhaustSlot, outR, dirR);
        var heard = new float[n];
        for (int i = 0; i < n; i++) heard[i] = outF[i] + outR[i];
        var st = Binaural(outF, dirF, outR, dirR);
        return (heard, st, st.Max(MathF.Abs));
    }
}
