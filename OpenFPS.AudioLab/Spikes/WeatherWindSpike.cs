using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --weather-wind [out=DIR] [sec=12]: the weather's wind, measured before anybody listens.
///
///   1. The wind at the ears, at 2, 5, 10 and 15 m/s past the head, facing into it, side-on each way
///      and with the back to it: each ear's level as the game places it (dBFS) and as the model says
///      it is at the ear (dB SPL), against the published law; the third-octave shape against the
///      published slope; how alike the two ears are.
///   2. A slow turn through 360 degrees at 5 and 10 m/s: each ear's level every 30 degrees.
///   3. A tree (foliage:park_tree) with the wind field driven the way the game drives it: a server
///      weather system (WorldEnvironmentSystem) set by the /weather path, broadcast once a second,
///      each broadcast followed by a client WindWeather ramp, and the tree reading the field. Calm,
///      breezy, stormy, and a storm arriving.
///
/// The ear WAVs are stereo at the level the game would place them (default compression, before the
/// master trim and limiter). The tree WAVs are mono at a metre, −20 dBFS rms = 94 dB SPL, the same
/// convention as --nature render.
/// </summary>
public static class WeatherWindSpike
{
    private const int Rate = 44100;
    private const int Block = 1024;

    private static readonly float[] Thirds =
        { 25, 31.5f, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 };

    public static int Run(string[] args)
    {
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4);
        float sec = Arg(args, "sec=", 12f);
        if (dir != null) Directory.CreateDirectory(dir);
        Loudness.DynamicRangeCompression = Loudness.DefaultCompression;
        Console.WriteLine($"Loudness law: compression {Loudness.DynamicRangeCompression:F2}, ceiling {Loudness.RenderCeilingDb:F1} dB SPL at full scale.");
        Console.WriteLine($"Ear law: {EarWind.ReferenceDb} dB at {EarWind.ReferenceSpeed} m/s, {EarWind.DbPerDecade} dB/decade; windward -{EarWind.WindwardDb} dB, lee +{EarWind.LeeDb}, from behind +{EarWind.FromBehindDb}; knee {EarWind.KneeHzPerMetrePerSecond} Hz per m/s.");

        if (args.Contains("live")) return Live(Arg(args, "sec=", 8f));
        if (!args.Contains("trees")) Ears(dir, sec);
        if (!args.Contains("trees")) Turn(dir);
        if (!args.Contains("trees")) EarsInWeather(dir, Math.Max(sec, 20f));
        if (!args.Contains("ears")) Trees(dir, args.Contains("short") ? 30f : 60f);
        return 0;
    }

    // ── 1. The ears ─────────────────────────────────────────────────────────────────────────────

    private static readonly (string Name, float From)[] Angles =
    {
        ("facing into it", 0f), ("wind from the right", 90f), ("back to it", 180f), ("wind from the left", 270f),
    };

    private static void Ears(string? dir, float sec)
    {
        Console.WriteLine();
        Console.WriteLine("== The wind at the ears, steady wind past the head (no gusts), per ear");
        Console.WriteLine("   speed  heading               law L/R dB SPL   placed L/R dBFS   measured L/R dBFS   implied L/R dB SPL   L-R   coherence");
        foreach (float speed in new[] { 2f, 5f, 10f, 15f })
        {
            foreach (var (name, from) in Angles)
            {
                // Facing north; the air comes from `from` degrees clockwise of the face, so it moves
                // the opposite way.
                float a = (from + 180f) * MathF.PI / 180f;
                var moving = new Vector2(MathF.Sin(a), MathF.Cos(a)) * speed;
                var ears = EarWind.Ears(speed, moving, 0f);
                var (l, r) = Render(ears, sec);
                double ml = Db(Rms(l)), mr = Db(Rms(r));
                double pl = EarWind.RenderedDb(ears.DeclaredDb, ears.LeftDb);
                double pr = EarWind.RenderedDb(ears.DeclaredDb, ears.RightDb);
                double placed = EarWind.PlacedDb(ears.DeclaredDb);
                // What the model claims at the ear, read back off the measured output through the
                // law it was placed by (only exact under the soft ceiling's knee).
                double il = ears.DeclaredDb + (ml - placed), ir = ears.DeclaredDb + (mr - placed);
                double coh = Correlation(l, r);
                Console.WriteLine($"   {speed,4:F0}   {name,-20}  {ears.LeftDb,6:F1} {ears.RightDb,6:F1}    {pl,6:F1} {pr,6:F1}      {ml,6:F1} {mr,6:F1}        {il,6:F1} {ir,6:F1}     {ml - mr,5:F1}   {coh,5:F2}");
                if (dir != null && (from == 0f || from == 90f))
                    WriteStereo(Path.Combine(dir, $"ears_{speed:F0}ms_{(from == 0f ? "facing" : "from_right")}.wav"), l, r);
            }
        }

        Console.WriteLine();
        Console.WriteLine("== Third-octave shape, right ear, facing into it (dB re the loudest band). Published: flat below the");
        Console.WriteLine("   knee, -26 dB/octave above it (Dillon et al. 1999 via Korhonen 2021), knee ~300 Hz, rising with speed.");
        Console.WriteLine("   speed knee  " + string.Join(" ", Thirds.Select(c => (c >= 1000 ? $"{c / 1000:0.#}k" : $"{c:0.#}").PadLeft(5))) + "   slope 2-4x knee");
        foreach (float speed in new[] { 2f, 5f, 10f, 15f })
        {
            var ears = EarWind.Ears(speed, new Vector2(0f, -speed), 0f);
            var (_, r) = Render(ears, sec);
            var bands = ThirdOctaves(r);
            double top = bands.Max();
            string row = string.Join(" ", bands.Select(b => $"{10 * Math.Log10(Math.Max(1e-20, b / top)),5:F0}"));
            // The slope between two and four times the knee, dB per octave, from the band levels.
            double s = Slope(bands, ears.KneeHz * 2f, ears.KneeHz * 4f);
            Console.WriteLine($"   {speed,4:F0} {ears.KneeHz,5:F0}  {row}   {s,6:F1} dB/oct");
        }

        Console.WriteLine();
        Console.WriteLine("== Buffeting: the 20 ms envelope of each ear (sd over mean), its rate, and how much the ears share it");
        foreach (float speed in new[] { 2f, 5f, 10f, 15f })
        {
            var ears = EarWind.Ears(speed, new Vector2(0f, -speed), 0f);
            var (l, r) = Render(ears, sec);
            var el = Envelope(l, Rate / 50); var er = Envelope(r, Rate / 50);
            double sdL = Sd(el) / el.Average();
            double envCorr = Correlation(el.Select(v => (float)v).ToArray(), er.Select(v => (float)v).ToArray());
            Console.WriteLine($"   {speed,4:F0} m/s: envelope sd/mean L {sdL:F2}, ears' envelopes correlate {envCorr:F2}; buffet rate ~{speed / EarWindSynth.BuffetEddyMetres:F0} Hz");
        }
    }

    /// <summary>One steady state, rendered through the synth as the game's DSP does, block by block.</summary>
    private static (float[] L, float[] R) Render(EarWindAtEars ears, float sec, int seed = 11)
    {
        var s = new EarWindSynth(Rate, seed);
        int n = (int)(sec * Rate);
        var l = new float[n]; var r = new float[n];
        // A second of settling (the gain and knee glide in) that is not kept.
        var warmL = new float[Block]; var warmR = new float[Block];
        for (int i = 0; i < Rate; i += Block) { s.Control(ears, Block / (float)Rate); s.Render(warmL, warmR); }
        for (int i = 0; i < n; i += Block)
        {
            int m = Math.Min(Block, n - i);
            s.Control(ears, m / (float)Rate);
            s.Render(l.AsSpan(i, m), r.AsSpan(i, m));
        }
        return (l, r);
    }

    // ── 2. Turning through 360 degrees ──────────────────────────────────────────────────────────

    private static void Turn(string? dir)
    {
        foreach (float speed in new[] { 5f, 10f })
        {
            const float seconds = 72f;
            Console.WriteLine();
            Console.WriteLine($"== Turning slowly clockwise through 360 degrees in {seconds:F0} s, {speed:F0} m/s from the north (steady), per ear, dBFS as placed");
            var s = new EarWindSynth(Rate, 5);
            int n = (int)(seconds * Rate);
            var l = new float[n]; var r = new float[n];
            var moving = new Vector2(0f, -speed); // from the north
            for (int i = 0; i < n; i += Block)
            {
                int m = Math.Min(Block, n - i);
                float facing = 360f * i / n;
                var ears = EarWind.Ears(speed, moving, facing);
                s.Control(ears, m / (float)Rate);
                s.Render(l.AsSpan(i, m), r.AsSpan(i, m));
            }
            Console.WriteLine("   facing   wind from   L dBFS  R dBFS   law L  law R   (2 s windows)");
            for (int deg = 0; deg < 360; deg += 30)
            {
                int at = (int)(deg / 360f * n), w = 2 * Rate;
                int a = Math.Max(0, at - w / 2), b = Math.Min(n, at + w / 2);
                double dl = Db(Rms(l.AsSpan(a, b - a))), dr = Db(Rms(r.AsSpan(a, b - a)));
                float from = (360f - deg) % 360f;
                var law = EarWind.Ears(speed, moving, deg);
                Console.WriteLine($"   {deg,5}    {from,5:F0}       {dl,6:F1}  {dr,6:F1}   {EarWind.RenderedDb(law.DeclaredDb, law.LeftDb),5:F1}  {EarWind.RenderedDb(law.DeclaredDb, law.RightDb),5:F1}");
            }
            if (dir != null) WriteStereo(Path.Combine(dir, $"turn_360_{speed:F0}ms.wav"), l, r);
        }
    }

    // ── 3. The tree, through the weather path ───────────────────────────────────────────────────

    private sealed record Setting(string Name, Action<WorldEnvironmentSystem> Set, Action<WorldEnvironmentSystem>? Then = null, float ThenAt = 0f);

    private static void Trees(string? dir, float sec)
    {
        var settings = new[]
        {
            new Setting("calm", e => e.PinWind(new Vector3(1.2f, 0f, 0.9f), 0.1f)),          // 1.5 m/s, steady
            new Setting("breezy", e => e.PinWind(new Vector3(-3.5f, 0f, -3.5f), 0.4f)),      // 5 m/s from the north east, gusty
            new Setting("stormy", e => e.PinScenario(WeatherType.Storm)),                    // 18 m/s, very gusty
            new Setting("storm_arriving", e => e.PinWind(new Vector3(-3.5f, 0f, -3.5f), 0.4f),
                        e => e.PinScenario(WeatherType.Storm), 10f),
        };
        var spec = FoliageSpec.ByName("park_tree");
        // A tree in Elm Park, a few hundred metres from the origin, where a swinging pattern would race.
        const float x = -375f, z = 195f;
        foreach (var set in settings)
        {
            var env = new WorldEnvironmentSystem(new Random(1)) { FrontProbabilityPerTick = 0 };
            set.Set(env);
            // A minute of server time so the weather has arrived; the clock and the travel carry on.
            const float tick = 1f / 60f;
            double clock = 1000.0;
            for (int i = 0; i < 60 * 60; i++) { env.Update(tick); clock += tick; }
            var weather = WindWeather.Default;
            void Broadcast(double at)
            {
                var st = env.GetCurrentState();
                var (te, tn) = env.WindTravel;
                var air = WindAir.FromBroadcast(st.WindVelocity, st.WindGustiness, at, te, tn);
                weather = weather.Following(air, at);
            }
            Broadcast(clock);

            var synth = new FoliageSynth(spec, Rate, 7);
            int n = (int)(sec * Rate);
            var pa = new float[n];
            double nextBroadcast = clock + 1.0, serverClock = clock;
            var winds = new List<float>();
            var steps = new List<float>();
            float lastWind = float.NaN;
            bool switched = false;
            for (int i = 0; i < n; i += 256)
            {
                double t = clock + i / (double)Rate;
                // The server ticks at 60 Hz up to now and broadcasts once a second.
                while (serverClock + tick <= t)
                {
                    if (set.Then != null && !switched && serverClock - clock >= set.ThenAt) { set.Then(env); switched = true; }
                    env.Update(tick); serverClock += tick;
                    if (serverClock >= nextBroadcast) { Broadcast(serverClock); nextBroadcast += 1.0; }
                }
                float wind = WindField.SpeedAt(weather, x, spec.CrownHeightMetres, z, t);
                if (!float.IsNaN(lastWind)) steps.Add(MathF.Abs(wind - lastWind));
                lastWind = wind;
                winds.Add(wind);
                synth.Wind = wind;
                synth.Control(256 / (float)Rate);
                for (int k = i; k < Math.Min(n, i + 256); k++) pa[k] = synth.Next();
            }
            var st = env.GetCurrentState();
            float mean = winds.Average(), sd = (float)Math.Sqrt(winds.Select(w => (w - mean) * (w - mean)).Average());
            steps.Sort();
            Console.WriteLine();
            Console.WriteLine($"== foliage:park_tree, {set.Name}: server wind {new Vector2(st.WindVelocity.X, st.WindVelocity.Z).Length():F1} m/s at 10 m, gustiness {st.WindGustiness:F2} " +
                              $"(turbulence {WindAir.TurbulenceFor(st.WindGustiness):F2}); at the crown ({spec.CrownHeightMetres} m) mean {mean:F1} m/s, sd {sd:F2}, " +
                              $"min {winds.Min():F1}, max {winds.Max():F1}; largest change between 5.8 ms reads {steps[^1]:F3} m/s (99th pct {steps[(int)(0.99 * (steps.Count - 1))]:F3})");
            NatureSpike.Report("park_tree " + set.Name, pa, Rate, calibrated: true);
            if (dir != null) WriteMono(Path.Combine(dir, $"tree_{set.Name}.wav"), pa.Select(p => p * 0.1f).ToArray());
        }
    }

    /// <summary>A server weather system ticking at 60 Hz and broadcasting once a second, and a client
    /// following the broadcasts: the game's path from /weather to WindField, on the lab's clock.</summary>
    private sealed class WeatherSim
    {
        public readonly WorldEnvironmentSystem Env = new(new Random(1)) { FrontProbabilityPerTick = 0 };
        public WindWeather Weather = WindWeather.Default;
        private const float Tick = 1f / 60f;
        private double _server, _nextBroadcast;

        public WeatherSim(Action<WorldEnvironmentSystem> set, double start)
        {
            set(Env);
            _server = start - 60.0;
            // A minute for the weather to arrive, then the first broadcast.
            while (_server < start) { Env.Update(Tick); _server += Tick; }
            Broadcast();
            _nextBroadcast = _server + 1.0;
        }

        public void Advance(double t)
        {
            while (_server + Tick <= t)
            {
                Env.Update(Tick); _server += Tick;
                if (_server >= _nextBroadcast) { Broadcast(); _nextBroadcast += 1.0; }
            }
        }

        private void Broadcast()
        {
            var st = Env.GetCurrentState();
            var (te, tn) = Env.WindTravel;
            Weather = Weather.Following(WindAir.FromBroadcast(st.WindVelocity, st.WindGustiness, _server, te, tn), _server);
        }
    }

    /// <summary>The ears in the game's weather: standing in the open facing north, and walking at the
    /// game's walk (4.5 m/s, a jog) north and south, through EarWind.Hear as the DSP calls it.</summary>
    private static void EarsInWeather(string? dir, float sec)
    {
        var settings = new (string Name, Action<WorldEnvironmentSystem> Set)[]
        {
            ("still", e => e.PinWind(Vector3.Zero, 0f)),
            ("calm", e => e.PinWind(new Vector3(1.2f, 0f, 0.9f), 0.1f)),
            ("breezy", e => e.PinWind(new Vector3(-3.5f, 0f, -3.5f), 0.4f)),   // from the north east
            ("stormy", e => e.PinScenario(WeatherType.Storm)),                  // from the north west
        };
        var walks = new (string Name, Vector2 Velocity, float Facing, float Exposure)[]
        {
            ("standing facing north", Vector2.Zero, 0f, 1f),
            ("walking north", new Vector2(0f, PhysicsConstants.WalkSpeed), 0f, 1f),
            ("walking south", new Vector2(0f, -PhysicsConstants.WalkSpeed), 180f, 1f),
            ("sprinting north", new Vector2(0f, PhysicsConstants.SprintSpeed), 0f, 1f),
            ("walking north indoors", new Vector2(0f, PhysicsConstants.WalkSpeed), 0f, 0f),
        };
        Console.WriteLine();
        Console.WriteLine("== The ears in the weather, out in the open at 1.7 m (the weather's wind at 10 m is the server's)");
        Console.WriteLine("   weather  what                     felt mean  from(face)  L dBFS  R dBFS  L SPL  R SPL   L range (1 s, dBFS)");
        foreach (var (wname, set) in settings)
            foreach (var (name, vel, facing, exposure) in walks)
            {
                double start = 5000.0;
                var sim = new WeatherSim(set, start);
                var synth = new EarWindSynth(Rate, 3);
                int n = (int)(sec * Rate);
                var l = new float[n]; var r = new float[n];
                var head = new Vector3(-375f, 1.7f, 195f);
                EarWindAtEars last = default;
                double meanSpeed = 0, meanL = 0, meanR = 0; int blocks = 0;
                for (int i = 0; i < n; i += Block)
                {
                    int m = Math.Min(Block, n - i);
                    double t = start + i / (double)Rate;
                    sim.Advance(t);
                    var at = head + new Vector3(vel.X, 0f, vel.Y) * (float)(i / (double)Rate);
                    var listener = new EarWindListener(at, 1.7f, vel, facing, exposure);
                    last = EarWind.Hear(sim.Weather, listener, t);
                    meanSpeed += last.MeanSpeed; meanL += last.LeftDb; meanR += last.RightDb; blocks++;
                    synth.Control(last, m / (float)Rate);
                    synth.Render(l.AsSpan(i, m), r.AsSpan(i, m));
                }
                var sec1 = new List<double>();
                for (int a = Rate; a + Rate <= n; a += Rate) sec1.Add(Db(Rms(l.AsSpan(a, Rate))));
                int skip = Rate; // the first second is the gain gliding in
                double dl = Db(Rms(l.AsSpan(skip))), dr = Db(Rms(r.AsSpan(skip)));
                if (sec1.Count == 0 || double.IsInfinity(sec1.Min())) sec1 = new List<double> { -240, -240 };
                Console.WriteLine($"   {wname,-7}  {name,-22}  {meanSpeed / blocks,6:F1} m/s   {last.FromDegrees,5:F0}     {dl,6:F1}  {dr,6:F1}  {meanL / blocks,5:F0}  {meanR / blocks,5:F0}   {sec1.Min(),6:F1} .. {sec1.Max(),6:F1}");
                if (dir != null && exposure > 0f && wname != "calm" && (name.StartsWith("standing") != (wname == "still")))
                    WriteStereo(Path.Combine(dir, $"weather_{wname}_{name.Replace(' ', '_')}.wav"), l, r);
            }
    }

    /// <summary>
    /// The ear wind through the REAL provider: FmodAudioProvider starts its generator, the listener is
    /// handed to it as ClientAudioSystem does, the master is captured, and each ear's level read back
    /// against the law plus the master's own trim and makeup (the limiter may take a little off the
    /// buffets). A steady 10 m/s from the east at ten metres, facing north: the right ear takes it.
    /// </summary>
    private static int Live(float sec)
    {
        string cap = Path.Combine(Path.GetTempPath(), "openfps-earwind-live.wav");
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", cap);
        var weather = WindWeather.Steady(WindAir.FromCompass(10f, 90f, 0f));
        WindField.Weather = weather;
        var provider = new OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        provider.UpdateListener(new Vector3(0f, 1.7f, 0f), Quaternion.Identity, Vector3.Zero, -1);
        var listener = new EarWindListener(new Vector3(0f, 1.7f, 0f), 1.7f, Vector2.Zero, 0f, 1f);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < sec)
        {
            provider.SetEarWind(listener);
            provider.Update();
            System.Threading.Thread.Sleep(16);
        }
        var (lf, rf, ears) = provider.EarWindLevels;
        provider.Dispose();
        var (l, r, rate) = ReadStereo(cap);
        int from = Math.Min(l.Length, 2 * rate);
        double dl = Db(Rms(l.AsSpan(from))), dr = Db(Rms(r.AsSpan(from)));
        float makeup = 2f + OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.MasterMakeupDb;
        Console.WriteLine($"== Live: 10 m/s from the east at 10 m ({ears.MeanSpeed:F1} m/s at the head), facing north, through FmodAudioProvider");
        Console.WriteLine($"   the voice placed L {lf:F1} R {rf:F1} dBFS; the master adds {makeup:F0} dB (trim and limiter makeup)");
        Console.WriteLine($"   captured L {dl:F1} R {dr:F1} dBFS over {(l.Length - from) / (double)rate:F1} s; expected L {lf + makeup:F1} R {rf + makeup:F1}; L-R {dl - dr:F1} dB (law {lf - rf:F1})");
        Console.WriteLine($"   interaural correlation {Correlation(l.AsSpan(from).ToArray(), r.AsSpan(from).ToArray()):F3}");
        File.Delete(cap);
        return 0;
    }

    private static (float[] L, float[] R, int Rate) ReadStereo(string path)
    {
        using var rd = new BinaryReader(File.OpenRead(path));
        rd.ReadBytes(12);
        int channels = 2, rate = 44100;
        while (rd.BaseStream.Position < rd.BaseStream.Length)
        {
            string id = new string(rd.ReadChars(4));
            int size = rd.ReadInt32();
            if (id == "fmt ")
            {
                rd.ReadInt16(); channels = rd.ReadInt16(); rate = rd.ReadInt32(); rd.ReadInt32(); rd.ReadInt16(); rd.ReadInt16();
                if (size > 16) rd.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                int frames = size / (2 * channels);
                var l = new float[frames]; var r = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    l[i] = rd.ReadInt16() / 32768f;
                    r[i] = channels > 1 ? rd.ReadInt16() / 32768f : l[i];
                    for (int c = 2; c < channels; c++) rd.ReadInt16();
                }
                return (l, r, rate);
            }
            else rd.ReadBytes(size);
        }
        throw new InvalidDataException(path);
    }

    // ── Measures and files ──────────────────────────────────────────────────────────────────────

    private static double Rms(ReadOnlySpan<float> x)
    {
        double e = 0;
        foreach (float v in x) e += v * (double)v;
        return Math.Sqrt(e / Math.Max(1, x.Length));
    }

    private static double Db(double rms) => 20 * Math.Log10(Math.Max(1e-12, rms));

    private static double Correlation(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < n; i++) { double x = a[i] - ma, y = b[i] - mb; ab += x * y; aa += x * x; bb += y * y; }
        return ab / Math.Sqrt(Math.Max(1e-30, aa * bb));
    }

    private static List<double> Envelope(float[] x, int hop)
    {
        var env = new List<double>();
        for (int s = 0; s + hop <= x.Length; s += hop)
        {
            double e = 0;
            for (int i = s; i < s + hop; i++) e += x[i] * (double)x[i];
            env.Add(Math.Sqrt(e / hop));
        }
        return env;
    }

    private static double Sd(List<double> v)
    {
        double m = v.Average();
        return Math.Sqrt(v.Select(x => (x - m) * (x - m)).Average());
    }

    /// <summary>Third-octave band powers (Welch, Hann, 16384).</summary>
    private static double[] ThirdOctaves(float[] x)
    {
        const int n = 16384;
        var bands = new double[Thirds.Length];
        var win = new double[n];
        for (int i = 0; i < n; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        var buf = new Complex[n];
        int frames = 0;
        double edge = Math.Pow(2, 1.0 / 6.0);
        for (int s = 0; s + n <= x.Length; s += n / 2)
        {
            for (int i = 0; i < n; i++) buf[i] = new Complex(x[s + i] * win[i], 0);
            Spectrum.Fft(buf);
            for (int k = 1; k < n / 2; k++)
            {
                double f = k * (double)Rate / n;
                double p = buf[k].Magnitude * buf[k].Magnitude;
                for (int b = 0; b < Thirds.Length; b++)
                    if (f >= Thirds[b] / edge && f < Thirds[b] * edge) { bands[b] += p; break; }
            }
            frames++;
        }
        for (int b = 0; b < bands.Length; b++) bands[b] /= Math.Max(1, frames);
        return bands;
    }

    /// <summary>Least-squares slope of band level against octaves between two frequencies.</summary>
    private static double Slope(double[] bands, float from, float to)
    {
        var pts = new List<(double X, double Y)>();
        for (int b = 0; b < Thirds.Length; b++)
            if (Thirds[b] >= from && Thirds[b] <= to && bands[b] > 0)
                pts.Add((Math.Log2(Thirds[b]), 10 * Math.Log10(bands[b])));
        if (pts.Count < 2) return double.NaN;
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double num = pts.Sum(p => (p.X - mx) * (p.Y - my)), den = pts.Sum(p => (p.X - mx) * (p.X - mx));
        // Band POWER in a third-octave band of a density falling at s dB/oct rises 3 dB/oct on it
        // (each band is twice as wide an octave up): take the bandwidth back out.
        return num / den - 10 * Math.Log10(2);
    }

    private static float Arg(string[] args, string key, float fallback)
    {
        var a = args.FirstOrDefault(s => s.StartsWith(key, StringComparison.Ordinal));
        return a != null && float.TryParse(a[key.Length..], System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }

    private static void WriteStereo(string path, float[] l, float[] r)
    {
        int n = Math.Min(l.Length, r.Length);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + n * 4); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(n * 4);
        for (int i = 0; i < n; i++)
        {
            w.Write((short)Math.Clamp(l[i] * 32767f, -32768f, 32767f));
            w.Write((short)Math.Clamp(r[i] * 32767f, -32768f, 32767f));
        }
        Console.WriteLine($"   wrote {path}");
    }

    private static void WriteMono(string path, float[] pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
        Console.WriteLine($"   wrote {path}");
    }
}
