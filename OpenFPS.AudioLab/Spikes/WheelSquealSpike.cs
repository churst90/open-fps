using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Each wheel squealing for itself (VehicleSynth.WheelSqueal through EngineVoiceState), measured and
/// rendered.
///
/// First the squeal of one tyre on its own, at three demands, measured: the band balance, the
/// spectral flatness (1 is white noise, 0 a pure tone) and how far the strongest component stands
/// above the band around it. Then four drives on the per-wheel model (WheelDynamics), its wheels sent
/// to a live engine voice exactly as the server sends them: an ordinary stop, a hard stop, a turn
/// tightened past the limit, and a pull-away with wheelspin. Each is rendered as two voices of the
/// same car, the listener walking alongside two and a half metres off its left side for the left
/// channel and off its right for the right, so which side is singing can be heard. Printed per
/// wheel: when it first reached the squeal onset and the most it was asked for.
///
///   --wheel-squeal [out=DIR]
/// </summary>
public static class WheelSquealSpike
{
    private const int Sr = VehicleSynth.SampleRate;
    private const int Tick = Sr / 30;
    private static readonly string[] WheelNames = { "front left", "front right", "rear left", "rear right" };
    private static readonly List<(string Name, float[] Left, float[] Right)> Rendered = new();
    /// <summary>Render with the axle voices squealing from the overall demand instead (no wheels to
    /// the voice), for comparison.</summary>
    public static bool AxleOnly;

    /// <summary>
    /// Heard from the pavement: the listener stands 7.5 m from the car's path, beside where its tyres
    /// work hardest, facing the road, and each end of the car (the rear voice, the front tap) goes
    /// through Steam Audio's binaural effect from where it is, with spherical spreading. No ground,
    /// walls or air: those the game adds. Files end in -binaural.wav.
    /// </summary>
    public static bool Binaural;

    public static int Run(string? outDir)
    {
        AcousticRegistry.Initialize();
        outDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "openfps-wheel-squeal");
        Directory.CreateDirectory(outDir);

        Console.WriteLine("\n  ONE TYRE'S SQUEAL, measured (i4_midsize's tyre, at its slip velocity reference, static load)\n");
        var car = VehicleProfile.ByName("i4_midsize");
        foreach (float d in new[] { 0.85f, 0.95f, 1.10f, 1.40f })
        {
            var v = new VehicleSynth.WheelSquealVoice { Demand = d, SlipVelocity = 1.4f };
            var rng = new Random(3);
            var buf = new float[Sr * 2];
            for (int i = 0; i < buf.Length; i++) buf[i] = VehicleSynth.WheelSqueal(car.Tyres, d, 1.4f, 1f, 1.4f, 4, rng, ref v);
            Report($"demand {d:F2}", buf.AsSpan(Sr / 2));
        }

        var report = new List<string>();
        Scenario("1-ordinary-stop", "i4_midsize", 0.85f, modulated: true, report, (body, t) =>
        {
            if (t < 2f) return (50f / 3.6f, 0f);
            return (MathF.Max(0f, body.Vx - 2.92f / 30f), 0f);
        }, startSpeed: 50f / 3.6f, seconds: 9f);

        Scenario("2-hard-stop", "i4_midsize", 0.85f, modulated: false, report, (body, t) =>
        {
            if (t < 2f) return (70f / 3.6f, 0f);
            // Standing on the pedal: asking for more than the tyres have.
            return (MathF.Max(0f, body.Vx - 11f / 30f), 0f);
        }, startSpeed: 70f / 3.6f, seconds: 7f);

        Scenario("3-fast-turn", "i4_midsize", 0.85f, modulated: false, report, (body, t) =>
        {
            // A 40 m left-hand bend at a speed that rises from 0.3 g to past the limit.
            float speed = MathF.Sqrt(MathF.Min(1.05f, 0.3f + 0.09f * t) * 9.81f * 40f);
            return (speed, -1f / 40f);
        }, startSpeed: MathF.Sqrt(0.3f * 9.81f * 40f), seconds: 10f);

        Scenario("4-wheelspin-pull-away", "v8_muscle", 0.9f, modulated: false, report, (body, t) =>
        {
            if (t < 1f) return (0f, 0f);
            // Floored from rest: asking the rear tyres for 8 m/s^2.
            return (body.Vx + 8f / 30f, 0f);
        }, startSpeed: 0f, seconds: 6f);

        // One gain for the whole set, a little under full scale at its loudest moment: a hard stop is
        // louder than an ordinary one in the files as it is on the street.
        float peak = 1e-6f;
        foreach (var (_, l, r) in Rendered) foreach (var o in new[] { l, r }) foreach (float x in o) peak = MathF.Max(peak, MathF.Abs(x));
        float g = 0.7f / peak;
        foreach (var (name, l, r) in Rendered)
        {
            for (int i = 0; i < l.Length; i++) { l[i] *= g; r[i] *= g; }
            File.WriteAllBytes(Path.Combine(outDir, name + (Binaural ? "-binaural" : "") + ".wav"), CrossingSpike.ToWav16Stereo(l, r, Sr));
        }
        report.Add($"one gain for every file: {20f * MathF.Log10(g):F1} dB");
        foreach (var (name, l, _) in Rendered)
        {
            int over = 0;
            foreach (float x in l) if (MathF.Abs(x) / g > SoftCeiling.Knee) over++;
            report.Add($"{name}: {100.0 * over / l.Length:F1} % of the left channel's samples past the voice's soft-ceiling knee");
            Console.WriteLine("  " + report[^1]);
        }
        File.WriteAllLines(Path.Combine(outDir, Binaural ? "measurements-binaural.txt" : "measurements.txt"), report);
        Console.WriteLine($"\n  Written to {outDir}");
        return 0;
    }

    /// <summary>
    /// One drive: <paramref name="plan"/> gives, at each tick, the speed wanted at its end and the
    /// curvature of the path (1/m, negative turning left). The body is stepped under the steering
    /// that holds that curvature at its speed (the kinematic angle plus the understeer the model has
    /// at that lateral acceleration), and its wheels go to two voices of the same car.
    /// </summary>
    private static void Scenario(string name, string preset, float grip, bool modulated, List<string> report,
                                 Func<WheelDynamics, float, (float Speed, float Curvature)> plan, float startSpeed, float seconds)
    {
        if (Binaural) { RoadsideBinaural(name, preset, grip, modulated, plan, startSpeed, seconds); return; }
        var profile = VehicleProfile.ByName(preset);
        var body = new WheelDynamics(profile, grip) { ForwardOnly = true, Modulated = modulated, Vx = startSpeed };
        var ears = new[] { new Vector3(-2.5f, 1.6f, 0f), new Vector3(2.5f, 1.6f, 0f) };
        var voices = ears.Select(_ => new EngineVoiceState(profile, Sr, 7) { TargetSpeed = startSpeed }).ToArray();
        foreach (var v in voices) v.PlaceAtSpeed(startSpeed);
        var outs = new[] { new float[(int)(seconds * Sr)], new float[(int)(seconds * Sr)] };
        var wire = new WheelState[body.Wheels.Length];
        var first = Enumerable.Repeat(float.NaN, body.Wheels.Length).ToArray();
        var most = new float[body.Wheels.Length];
        var block = new float[Tick];
        // Each wheel's squeal on its own, made from the same drive the voice gets, to say which wheel
        // is singing and how loud: the energy per tick, per wheel.
        var shadow = new VehicleSynth.WheelSquealVoice[body.Wheels.Length];
        var shadowRng = new Random(11);
        var energy = new List<float[]>();
        var probe = new WheelDynamics(profile);
        float refVs = 12f * MathF.Tan(probe.SteeredPeakSlip());
        float steer = 0f;
        int ticks = (int)(seconds * 30);
        for (int k = 0; k < ticks; k++)
        {
            float t = k / 30f;
            var (want, curvature) = plan(body, t);
            // The steering for the curvature: kinematic angle and the model's understeer gradient.
            float target = MathF.Atan(body.Wheelbase * curvature) + body.UndersteerGradient * body.Vx * body.Vx * curvature;
            steer += Math.Clamp(target - steer, -0.9f / 30f, 0.9f / 30f);
            body.Step(1f / 30f, steer, (want - body.Vx) * 30f);
            for (int i = 0; i < wire.Length; i++)
            {
                ref var w = ref body.Wheels[i];
                wire[i] = WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand);
                most[i] = MathF.Max(most[i], w.Demand);
                if (float.IsNaN(first[i]) && w.Demand >= TyreFriction.SquealOnset) first[i] = t;
            }
            var e4 = new float[wire.Length];
            for (int i = 0; i < wire.Length; i++)
            {
                float kappa = wire[i].SlipRatioValue, ta = MathF.Tan(wire[i].SlipAngleRad);
                float vs = MathF.Abs(body.Vx) * MathF.Sqrt(kappa * kappa + ta * ta);
                float load = wire[i].LoadNewtons / MathF.Max(1f, probe.Wheels[i].StaticLoad);
                double sum = 0;
                for (int j = 0; j < Tick; j++)
                {
                    float y = VehicleSynth.WheelSqueal(profile.Tyres, wire[i].DemandFraction, vs, load, refVs, wire.Length, shadowRng, ref shadow[i]);
                    sum += y * y;
                }
                e4[i] = (float)(sum / Tick);
            }
            energy.Add(e4);
            var placedAt = profile.ExhaustOffset;
            for (int e = 0; e < voices.Length; e++)
            {
                var v = voices[e];
                v.TargetSpeed = body.Vx;
                v.RoadSlip = MathF.Min(2f, body.MaxDemand);
                // AxleOnly: the voice as it was before the wheels squealed for themselves, for comparison.
                v.Wheels = AxleOnly ? null : (WheelState[])wire.Clone();
                v.SetListener(ears[e] - placedAt);
                v.Render(block);
                int at = k * Tick;
                for (int i = 0; i < Tick && at + i < outs[e].Length; i++) outs[e][at + i] = block[i];
            }
        }

        Rendered.Add((name, outs[0], outs[1]));

        report.Add($"{name} ({preset}, {grip:F2} g{(modulated ? ", a driver who feels the car" : "")})");
        Console.WriteLine($"\n  {name} ({preset})");
        int n = body.Wheels.Length;
        for (int i = 0; i < n; i++)
        {
            string label = n == 4 ? WheelNames[i] : $"wheel {i}";
            string line = $"    {label,-12} most {most[i]:F2} of its grip, " +
                          (float.IsNaN(first[i]) ? "never reached the squeal onset" : $"reached the squeal onset at {first[i]:F2} s");
            Console.WriteLine(line);
            report.Add(line);
        }
        // Which wheel is singing: each wheel's squeal level against the loudest any wheel reached, the
        // first time it came within 20 dB of that, and its share of all the squeal energy.
        float top = 1e-20f;
        foreach (var e4 in energy) foreach (float x in e4) top = MathF.Max(top, x);
        var total = new double[n];
        for (int i = 0; i < n; i++)
        {
            float firstLoud = float.NaN;
            for (int k = 0; k < energy.Count; k++)
            {
                total[i] += energy[k][i];
                if (float.IsNaN(firstLoud) && energy[k][i] >= top * 0.01f) firstLoud = k / 30f;
            }
            string label = n == 4 ? WheelNames[i] : $"wheel {i}";
            report.Add($"    {label,-12} squeal within 20 dB of the loudest from {(float.IsNaN(firstLoud) ? "never" : $"{firstLoud:F2} s")}");
            Console.WriteLine(report[^1]);
        }
        double all = total.Sum() + 1e-30;
        string shares = "    share of the squeal energy: " + string.Join(", ", Enumerable.Range(0, n).Select(i => $"{(n == 4 ? WheelNames[i] : i.ToString())} {100 * total[i] / all:F0} %"));
        Console.WriteLine(shares);
        report.Add(shares);
        var mono = new float[outs[0].Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = 0.5f * (outs[0][i] + outs[1][i]);
        string lr = $"    left/right level {MathF.Round(Db(outs[0]) - Db(outs[1]), 1):+0.0;-0.0;0.0} dB (left minus right)";
        Console.WriteLine(lr);
        report.Add(lr);
        report.Add("    " + Report("whole render", mono));
    }

    private static void RoadsideBinaural(string name, string preset, float grip, bool modulated,
                                         Func<WheelDynamics, float, (float Speed, float Curvature)> plan,
                                         float startSpeed, float seconds)
    {
        var profile = VehicleProfile.ByName(preset);
        var body = new WheelDynamics(profile, grip) { ForwardOnly = true, Modulated = modulated, Vx = startSpeed };
        int ticks = (int)(seconds * 30);
        // Pass 1: the drive, its pose (x east, z north; heading 0 = east, a left turn turns north) and wheels.
        var poses = new (Vector3 At, float Heading, float Speed, WheelState[] Wire)[ticks];
        Vector3 at = Vector3.Zero; float heading = 0f, steer = 0f, worst = -1f; int worstTick = ticks / 2;
        for (int k = 0; k < ticks; k++)
        {
            float t = k / 30f;
            var (want, curvature) = plan(body, t);
            float target = MathF.Atan(body.Wheelbase * curvature) + body.UndersteerGradient * body.Vx * body.Vx * curvature;
            steer += Math.Clamp(target - steer, -0.9f / 30f, 0.9f / 30f);
            body.Step(1f / 30f, steer, (want - body.Vx) * 30f);
            heading -= body.Vx * curvature / 30f;
            at += new Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading)) * body.Vx / 30f;
            var wire = new WheelState[body.Wheels.Length];
            for (int i = 0; i < wire.Length; i++)
            {
                ref var w = ref body.Wheels[i];
                wire[i] = WheelState.Encode(w.Load, w.AngularSpeed, w.SlipRatio, w.SlipAngle, w.Surface, w.Demand);
            }
            poses[k] = (at, heading, body.Vx, wire);
            float demand = body.MaxDemand;
            // Where the tyres work hardest; a drive that never works them, mid-way through it.
            if (demand > worst + 1e-3f) { worst = demand; worstTick = k; }
        }
        var (p0, h0, _, _) = poses[worstTick];
        var right0 = new Vector3(MathF.Sin(h0), 0f, -MathF.Cos(h0));
        var ear = p0 + right0 * 7.5f + new Vector3(0f, 1.6f, 0f);
        // Facing the road: the listener's forward is back across to the path.
        var faceFwd = -right0;
        var faceRight = new Vector3(-faceFwd.Z, 0f, faceFwd.X);

        // Pass 2: one voice, its two ends rendered apart as the game places them.
        var voice = new EngineVoiceState(profile, Sr, 7) { TargetSpeed = startSpeed, SplitVoices = true };
        voice.PlaceAtSpeed(startSpeed);
        var front = new EngineTapState(voice);
        int n = ticks * Tick;
        var rear = new float[n]; var nose = new float[n];
        var rearDirs = new Phonon.IPLVector3[n]; var noseDirs = new Phonon.IPLVector3[n];
        var rb = new float[Tick]; var fb = new float[Tick];
        var rearTap = new Vector3(0f, profile.ExhaustHeight, profile.ExhaustOffsetZ);
        var frontTap = new Vector3(0f, profile.FrontTapHeight, profile.FrontTapZ);
        for (int k = 0; k < ticks; k++)
        {
            var (p, h, v, wire) = poses[k];
            var fwd = new Vector3(MathF.Cos(h), 0f, MathF.Sin(h));
            var right = new Vector3(MathF.Sin(h), 0f, -MathF.Cos(h));
            var d = ear - p;
            var local = new Vector3(Vector3.Dot(d, right), d.Y, Vector3.Dot(d, fwd));
            voice.TargetSpeed = v;
            voice.RoadSlip = MathF.Min(2f, wire.Max(w => w.DemandFraction));
            voice.Wheels = AxleOnly ? null : wire;
            voice.SetListener(local - rearTap);
            voice.Render(rb);
            front.Render(fb);
            foreach (var (tap, sig, dirs, outSig) in new[] { (rearTap, rb, rearDirs, rear), (frontTap, fb, noseDirs, nose) })
            {
                var world = p + right * tap.X + new Vector3(0f, tap.Y, 0f) + fwd * tap.Z;
                var to = world - ear;
                float r = MathF.Max(1f, to.Length());
                var u = to / to.Length();
                // Steam Audio's frame: +x right, +y up, -z forward.
                var dir = new Phonon.IPLVector3 { x = Vector3.Dot(u, faceRight), y = u.Y, z = -Vector3.Dot(u, faceFwd) };
                for (int i = 0; i < Tick; i++) { outSig[k * Tick + i] = sig[i] / r; dirs[k * Tick + i] = dir; }
            }
        }
        var yr = PassBySpike.Render(rear, rearDirs, 256, Phonon.IPL_HRTFINTERPOLATION_BILINEAR);
        var yf = PassBySpike.Render(nose, noseDirs, 256, Phonon.IPL_HRTFINTERPOLATION_BILINEAR);
        var l = new float[n]; var rr = new float[n];
        for (int i = 0; i < n; i++) { l[i] = yr[2 * i] + yf[2 * i]; rr[i] = yr[2 * i + 1] + yf[2 * i + 1]; }
        Rendered.Add((name, l, rr));
        Console.WriteLine($"  {name}: heard from the pavement 7.5 m off the car's path at {p0.X:F0},{p0.Z:F0} (tyres hardest at {worstTick / 30f:F1} s, {worst:F2} of grip)");
    }

    private static float Db(float[] x)
    {
        double e = 0;
        foreach (float v in x) e += v * v;
        return 10f * MathF.Log10((float)(e / Math.Max(1, x.Length)) + 1e-20f);
    }

    /// <summary>Band balance, spectral flatness over 250 Hz to 8 kHz, and the strongest line's height
    /// above the median of the band an octave either side of it.</summary>
    private static string Report(string what, ReadOnlySpan<float> x)
    {
        var bands = Spectrum.BandsDb(x, Sr);
        const int N = 8192;
        var power = new double[N / 2];
        int frames = 0;
        var re = new double[N];
        var im = new double[N];
        for (int start = 0; start + N <= x.Length; start += N / 2)
        {
            for (int i = 0; i < N; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
                re[i] = x[start + i] * w; im[i] = 0;
            }
            Fft(re, im);
            for (int i = 0; i < N / 2; i++) power[i] += re[i] * re[i] + im[i] * im[i];
            frames++;
        }
        float binHz = (float)Sr / N;
        int lo = (int)(250f / binHz), hi = (int)(8000f / binHz);
        double logSum = 0, sum = 0;
        int peakBin = lo;
        for (int i = lo; i < hi; i++)
        {
            double p = power[i] + 1e-30;
            logSum += Math.Log(p); sum += p;
            if (power[i] > power[peakBin]) peakBin = i;
        }
        int m = hi - lo;
        double flatness = Math.Exp(logSum / m) / (sum / m);
        int a = Math.Max(1, peakBin / 2), b = Math.Min(power.Length - 1, peakBin * 2);
        var around = new List<double>();
        for (int i = a; i <= b; i++) around.Add(power[i]);
        around.Sort();
        double median = around[around.Count / 2] + 1e-30;
        string s = $"{what}: flatness {flatness:F2}, strongest line {peakBin * binHz:F0} Hz {10 * Math.Log10(power[peakBin] / median):F0} dB over its neighbourhood; bands "
                 + string.Join(" ", Enumerable.Range(3, 6).Select(i => $"{Spectrum.BandEdges[i]:F0}:{bands[i]:F0}"));
        if (frames > 0) Console.WriteLine("    " + s);
        return s;
    }

    private static void Fft(double[] re, double[] im)
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
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = i + k + len / 2;
                    double tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - tr; im[v] = im[u] - ti;
                    re[u] += tr; im[u] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }
}
