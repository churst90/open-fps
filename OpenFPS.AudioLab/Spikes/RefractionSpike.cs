using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --refraction [car=preset] [speed=km/h] [wind=m/s] [gust=0..1] [d=150,300,600] [src=m] [ground=Grass] [day=N] [temp=C]
///              [out=DIR] [nowav] [cost] [thunder]
///
/// Sound bent by the wind and the air's layers, measured as the game works it out (Bending.Along with a world's weather,
/// GroundAnswer with the raised ends): a source <c>src=</c> m up (0.5, a car's exhaust) heard by a standing ear across
/// flat <c>ground=</c> at each distance, upwind, downwind and across a <c>wind=</c> m/s west wind (4.5), on a clear
/// afternoon (15:00), a clear night (03:00) and in still neutral air. Per band, dB over free field: the ground as before
/// (the copy and what the ground takes, unbent), and now (the bounce with its ends raised, less the shadow, the stir's
/// mean), with the stir's swing as one standard deviation over ten minutes and the shadow's edge.
///
/// car=preset renders that car's live voice at each case (the copy, its excess and the bending as band gains, as the
/// mixer's THREE_EQ does) before and now and says each band's level over the dry voice; the day and night, upwind and
/// downwind, 300 m pairs are written to out=DIR (~/openfps-listen/refraction). cost times Bending.Along, the per-voice
/// price on the mixer thread. thunder compares the thunder's own shadow (Thunder.ShadowGain, unchanged) with what the
/// shared rule would give it, per band, over strikes 5 to 40 km off.
/// </summary>
public static class RefractionSpike
{
    const int Rate = MixerQuality.DefaultRate, Block = 512;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string? S(string k) => args.FirstOrDefault(a => a.StartsWith(k + "=", StringComparison.Ordinal))?[(k.Length + 1)..];
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        string? car = S("car");
        float speed = F(S("speed") ?? "50") / 3.6f;
        float wind = F(S("wind") ?? "4.5"), gust = F(S("gust") ?? "0.1");
        float srcHeight = F(S("src") ?? "0.5"), temp = F(S("temp") ?? "20");
        int day = int.Parse(S("day") ?? "172", CultureInfo.InvariantCulture);
        var distances = (S("d") ?? "50,100,150,300,600").Split(',').Select(F).ToArray();
        string groundName = S("ground") ?? "Grass";
        string outDir = S("out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "openfps-listen", "refraction");
        float c = AirLayers.SpeedOfSound(temp);
        var ground = AcousticRegistry.GetProperties(groundName);

        if (args.Contains("thunder")) return ThunderShare();

        float lat = RoadDrainageSpec.Default.LatitudeDegrees;
        var conditions = new (string Name, AirLayers Layers, float Wind)[]
        {
            ("afternoon", AirLayers.From(15f, day, lat, 0f, 0.4f, temp, 1013.25f, wind, gust), wind),
            ("night", AirLayers.From(3f, day, lat, 0f, 0.4f, temp - 8f, 1013.25f, wind, gust), wind),
            ("still neutral", AirLayers.Neutral(temp), 0f),
        };
        var ear0 = new Vector3(0f, 1.7f, 0f);
        Console.WriteLine($"  {groundName}, source {srcHeight:F2} m up, ear 1.7 m; {wind:F1} m/s from the west, gustiness {gust:F1}; day {day}, {temp:F0} °C (night {temp - 8f:F0})");
        Console.WriteLine("  bands low/middle/high, dB over free field; swing: one standard deviation over ten minutes");
        foreach (var (name, layers, w) in conditions)
        {
            var weather = new WorldWeather { Layers = layers };
            weather.Wind = WindWeather.Steady(w, 270f, WindAir.TurbulenceFor(gust));
            var land = layers.Land;
            Console.WriteLine($"  {name}: L {land.ObukhovMetres:F0} m, thermal log {land.ThermalLog:F3} m/s, linear {land.ThermalLinear:F4} /s, wind log {land.WindLog:F3}, linear {land.WindLinear:F4} /m");
            foreach (var (dir, toEar) in new[] { ("upwind", new Vector3(-1f, 0f, 0f)), ("downwind", new Vector3(1f, 0f, 0f)), ("across", new Vector3(0f, 0f, 1f)) })
            {
                if (w == 0f && dir != "upwind") continue;
                foreach (float d in distances)
                {
                    var src = new Vector3(0f, srcHeight, 0f);
                    var ear = src with { Y = 1.7f } + toEar * d;
                    var m = Measure(weather, ground, src, ear, c);
                    Console.WriteLine($"    {(w == 0f ? "still" : dir),-8} {d,5:F0} m: before {m.Before.Low,6:F1} {m.Before.Mid,6:F1} {m.Before.High,6:F1}   "
                                    + $"now {m.Now.Low,6:F1} {m.Now.Mid,6:F1} {m.Now.High,6:F1}   change {m.Now.Low - m.Before.Low,6:F1} {m.Now.Mid - m.Before.Mid,6:F1} {m.Now.High - m.Before.High,6:F1}   "
                                    + $"swing {m.Swing.Low,4:F1} {m.Swing.Mid,4:F1} {m.Swing.High,4:F1}   edge {(float.IsFinite(m.Edge) ? $"{m.Edge:F0} m" : "none")}, raised {m.RaiseS:F1}/{m.RaiseE:F1} m");
                    if (car != null) Render(car, speed, ground, src, ear, weather, c, $"{name.Replace(' ', '_')}_{(w == 0f ? "still" : dir)}_{d:F0}m",
                                            !args.Contains("nowav") && d == 300f && dir != "across", outDir);
                }
            }
        }

        if (args.Contains("cost"))
        {
            var weather = new WorldWeather { Layers = conditions[0].Layers };
            weather.Wind = WindWeather.Steady(wind, 270f, WindAir.TurbulenceFor(gust));
            var src = new Vector3(0f, srcHeight, 0f);
            for (int i = 0; i < 2000; i++) Bending.Along(weather, src, new Vector3(-300f, 1.7f, 0f), 0f, false, i * 0.1, c);
            const int N = 200000;
            var t = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) Bending.Along(weather, src, new Vector3(-300f + i * 1e-4f, 1.7f, 0f), 0f, false, i * 0.01, c);
            Console.WriteLine($"  cost: {t.Elapsed.TotalMilliseconds * 1000.0 / N:F2} µs a voice (Bending.Along, every {FmodAudioProvider.BendSeconds * 1000:F0} ms a voice)");
        }
        return 0;
    }

    internal readonly record struct Measured(Refraction.Bands Before, Refraction.Bands Now, Refraction.Bands Swing, float Edge, float RaiseS, float RaiseE);

    /// <summary>One case over ten minutes of its wind, a second apart: the ground before, and now with the bending.</summary>
    internal static Measured Measure(WorldWeather weather, in MaterialProperties ground, Vector3 src, Vector3 ear, float c)
    {
        Span<double> real = stackalloc double[3], copy = stackalloc double[3];
        var before = GroundDb(ground, src, ear, c, 0f, 0f, real, copy);
        double[] sum = new double[3], sumSq = new double[3];
        double rs = 0, re = 0, edge = 0;
        const int Samples = 600;
        for (int i = 0; i < Samples; i++)
        {
            var bent = Bending.Along(weather, src, ear, 0f, false, 1000.0 + i, c);
            var g = GroundDb(ground, src, ear, c, bent.SourceRaise, bent.EarRaise, real, copy);
            var t = bent.TakenDb;
            double[] now = { g.Low - t.Low, g.Mid - t.Mid, g.High - t.High };
            for (int b = 0; b < 3; b++) { sum[b] += now[b]; sumSq[b] += now[b] * now[b]; }
            rs += bent.SourceRaise; re += bent.EarRaise; edge += float.IsFinite(bent.EdgeMetres) ? bent.EdgeMetres : 1e9;
        }
        float Mean(int b) => (float)(sum[b] / Samples);
        float Sd(int b) => (float)Math.Sqrt(Math.Max(0.0, sumSq[b] / Samples - (sum[b] / Samples) * (sum[b] / Samples)));
        float e = (float)(edge / Samples);
        return new Measured(before, new Refraction.Bands(Mean(0), Mean(1), Mean(2)), new Refraction.Bands(Sd(0), Sd(1), Sd(2)),
                            e > 1e8f ? float.PositiveInfinity : e, (float)(rs / Samples), (float)(re / Samples));
    }

    /// <summary>The ground over free field per band, dB, as the voice plays it: the copy's power and what the ground takes.</summary>
    static Refraction.Bands GroundDb(in MaterialProperties ground, Vector3 src, Vector3 ear, float c, float rs, float re, Span<double> real, Span<double> copy)
    {
        if (GroundAnswer.Reflect(0f, ground, src, ear, 0f, c, rs, re) is not { } b) return Refraction.Bands.None;
        if (!GroundAnswer.Powers(0f, ground, src, ear, c, real, copy, rs, re))
        {
            // No flow resistivity: the copy alone, reckoned as a comb would be beside the point; say nothing taken.
            return Refraction.Bands.None;
        }
        return new Refraction.Bands(P10(copy[0]) + Db(b.ExcessLow), P10(copy[1]) + Db(b.ExcessMid), P10(copy[2]) + Db(b.ExcessHigh));
    }

    /// <summary>
    /// The car's live voice before (the copy and its excess, unbent) and now (the raised bounce and its excess, less the
    /// shadow and the stir's mean), each band's level over the dry voice; written as WAVs when asked.
    /// </summary>
    static void Render(string preset, float speed, in MaterialProperties ground, Vector3 src, Vector3 ear, WorldWeather weather, float c,
                       string tag, bool wav, string outDir)
    {
        float d = Vector3.Distance(src, ear);
        var bent = Bending.Along(weather, src, ear, 0f, false, 1000.0, c);
        var before = GroundAnswer.Reflect(0f, ground, src, ear, 0f, c);
        var now = GroundAnswer.Reflect(0f, ground, src, ear, 0f, c, bent.SourceRaise, bent.EarRaise);
        if (before is not { } b0 || now is not { } b1) return;
        float[] Voice(GroundAnswer.Bounce? bounce)
        {
            var v = MachineRegistry.VehicleFor(preset);
            var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed, SplitVoices = true, CompensateLevel = true };
            voice.PlaceAtSpeed(speed);
            voice.Revive();
            var tap = new EngineTapState(voice);
            voice.SetListener(new Vector3(0f, 1.7f, -d) - v.ExhaustSlot);
            voice.Ground.Set(bounce?.DelaySeconds ?? 0f, bounce?.LowGain ?? 0f, bounce?.HighGain ?? 0f);
            int warm = Rate / Block, blocks = 4 * Rate / Block;
            var fb = new float[Block]; var rb = new float[Block];
            var x = new float[blocks * Block];
            for (int k = 0; k < warm + blocks; k++)
            {
                voice.Produce(); tap.Render(fb); voice.Consume(rb);
                if (k >= warm) Array.Copy(rb, 0, x, (k - warm) * Block, Block);
            }
            return x;
        }
        var dry = GroundAnswerSpike.Bands(Voice(null));
        var shadow = bent.ShadowDb;
        var x0 = Banded(Voice(b0), Db(b0.ExcessLow), Db(b0.ExcessMid), Db(b0.ExcessHigh));
        var x1 = Banded(Voice(b1), Db(b1.ExcessLow) - shadow.Low, Db(b1.ExcessMid) - shadow.Mid, Db(b1.ExcessHigh) - shadow.High);
        var p0 = GroundAnswerSpike.Bands(x0); var p1 = GroundAnswerSpike.Bands(x1);
        Console.WriteLine($"             {preset} at {speed * 3.6f:F0} km/h rendered: before {P10(p0[0] / dry[0]),6:F1} {P10(p0[1] / dry[1]),6:F1} {P10(p0[2] / dry[2]),6:F1}   "
                        + $"now {P10(p1[0] / dry[0]),6:F1} {P10(p1[1] / dry[1]),6:F1} {P10(p1[2] / dry[2]),6:F1}");
        if (!wav) return;
        Directory.CreateDirectory(outDir);
        float peak = 1e-9f;
        foreach (float s in x0) peak = MathF.Max(peak, MathF.Abs(s));
        float gain = 0.5f / peak;
        GroundAnswerSpike.Write(Path.Combine(outDir, $"{preset}_{tag}_1_before.wav"), x0, gain);
        GroundAnswerSpike.Write(Path.Combine(outDir, $"{preset}_{tag}_2_now.wav"), x1, gain);
    }

    /// <summary>The signal with each of the voice's bands moved by so many dB (whole-signal FFT).</summary>
    static float[] Banded(float[] x, float lowDb, float midDb, float highDb)
    {
        int n = 1; while (n < x.Length) n <<= 1;
        var a = new Complex[n];
        for (int i = 0; i < x.Length; i++) a[i] = x[i];
        Spectrum.Fft(a);
        float gl = MathF.Pow(10f, lowDb / 20f), gm = MathF.Pow(10f, midDb / 20f), gh = MathF.Pow(10f, highDb / 20f);
        for (int k = 0; k < n; k++)
        {
            double hz = Math.Min(k, n - k) * (double)Rate / n;
            a[k] *= hz < AcousticBands.LowCrossoverHz ? gl : hz < AcousticBands.HighCrossoverHz ? gm : gh;
        }
        for (int k = 0; k < n; k++) a[k] = Complex.Conjugate(a[k]);
        Spectrum.Fft(a);
        var y = new float[x.Length];
        for (int i = 0; i < y.Length; i++) y[i] = (float)(a[i].Real / n);
        return y;
    }

    /// <summary>
    /// The thunder's shadow as it is (Thunder.ShadowGain: the standard lapse and the ground wind over a kilometre, one
    /// figure for every band) against what the shared rule gives a piece of channel at the same place (Refraction: the
    /// standard lapse as the profile's linear term, the wind's log law, the creeping wave's depth by band), dB, in the
    /// server's Storm air.
    /// </summary>
    static int ThunderShare()
    {
        var storm = new Thunder.Air(15f, 0.95f, 1013.25f, new Vector3(15f, 0f, -10f), 0.8f);
        float c = AudioPhysics.SpeedOfSoundAt(storm.TemperatureC);
        float windSpeed = new Vector2(storm.Wind.X, storm.Wind.Z).Length();
        var down = Vector2.Normalize(new Vector2(storm.Wind.X, storm.Wind.Z));
        var ear = new Vector3(0f, 1.7f, 0f);
        float mu2 = Thunder.TurbulenceVariance(storm, c);
        float worst = 0f;
        Console.WriteLine($"  thunder: storm wind {windSpeed:F1} m/s, μ² {mu2:E2}; old (Thunder.ShadowGain) and the shared rule's low/middle/high, dB");
        foreach (var (name, sign, still) in new[] { ("still", 0f, true), ("upwind", 1f, false), ("downwind", -1f, false), ("across", 0f, false) })
        {
            var air = still ? storm with { Wind = Vector3.Zero } : storm;
            foreach (float h in new[] { 2000f, 4000f, 6000f })
                foreach (float x in new[] { 5000f, 10000f, 15000f, 20000f, 25000f, 30000f, 40000f })
                {
                    // Upwind: the piece lies downwind of the ear, the sound going against the wind.
                    var dir = sign != 0f ? down * sign : new Vector2(-down.Y, down.X);
                    var at = new Vector3(dir.X * x, h, dir.Y * x);
                    float old = 20f * MathF.Log10(MathF.Max(1e-6f, Thunder.ShadowGain(at, ear, 0f, air, c)));
                    var flat = new Vector2(ear.X - at.X, ear.Z - at.Z);
                    float toward = (air.Wind.X * flat.X + air.Wind.Z * flat.Y) / flat.Length();
                    var profile = new Refraction.Profile(toward / MathF.Log(10f / WindField.RoughnessMetres),
                                                         -c / (2f * (air.TemperatureC + 273.15f)) * Thunder.LapseRate);
                    float k = Refraction.Curvature(profile, x, h, 1.7f, c);
                    float muAir = Thunder.TurbulenceVariance(air, c);
                    var now = Refraction.Shadow(x, h, 1.7f, k, muAir, c);
                    worst = MathF.Max(worst, MathF.Max(MathF.Abs(-now.Low - old), MathF.Max(MathF.Abs(-now.Mid - old), MathF.Abs(-now.High - old))));
                    Console.WriteLine($"    {name,-8} {h / 1000f,3:F0} km up, {x / 1000f,4:F0} km off: old {old,6:F1}   shared rule {-now.Low,6:F1} {-now.Mid,6:F1} {-now.High,6:F1}"
                                    + $"   edge old {EdgeOld(h, air, toward, c) / 1000f,5:F1} km, shared {Refraction.ShadowEdge(k, h, 1.7f) / 1000f,5:F1} km");
                }
        }
        Console.WriteLine($"  the most any band would move: {worst:F1} dB");
        return 0;
    }

    static float EdgeOld(float h, Thunder.Air air, float toward, float c)
    {
        float a = Thunder.LapseRate / (2f * (air.TemperatureC + 273.15f)) - toward / Thunder.WindShearDepthMetres / c;
        return a <= 0f ? float.PositiveInfinity : MathF.Sqrt(2f * h / a) + MathF.Sqrt(2f * 1.7f / a);
    }

    static float Db(float gain) => 20f * MathF.Log10(MathF.Max(1e-6f, gain));
    static float P10(double power) => (float)(10.0 * Math.Log10(Math.Max(1e-12, power)));
}
