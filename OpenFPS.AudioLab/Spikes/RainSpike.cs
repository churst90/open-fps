using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --rain: rain on the surfaces round a listener, surveyed and rendered as the game does it, and
/// measured before anybody listens.
///
///   --rain [levels] [scene ...] [rate=light,moderate,heavy,violent|mm/h,...] [sec=20]
///        each scene at each rate: the survey's patches, then the drops rendered patch by patch at
///        the listener (RainSynth, the game's synthesiser) through each patch's path (SpatialAcoustics,
///        the game's), summed. Leq, LAeq, octave shape, headroom and texture (NatureSpike.Report, the
///        statistics the footstep rounds lacked), the share of each patch, and what a voice costs.
///   --rain render out=DIR [...]          the same, written as stereo WAVs (the listener faces north;
///        east is to the right) at the level the game plays them at its master (GameDb): the loudness
///        law at the default /levels, and the provider's own gain as --rain live measured it.
///   --rain live [sec=10]                 rain on the street at three rates, under a bus shelter, and an
///        air conditioner, a fountain and a tree for scale, each through the REAL provider (the
///        voices, the mixer, the HRTF, the master), captured and measured: what the game plays.
///   --rain physics                      the rain itself: drops per m² per second, the drop-size
///        closure, the kinetic energy against van Dijk et al. (2002), and the plate law for the roofs
///        in the scenes under natural rain and under ISO 10140-1's artificial heavy rain.
///   --rain survey map=city ear=x,y,z    the survey of a real place on a real map, and its render.
///   --rain compare=FILE.wav [...]        the same statistics for a recording (relative only).
///
/// Scenes: street (open asphalt), park (open grass), tree (grass under a park tree's crown),
/// shelter (a bus shelter: a sheet-steel canopy over the pavement, glass ends), room (a top-floor
/// room under a concrete slab, a metre from a window onto the street), car (beside a parked saloon
/// on the street), pond (beside open water), city (the bus shelter on the city map, as surveyed there).
/// </summary>
public static class RainSpike
{
    private const int Rate = 48000;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        var compares = args.Where(a => a.StartsWith("compare=", StringComparison.Ordinal)).Select(a => a[8..]).ToList();
        if (compares.Count > 0)
        {
            foreach (var path in compares)
            {
                var (pcm, sr) = NatureSpike.ReadWav(path);
                NatureSpike.Report(Path.GetFileName(path), pcm, sr, calibrated: false);
                Console.WriteLine("  " + Grain(pcm, sr));
                Console.WriteLine("  " + Balance(pcm, sr));
            }
            return 0;
        }
        _near = args.FirstOrDefault(a => a.StartsWith("near=", StringComparison.Ordinal))?[5..] ?? "on";
        if (args.Contains("physics")) return Physics();
        if (args.Contains("resolve")) return Resolve(Arg(args, "sec=", 10f));
        if (args.Contains("live")) return Live(Arg(args, "sec=", 10f));

        float sec = Arg(args, "sec=", 20f);
        var rates = Falls(args);
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);

        var scenes = new List<(string Name, Func<(WorldSnapshot World, Vector3 Ear, string About)> Make)>();
        if (args.Contains("survey"))
        {
            string mapId = args.FirstOrDefault(a => a.StartsWith("map=", StringComparison.Ordinal))?[4..] ?? "city";
            var ear = P(args.First(a => a.StartsWith("ear=", StringComparison.Ordinal))[4..]);
            string root = args.FirstOrDefault(a => a.StartsWith("root=", StringComparison.Ordinal))?[5..] ?? AppContext.BaseDirectory;
            Func<(WorldSnapshot, Vector3, string)> load = () => (OpenFPS.Client.Core.AudioEngine.SteamAudio.PathProbeSpike.LoadAsClient(root, mapId), ear, $"{mapId} at {ear}");
            scenes.Add(($"{mapId}@{ear.X:F0},{ear.Y:F0},{ear.Z:F0}", load));
        }
        else
        {
            var all = Scenes();
            var wanted = args.Where(a => !a.StartsWith("--") && !a.Contains('=') && a != "levels" && a != "render").ToList();
            foreach (var s in all)
                if (wanted.Count == 0 || wanted.Contains(s.Name, StringComparer.OrdinalIgnoreCase)) scenes.Add(s);
        }

        var summary = new List<string>();
        foreach (var (name, make) in scenes)
        {
            _riding = -1;
            var (world, ear, about) = make();
            // Sitting in a car, everything outside it comes through its shell, as the provider does it
            // (ClientAudioSystem.CabinEnclosure, the windows shut).
            _cabinDb = (0f, 0f, 0f);
            if (_riding >= 0 && OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Vehicle(world.Entities[_riding]) is { } cabin)
                _cabinDb = OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.LossDb(cabin, 0f);
            var survey = new RainSurvey { TraceColumns = args.Contains("columns") }.Run(world, ear, -1, _riding);
            if (survey.Trace != null) foreach (var line in survey.Trace) Console.WriteLine("    " + line);
            Console.WriteLine();
            Console.WriteLine($"== {name}: {about}");
            Console.WriteLine($"  survey: {survey.Describe()}");
            var acoustics = new SpatialAcoustics();
            var paths = new OpenFPS.Client.AudioEngine.Data.AcousticPathData?[OpenFPS.Client.AudioEngine.Fmod.RainFeeds.Slots];
            for (int s = 0; s < paths.Length; s++)
            {
                if (s == RainSurvey.OverheadSlot || survey.Patches[s] == null) continue;
                if (survey.Direct[s])
                {
                    // In view: the air alone, as the game gives it (RainField.Emitter).
                    var direct = new OpenFPS.Client.AudioEngine.Data.AcousticPathData(0f, survey.Centres[s], survey.Patches[s]!.ReferenceDistance);
                    (direct.AirLowDb, direct.AirMidDb, direct.AirHighDb) = OpenFPS.Client.AudioEngine.Core.AudioPhysics.AirLossDb(
                        survey.Patches[s]!.ReferenceDistance, world.Humidity, world.Temperature, world.AirPressure, world.AirAbsorptionMultiplier);
                    paths[s] = direct;
                    continue;
                }
                paths[s] = acoustics.CalculateAcousticPath(world, -1, ear, survey.Centres[s] + new Vector3(0f, 0.3f, 0f));
            }
            foreach (var (label, fall) in rates)
            {
                float mmh = fall.RateMmPerHour;
                var r = Render(survey, paths, ear, fall, sec, seed: 11);
                Console.WriteLine();
                Console.WriteLine($"  -- {name}, {label} ({OpenFPS.Server.Core.CommandHandler.DescribePrecipitation(fall)}): " +
                                  $"{r.Voices} voices, {r.CostPerVoice * 100:F2} % of a core each (worst {r.WorstCost * 100:F2} %)");
                foreach (var line in r.PatchLines) Console.WriteLine("    " + line);
                Console.WriteLine("    " + r.NearSummary);
                NatureSpike.Report($"{name} {label}", r.Mono, Rate, calibrated: true);
                Console.WriteLine("  " + Grain(r.Mono, Rate));
                Console.WriteLine("  " + Balance(r.Mono, Rate));
                summary.Add($"{name,-10} {label,-9} {mmh,5:F1} mm/h  in game {r.GameRmsDbfs,6:F1} dBFS (limiter -{r.LimitedDb:F1} dB)  Leq {Db(r.Mono),5:F1} dB  " +
                            $"LAeq {AWeighted(r.Mono),5:F1} dB(A)  headroom {Headroom(r.Mono),4:F1} dB  " +
                            $"low/mid/high octaves (125+250 / 1k+2k / 8k+16k) {r.BandSummary}");
                if (dir != null)
                {
                    string path = Path.Combine(dir, $"rain_{name}_{label}.wav");
                    WriteStereo(path, r.GameLeft, r.GameRight);
                    Console.WriteLine($"  wrote {path}");
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("== Summary (Leq at the listener; the scene's every patch summed)");
        foreach (var s in summary) Console.WriteLine("  " + s);
        return 0;
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    private sealed class Rendered
    {
        public float[] Mono = Array.Empty<float>(), Left = Array.Empty<float>(), Right = Array.Empty<float>();
        /// <summary>The same, at the level the game plays it (see <see cref="GameDb"/>), full scale 1.</summary>
        public float[] GameLeft = Array.Empty<float>(), GameRight = Array.Empty<float>();
        public double GameRmsDbfs;
        /// <summary>The most the master limiter took off, dB (0: it never acted).</summary>
        public double LimitedDb;
        public int Voices;
        public double CostPerVoice, WorstCost;
        public List<string> PatchLines = new();
        public int NearImpacts;
        public string NearSummary = "";
        public string BandSummary = "";
    }

    private static Rendered Render(RainSurvey.Result survey, OpenFPS.Client.AudioEngine.Data.AcousticPathData?[] paths,
                                   Vector3 ear, Precipitation fall, float sec, int seed)
    {
        int n = (int)(sec * Rate);
        var r = new Rendered { Mono = new float[n], Left = new float[n], Right = new float[n], GameLeft = new float[n], GameRight = new float[n] };
        double costSum = 0;
        // The near drops first: they tell the patches which drops are theirs (NearDrops.Plan).
        var near = new NearDrops(seed);
        var impacts = new List<NearDrops.Impact>();
        var bank = new DropBank();
        near.Plan(survey, fall, ear, -10.0, 0f, impacts, bank);
        for (int s = 0; s < survey.Patches.Length; s++)
        {
            var patch = survey.Patches[s];
            if (patch == null || _near == "only") continue;
            var synth = new RainSynth(Rate, seed * 31 + s) { Patch = patch, Falling = fall };
            var x = new float[n];
            var sw = Stopwatch.StartNew();
            float inv = 1f / patch.ReferenceDistance;
            // A second's settling first: the plates' fields and the ring start empty.
            for (int i = 0; i < Rate; i++) synth.Next();
            for (int i = 0; i < n; i++) x[i] = synth.Next() * inv;
            double cost = sw.Elapsed.TotalSeconds / (sec + 1f);
            costSum += cost;
            r.WorstCost = Math.Max(r.WorstCost, cost);
            r.Voices++;
            // What the game plays it at: the voice measures this level and the loudness law places it.
            double raw = 0; foreach (float v in x) raw += v * (double)v;
            double rawRms = Math.Sqrt(raw / n);
            float toGame = rawRms > 0 ? (float)(Math.Pow(10, GameDb(rawRms, patch.ReferenceDistance) / 20) / rawRms) : 0f;
            // The path: three bands as the mixer's THREE_EQ takes them, and the air.
            (float lo, float mid, float hi) eq = s == RainSurvey.OverheadSlot ? survey.OverheadEq : (1f, 1f, 1f);
            (float lo, float mid, float hi) air = (0f, 0f, 0f);
            if (paths[s] is { } p)
            {
                eq = (p.EqLow, p.EqMid, p.EqHigh);
                air = (p.AirLowDb, p.AirMidDb, p.AirHighDb);
            }
            if (s != RainSurvey.OverheadSlot || !survey.OverheadIsVehicle)
                (air.lo, air.mid, air.hi) = (air.lo + _cabinDb.Low, air.mid + _cabinDb.Mid, air.hi + _cabinDb.High);
            ThreeEq(x, eq.lo * MathF.Pow(10f, air.lo / 20f), eq.mid * MathF.Pow(10f, air.mid / 20f), eq.hi * MathF.Pow(10f, air.hi / 20f));
            double e = 0; foreach (float v in x) e += v * (double)v;
            double db = 10 * Math.Log10(Math.Max(1e-20, e / n) / 4e-10);
            // Placed: azimuth from the listener facing north (+z), east (+x) to the right.
            Vector3 at = s == RainSurvey.OverheadSlot ? ear + Vector3.UnitY : (paths[s]?.ApparentPosition ?? survey.Centres[s]);
            var to = at - ear;
            float az = MathF.Atan2(to.X, to.Z);                      // 0 ahead, +π/2 right
            float pan = s == RainSurvey.OverheadSlot ? 0f : MathF.Sin(az);
            float gl = MathF.Cos((pan + 1f) * MathF.PI / 4f), gr = MathF.Sin((pan + 1f) * MathF.PI / 4f);
            for (int i = 0; i < n; i++)
            {
                r.Mono[i] += x[i]; r.Left[i] += x[i] * gl; r.Right[i] += x[i] * gr;
                // The game's binaural voice puts a source's whole level in each ear (the live capture
                // measures it per channel), where a pan splits it: √2 per ear gives the same.
                r.GameLeft[i] += x[i] * gl * toGame * 1.4142135f; r.GameRight[i] += x[i] * gr * toGame * 1.4142135f;
            }
            string layers = string.Join(", ", patch.Layers.Select(l => $"{l.Kind}/{l.Material}{(l.FromBelow ? "↑" : "")}"));
            string extra = "";
            if (s == RainSurvey.OverheadSlot)
            {
                float p2 = 0f;
                foreach (var l in patch.Layers) if (l.Kind == RainSurfaceKind.Plate) p2 += l.Plate.MeanSquarePressure(fall.RateMmPerHour, l.ViewFactor);
                extra = $"; plate law predicts {10 * Math.Log10(Math.Max(1e-20, p2) / 4e-10):F1} dB before the path";
            }
            r.PatchLines.Add($"{RainSurvey.SlotName(s),-10} {db,5:F1} dB at the ear, peaks +{Headroom(x),4:F1} dB, centre {patch.ReferenceDistance,4:F1} m, " +
                             $"path {Db20(eq.lo):F0}/{Db20(eq.mid):F0}/{Db20(eq.hi):F0} dB, cost {cost * 100:F2} %: {layers}{extra}");
        }
        // The near drops, one by one, where they land, at the level the game places a one-off sound by
        // its peak, panned as the patches are.
        impacts.Clear();
        for (double t = 0; t < sec + 1.0; t += 0.05) near.Plan(survey, fall, ear, t, 0.05f, impacts, bank);
        int variant = 0;
        var nearMono = new float[n];
        foreach (var impact in impacts)
        {
            var sound = bank.Get(impact, variant++ % DropBank.Variants)!;
            int start = (int)((impact.At - 1.0) * Rate);
            if (start < 0 || start >= n || _near == "off") continue;
            r.NearImpacts++;
            float dist = MathF.Max(0.1f, Vector3.Distance(impact.Position, ear));
            float aim = (ear.Y - impact.Position.Y) / dist;
            float level = DropBank.LevelDb(sound, impact, aim);
            float pascalsAtEar = 20e-6f * MathF.Pow(10f, level / 20f) / dist;
            var (gain, reference) = Loudness.Place(level);
            // Through the roof's EQ (a car's headliner), or through the shell of the car the listener sits in.
            bool overhead = impact.FromBelow && impact.Slot == RainSurvey.OverheadSlot;
            var bands = overhead ? survey.OverheadEq : (1f, 1f, 1f);
            if (!(overhead && survey.OverheadIsVehicle))
                bands = (bands.Item1 * MathF.Pow(10f, _cabinDb.Low / 20f), bands.Item2 * MathF.Pow(10f, _cabinDb.Mid / 20f), bands.Item3 * MathF.Pow(10f, _cabinDb.High / 20f));
            var pcm = (float[])sound.Pcm.Clone();
            if (bands != (1f, 1f, 1f)) ThreeEq(pcm, bands.Item1, bands.Item2, bands.Item3);
            const float eq = 1f;
            float game = gain * MathF.Min(1f, reference / dist) * (float)Math.Pow(10, ProviderDb / 20) * 1.4142135f * eq;
            var to = impact.Position - ear;
            float pan = impact.FromBelow ? 0f : MathF.Sin(MathF.Atan2(to.X, to.Z)) * MathF.Min(1f, new Vector2(to.X, to.Z).Length() / MathF.Max(0.1f, dist));
            float gl = MathF.Cos((pan + 1f) * MathF.PI / 4f), gr = MathF.Sin((pan + 1f) * MathF.PI / 4f);
            for (int i = 0; i < pcm.Length && start + i < n; i++)
            {
                float x = pcm[i];
                r.Mono[start + i] += x * pascalsAtEar * eq;
                nearMono[start + i] += x * pascalsAtEar * eq;
                r.Left[start + i] += x * pascalsAtEar * eq * gl;
                r.Right[start + i] += x * pascalsAtEar * eq * gr;
                r.GameLeft[start + i] += x * game * gl;
                r.GameRight[start + i] += x * game * gr;
            }
        }
        double ne = 0; foreach (float v in nearMono) ne += v * (double)v;
        r.NearSummary = $"near drops: {r.NearImpacts / sec:F1} a second placed one by one (rain from {near.RainFromMm:F2} mm" +
                        (fall.Kind == PrecipitationKind.Hail ? $", hail from {near.HailFromMm:F1} mm" : "") +
                        $"), {10 * Math.Log10(Math.Max(1e-20, ne / n) / 4e-10):F1} dB at the ear; {bank.Made.Count()} sounds made";
        r.CostPerVoice = r.Voices > 0 ? costSum / r.Voices : 0;
        r.LimitedDb = MasterLimiter(r.GameLeft, r.GameRight);
        double ge = 0;
        for (int i = 0; i < n; i++) ge += 0.5 * (r.GameLeft[i] * (double)r.GameLeft[i] + r.GameRight[i] * (double)r.GameRight[i]);
        r.GameRmsDbfs = 10 * Math.Log10(Math.Max(1e-20, ge / n));
        var oct = OctaveDb(r.Mono);
        r.BandSummary = $"{Pow(oct, 125, 250):F1} / {Pow(oct, 1000, 2000):F1} / {Pow(oct, 8000, 16000):F1} dB";
        return r;
    }

    /// <summary>
    /// The grain of the 2-8 kHz band, where "staticy, grainy, scratchy" lives: its kurtosis (3 is a
    /// smooth wash, tens are separate clicks), and the crest of its 10 ms windows (peak over rms in
    /// each window; the median and the 95th percentile), computed the same way for a render and a
    /// recording so the two compare.
    /// </summary>
    /// <summary>
    /// How many separate impacts a second can be told apart. A Poisson train of drops on one surface,
    /// at a metre, sizes from moderate rain's spectrum above 1.5 mm, at rising rates; counted by the
    /// same onset detector the levels report uses (a 1 ms peak 12 dB over the median of the 200 ms
    /// round it, in each octave 250 Hz-8 kHz, the best octave kept). Where the count stops following
    /// the true rate is where impacts stop being events and become a texture.
    /// </summary>
    private static int Resolve(float sec)
    {
        var surfaces = new (string Name, RainLayer Layer)[]
        {
            ("asphalt", new RainLayer { Kind = RainSurfaceKind.Hard, Material = "Asphalt", ModulusGPa = 3f }),
            ("steel 0.7 mm", new RainLayer { Kind = RainSurfaceKind.Plate, Material = "Metal", ModulusGPa = 200f,
                                             Plate = new RainPlate("Metal", 0.0007f, 1.2f, 0.6f) }),
            ("puddle", new RainLayer { Kind = RainSurfaceKind.Pool, Material = "Water", ModulusGPa = 2.2f }),
            ("head (skin)", new RainLayer { Kind = RainSurfaceKind.Soft, Material = "Skin", ModulusGPa = 0.0015f,
                                            Stretch = RainSurfaces.ContactStretch(AcousticRegistry.GetProperties("Skin")) }),
        };
        var spectrum = new ParticleSpectrum();
        spectrum.Build(PrecipitationKind.Rain, 5f, Hydrometeors.MarshallPalmerMedianMm(5f));
        float[] rates = { 2, 4, 8, 12, 16, 24, 32, 48, 64, 96, 128 };
        var bank = new DropBank();
        int n = (int)(sec * Rate);
        Console.WriteLine($"Separate impacts a second counted, against the true rate ({sec:F0} s each, best octave 250 Hz-8 kHz):");
        Console.WriteLine("  true rate     " + string.Join(" ", rates.Select(r => $"{r,6:F0}")));
        foreach (var (name, layer) in surfaces)
        {
            var counted = new List<double>();
            foreach (float rate in rates)
            {
                var rng = new Random(7);
                var x = new float[n];
                int events = 0;
                double t = 0;
                while (true)
                {
                    t += -Math.Log(1.0 - rng.NextDouble()) / rate;
                    int at = (int)(t * Rate);
                    if (at >= n) break;
                    events++;
                    float d = spectrum.DrawAbove(1.5f, (float)rng.NextDouble());
                    var impact = new NearDrops.Impact(Vector3.Zero, layer, PrecipitationKind.Rain, d,
                                                      Hydrometeors.FallSpeed(PrecipitationKind.Rain, d), t, false, 0, false);
                    var sound = bank.Get(impact, events % DropBank.Variants)!;
                    float p = 20e-6f * MathF.Pow(10f, DropBank.LevelDb(sound, impact, 1f) / 20f);
                    for (int i = 0; i < sound.Pcm.Length && at + i < n; i++) x[at + i] += sound.Pcm[i] * p;
                }
                // A floor 50 dB under the drops' mean peak, so silence between them is not a median of zero.
                float floor = 0f;
                for (int i = 0; i < n; i++) floor = MathF.Max(floor, MathF.Abs(x[i]));
                floor *= 0.00316f;
                for (int i = 0; i < n; i++) x[i] += floor * (float)(rng.NextDouble() * 2.0 - 1.0);
                double best = 0;
                foreach (float c in new[] { 250f, 500f, 1000f, 2000f, 4000f, 8000f })
                    best = Math.Max(best, Onsets(NatureSpike.BandPass(x, Rate, c / MathF.Sqrt(2f), MathF.Min(c * MathF.Sqrt(2f), 0.45f * Rate)), Rate) / sec);
                counted.Add(best);
            }
            Console.WriteLine($"  {name,-13} " + string.Join(" ", counted.Select(c => $"{c,6:F1}")));
            int k = 0;
            while (k < rates.Length && counted[k] >= 0.8 * rates[k]) k++;
            double peak = counted.Max();
            Console.WriteLine($"  {"",-13} follows the rate (within a fifth) up to {(k > 0 ? rates[k - 1] : 0):F0} a second; never counts more than {peak:F1} a second");
        }
        return 0;
    }

    /// <summary>Onsets in a band: 1 ms peaks that are local maxima and stand 12 dB over the median of the
    /// 200 ms round them, no two within 30 ms (the ear's integration window: two clicks closer than
    /// that are heard as one).</summary>
    private static int Onsets(float[] band, int sr)
    {
        int pw = Math.Max(1, sr / 1000);
        var peaks = new List<double>();
        for (int s = 0; s + pw <= band.Length; s += pw)
        {
            double p = 0;
            for (int i = s; i < s + pw; i++) p = Math.Max(p, Math.Abs(band[i]));
            peaks.Add(p);
        }
        int count = 0, last = -1000;
        var round = new List<double>();
        for (int i = 0; i < peaks.Count; i++)
        {
            if (i > 0 && peaks[i - 1] >= peaks[i]) continue;
            if (i + 1 < peaks.Count && peaks[i + 1] > peaks[i]) continue;
            int a = Math.Max(0, i - 100), z = Math.Min(peaks.Count, i + 100);
            round.Clear();
            round.AddRange(peaks.GetRange(a, z - a));
            round.Sort();
            if (peaks[i] > 3.98 * round[round.Count / 2] && i - last >= 30) { count++; last = i; }
        }
        return count;
    }

    internal static string Grain(float[] x, int sr)
    {
        int n = Math.Min(x.Length, sr * 15);
        int size = 1;
        while (size < n) size <<= 1;
        var re = new double[size]; var im = new double[size];
        for (int i = 0; i < n; i++) re[i] = x[i];
        Fft(re, im);
        for (int k = 0; k < size; k++)
        {
            double f = (k <= size / 2 ? k : size - k) * (double)sr / size;
            if (f < 2000 || f > 8000) { re[k] = 0; im[k] = 0; }
        }
        for (int k = 0; k < size; k++) im[k] = -im[k];
        Fft(re, im);
        var y = new double[n];
        for (int i = 0; i < n; i++) y[i] = re[i] / size;
        double m2 = 0, m4 = 0;
        foreach (double v in y) { m2 += v * v; m4 += v * v * v * v; }
        m2 /= n; m4 /= n;
        double kurt = m4 / Math.Max(1e-30, m2 * m2);
        int w = sr / 100;
        var crests = new List<double>();
        for (int s = 0; s + w <= n; s += w)
        {
            double pk = 0, e = 0;
            for (int i = s; i < s + w; i++) { pk = Math.Max(pk, Math.Abs(y[i])); e += y[i] * y[i]; }
            if (e > 0) crests.Add(20 * Math.Log10(pk / Math.Sqrt(e / w)));
        }
        crests.Sort();
        double med = crests.Count > 0 ? crests[crests.Count / 2] : 0, p95 = crests.Count > 0 ? crests[(int)(0.95 * (crests.Count - 1))] : 0;
        return $"grain 2-8 kHz: kurtosis {kurt:F1}, 10 ms crest median {med:F1} dB, 95th percentile {p95:F1} dB";
    }

    /// <summary>Octave bands 250 Hz-16 kHz relative to the 1 kHz octave, dB.</summary>
    internal static string Balance(float[] x, int sr)
    {
        if (sr != Rate) return "balance: (resample to 48 kHz to compare)";
        var oct = OctaveDb(x);
        double r = oct[1000];
        return "balance re 1 kHz: " + string.Join("  ", new[] { 250, 500, 2000, 4000, 8000, 16000 }.Select(c => $"{c}:{oct[c] - r:+0.0;-0.0}"));
    }

    /// <summary>
    /// The level a patch plays at in the game, dBFS at the master, for its pressure at the ear: the
    /// voice measures its level (RainVoiceState) as a source at a metre placed at the patch's reference
    /// distance, renders it a fixed headroom under full scale and is given that headroom back over the
    /// fleet's shared one (PhysicalVoiceState.HeadroomGain); the loudness law places it (Loudness.Place
    /// with the extent, at the default /levels, 45 per cent); and the provider's own chain — the HRTF,
    /// the master trim — adds <see cref="ProviderDb"/>, measured with --rain live.
    /// </summary>
    internal static double GameDb(double pascalsAtEar, float referenceDistance)
    {
        double level = 20 * Math.Log10(Math.Max(1e-12, pascalsAtEar * referenceDistance) / 20e-6);
        var (gain, _) = Loudness.Place((float)level, referenceDistance);
        return 20 * Math.Log10(Math.Max(1e-9, gain)) - VehicleProfile.PeakHeadroomDb + ProviderDb;
    }

    /// <summary>What the provider's chain adds to the rain voices' channel levels at the master, dB:
    /// MEASURED with --rain live, 2026-10-05 (the street at three rates and the bus shelter, captured
    /// at the master against what the law predicts for the same patches: +5.0 to +5.6 dB; the master
    /// trim is 2 of it).</summary>
    internal const double ProviderDb = 5.5;

    private static float Db20(float g) => 20f * MathF.Log10(MathF.Max(1e-5f, g));

    /// <summary>A three-band EQ split as FMOD's THREE_EQ is, at 400 Hz and 4 kHz (24 dB/octave).</summary>
    internal static void ThreeEq(float[] x, float low, float mid, float high)
    {
        if (MathF.Abs(low - 1f) < 1e-4f && MathF.Abs(mid - 1f) < 1e-4f && MathF.Abs(high - 1f) < 1e-4f) return;
        var lo = (float[])x.Clone();
        var hi = (float[])x.Clone();
        Biquad(lo, 400f, lowpass: true); Biquad(lo, 400f, lowpass: true);
        Biquad(hi, 4000f, lowpass: false); Biquad(hi, 4000f, lowpass: false);
        for (int i = 0; i < x.Length; i++)
        {
            float m = x[i] - lo[i] - hi[i];
            x[i] = low * lo[i] + mid * m + high * hi[i];
        }
    }

    private static void Biquad(float[] x, float hz, bool lowpass)
    {
        double w = 2 * Math.PI * hz / Rate, c = Math.Cos(w), a = Math.Sin(w) / (2 * 0.7071);
        double b0 = lowpass ? (1 - c) / 2 : (1 + c) / 2, b1 = lowpass ? 1 - c : -(1 + c), b2 = b0;
        double a0 = 1 + a, a1 = -2 * c, a2 = 1 - a;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double xi = x[i], yi = (b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = xi; y2 = y1; y1 = yi;
            x[i] = (float)yi;
        }
    }

    // ── The scenes ──────────────────────────────────────────────────────────────────────────────

    private static int _nextId;

    private static List<(string Name, Func<(WorldSnapshot World, Vector3 Ear, string About)> Make)> Scenes() => new()
    {
        ("street", () =>
        {
            var w = World(); Ground(w, "Asphalt");
            return (w, new Vector3(0f, 1.6f, 0f), "the middle of an open asphalt street, nothing within 40 m");
        }),
        ("park", () =>
        {
            var w = World(); Ground(w, "Grass");
            return (w, new Vector3(0f, 1.6f, 0f), "the middle of an open lawn");
        }),
        ("tree", () =>
        {
            var w = World(); Ground(w, "Grass");
            Tree(w, new Vector3(0f, 7f, 0f));
            return (w, new Vector3(1.5f, 1.6f, 0f), "on the lawn under a park tree's crown (crown 4 m radius at 7 m), 1.5 m from the trunk");
        }),
        ("shelter", () =>
        {
            var w = World(); Ground(w, "Asphalt");
            // As the city's: 3.2 m deep, 4.4 long, roof at 2.4-2.5 m, glass ends.
            Box(w, "Metal", new Vector3(0f, 2.5f - MetalRoofSheet / 2f, 0f), new Vector3(3.2f, MetalRoofSheet, 4.4f));
            Box(w, "Glass", new Vector3(0f, 1.2f, -2.17f), new Vector3(3.2f, 2.4f, 0.06f), leaf: 0.006f);
            Box(w, "Glass", new Vector3(0f, 1.2f, 2.17f), new Vector3(3.2f, 2.4f, 0.06f), leaf: 0.006f);
            return (w, new Vector3(0f, 1.6f, 0f), "under a bus shelter: a single 0.7 mm sheet-steel canopy (the city's metal_roof) 2.5 m up, glass ends, on an asphalt street");
        }),
        ("room", () => Room(glazed: true)),
        ("room_open", () => Room(glazed: false)),
        ("attic", () =>
        {
            var w = World(); Ground(w, "Asphalt");
            // The same room under a single-skin sheet-steel roof: the case the concrete one is not.
            float f = 9f;
            Box(w, "Concrete", new Vector3(0f, f - 0.15f, 0f), new Vector3(6.6f, 0.3f, 6.6f));
            Box(w, "Metal", new Vector3(0f, f + 3.0f, 0f), new Vector3(6.6f, 0.0007f, 6.6f));
            Box(w, "Brick", new Vector3(-3.15f, f + 1.5f, 0f), new Vector3(0.3f, 3f, 6.6f));
            Box(w, "Brick", new Vector3(3.15f, f + 1.5f, 0f), new Vector3(0.3f, 3f, 6.6f));
            Box(w, "Brick", new Vector3(0f, f + 1.5f, -3.15f), new Vector3(6.6f, 3f, 0.3f));
            Box(w, "Brick", new Vector3(0f, f + 1.5f, 3.15f), new Vector3(6.6f, 3f, 0.3f));
            Box(w, "Brick", new Vector3(0f, (f - 0.3f) / 2f, 0f), new Vector3(6.6f, f - 0.3f, 6.6f));
            return (w, new Vector3(0f, f + 1.6f, 0f), "the same room under a single 0.7 mm sheet-steel roof, no ceiling");
        }),
        ("car", () =>
        {
            var w = World(); Ground(w, "Asphalt");
            Car(w, new Vector3(1.8f, 0f, 0f), "v6");
            return (w, new Vector3(0f, 1.6f, 0f), "on the street beside a parked saloon (the v6 sedan, its side 0.85 m to the right)");
        }),
        ("incar", () =>
        {
            var w = World(); Ground(w, "Asphalt");
            _riding = Car(w, new Vector3(0f, 0f, 0f), "v6");
            return (w, new Vector3(-0.35f, 1.15f, 0.1f), "in the driver's seat of a parked saloon (the v6 sedan) on the street");
        }),
        ("pond", () =>
        {
            var w = World(); Ground(w, "Grass");
            Box(w, "Water", new Vector3(0f, 0.05f, 6f), new Vector3(8f, 0.1f, 8f));
            return (w, new Vector3(0f, 1.6f, 0f), "on a lawn at the edge of a pond 8 m across (ahead)");
        }),
    };

    /// <summary>A top-floor room 6 x 6 x 3 m, its floor at 9 m, a 30 cm concrete roof slab, brick walls,
    /// a 1.4 x 1.5 m window in the north wall onto the street: shut (double glazed) or open.</summary>
    private static (WorldSnapshot, Vector3, string) Room(bool glazed)
    {
        var w = World(); Ground(w, "Asphalt");
        float f = 9f;
        Box(w, "Concrete", new Vector3(0f, f - 0.15f, 0f), new Vector3(6.6f, 0.3f, 6.6f));
        Box(w, "Concrete", new Vector3(0f, f + 3.15f, 0f), new Vector3(6.6f, 0.3f, 6.6f));
        Box(w, "Brick", new Vector3(-3.15f, f + 1.5f, 0f), new Vector3(0.3f, 3f, 6.6f));
        Box(w, "Brick", new Vector3(3.15f, f + 1.5f, 0f), new Vector3(0.3f, 3f, 6.6f));
        Box(w, "Brick", new Vector3(0f, f + 1.5f, -3.15f), new Vector3(6.6f, 3f, 0.3f));
        Box(w, "Brick", new Vector3(-2f, f + 1.5f, 3.15f), new Vector3(2.6f, 3f, 0.3f));
        Box(w, "Brick", new Vector3(2f, f + 1.5f, 3.15f), new Vector3(2.6f, 3f, 0.3f));
        Box(w, "Brick", new Vector3(0f, f + 0.45f, 3.15f), new Vector3(1.4f, 0.9f, 0.3f));
        Box(w, "Brick", new Vector3(0f, f + 2.7f, 3.15f), new Vector3(1.4f, 0.6f, 0.3f));
        if (glazed) Box(w, "Glass", new Vector3(0f, f + 1.65f, 3.15f), new Vector3(1.4f, 1.5f, 0.02f), leaf: 0.006f);
        // The rest of the building below the room, so the street is the street.
        Box(w, "Brick", new Vector3(0f, (f - 0.3f) / 2f, 0f), new Vector3(6.6f, f - 0.3f, 6.6f));
        return (w, new Vector3(0f, f + 1.6f, 2.0f),
                $"a top-floor room under a 30 cm concrete roof, a metre from a 1.4 x 1.5 m window onto the street, {(glazed ? "shut (double glazed)" : "open")}");
    }

    /// <summary>The bus shelter canopy's sheet, as the metal_roof prefab has it, m.</summary>
    private const float MetalRoofSheet = 0.0007f;

    private static WorldSnapshot World() => new() { StaticGrid = new SpatialGrid<int>(10.0f) };

    private static void Ground(WorldSnapshot w, string material)
        => Box(w, material, new Vector3(0f, -0.05f, 0f), new Vector3(200f, 0.1f, 200f));

    private static void Box(WorldSnapshot w, string material, Vector3 centre, Vector3 size, float leaf = 0f, bool solid = true)
    {
        int id = ++_nextId + 100;
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = solid },
            Material = new MaterialComponent { Material = material },
            Acoustics = new AcousticComponent { LeafMetres = leaf },
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        w.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
        if (solid) w.StaticGrid!.AddOverlapping(centre, size, Quaternion.Identity, id, isStatic: true);
    }

    private static void Tree(WorldSnapshot w, Vector3 crown)
    {
        int id = ++_nextId + 100;
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f), IsSolid = false },
            Material = new MaterialComponent { Material = "Foliage" },
            SoundEmitter = new SoundEmitterComponent { SoundId = "foliage:park_tree" },
            Transform = new Transform { Position = crown, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        w.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
        w.AudioEntityIds.Add(id);
    }

    /// <summary>The entity the scene's listener sits in, or -1.</summary>
    private static int _riding = -1;

    /// <summary>near=off renders the patches alone, near=only the near drops alone: to tell which of
    /// the two a fault is in.</summary>
    private static string _near = "on";

    /// <summary>What the shell of the car the listener sits in takes off everything outside it, dB per band.</summary>
    private static (float Low, float Mid, float High) _cabinDb;

    private static int Car(WorldSnapshot w, Vector3 restsAt, string preset)
    {
        int id = ++_nextId + 100;
        var p = MachineRegistry.VehicleFor(preset);
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Moves = true,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(p.WidthMetres, p.HeightMetres, p.LengthMetres), IsSolid = true },
            Material = new MaterialComponent { Material = "Metal" },
            SoundEmitter = new SoundEmitterComponent { SoundId = "engine:" + preset },
            Transform = new Transform { Position = restsAt, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        var snap = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
        w.Entities[id] = snap;
        w.DynamicEntities.Add(snap);
        return id;
    }

    // ── Through the real provider ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What the game plays: each case alone through a real FmodAudioProvider, its master captured.
    /// Rain the way RainField plays it (the patches' voices, placed by the level each measures);
    /// the others as ClientAudioSystem places a physical source (Loudness.Place with its extent and
    /// headroom gain). The first three seconds are the voices priming and finding their level.
    /// </summary>
    private static int Live(float sec)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-rain-live");
        Directory.CreateDirectory(dir);
        var ear = new Vector3(0f, 1.6f, 0f);
        var results = new List<(string Name, double Db, double Predicted)>();
        var scenes = Scenes().ToDictionary(s => s.Name, s => s.Make);

        foreach (var (name, scene, mmh) in new[] { ("rain, street, light", "street", Rainfall.LightRate),
                                                   ("rain, street, moderate", "street", Rainfall.ModerateRate),
                                                   ("rain, street, heavy", "street", Rainfall.HeavyRate),
                                                   ("rain, bus shelter, heavy", "shelter", Rainfall.HeavyRate) })
        {
            var (world, at, _) = scenes[scene]();
            var survey = new RainSurvey().Run(world, at, -1, -1);
            for (int s = 0; s < OpenFPS.Client.AudioEngine.Fmod.RainFeeds.Slots; s++)
            {
                var feed = OpenFPS.Client.AudioEngine.Fmod.RainFeeds.Feed[s];
                feed.Patch = survey.Patches[s];
                feed.Rate = mmh;
                feed.LevelDb = float.NaN;
            }
            double predicted = 0;
            string cap = Path.Combine(dir, $"{scene}_{mmh:F0}.wav");
            double db = Capture(cap, at, sec, provider =>
            {
                predicted = 0;
                for (int s = 0; s < survey.Patches.Length; s++)
                {
                    var patch = survey.Patches[s];
                    if (patch == null) continue;
                    float level = OpenFPS.Client.AudioEngine.Fmod.RainFeeds.Feed[s].LevelDb;
                    if (float.IsNaN(level)) level = 40f;
                    var (gain, reference) = Loudness.Place(level, patch.ReferenceDistance);
                    predicted += Math.Pow(10, (20 * Math.Log10(Math.Max(1e-9, gain)) - VehicleProfile.PeakHeadroomDb) / 10);
                    provider.PlaySpatialSound(new OpenFPS.Client.AudioEngine.Data.SpatialEmitter
                    {
                        EntityId = OpenFPS.Client.Core.RainField.VoiceBase - s,
                        SoundId = "rain",
                        IsSynth = true,
                        PhysicalKey = OpenFPS.Client.AudioEngine.Fmod.RainFeeds.Key(s),
                        EngineKey = "",
                        Mode = PlaybackMode.LoopOne,
                        Type = OpenFPS.Client.AudioEngine.Data.EmitterType.WorldLocked,
                        Position = survey.Centres[s],
                        ApparentPosition = survey.Centres[s],
                        Volume = gain * OpenFPS.Client.AudioEngine.Fmod.PhysicalVoiceState.HeadroomGain(OpenFPS.Client.AudioEngine.Fmod.RainVoiceState.HeadroomDb),
                        MinDistance = reference,
                        ExtentMetres = patch.ReferenceDistance,
                        Range = MathF.Max(60f, Loudness.AudibleRange(level)),
                        Pitch = 1f,
                        EngineRunning = true,
                        CarriesPath = true,
                        ApertureFactor = 1f,
                        EqLow = 1f, EqMid = 1f, EqHigh = 1f,
                        EffectiveDistance = patch.ReferenceDistance,
                        TargetRegionId = -1,
                    });
                }
            });
            results.Add((name, db, 10 * Math.Log10(Math.Max(1e-20, predicted))));
            for (int s = 0; s < OpenFPS.Client.AudioEngine.Fmod.RainFeeds.Slots; s++) OpenFPS.Client.AudioEngine.Fmod.RainFeeds.Feed[s].Patch = null;
        }

        foreach (var (name, key, level, extent, headroom, dist) in new[]
        {
            ("window air conditioner at 3 m", "machine:ac_window", SmallMachineSpec.ByName("ac_window").SourceLevelDb, SmallMachineSpec.ByName("ac_window").ExtentMetres, VehicleProfile.PeakHeadroomDb, 3f),
            ("park fountain at 10 m", "water:park_fountain", WaterFeatureSpec.ByName("park_fountain").SourceLevelDb, WaterFeatureSpec.ByName("park_fountain").ExtentMetres, WaterFeatureSpec.ByName("park_fountain").PeakHeadroomDb, 10f),
            ("park tree in the wind at 8 m", "foliage:park_tree", FoliageSpec.ByName("park_tree").SourceLevelDb, FoliageSpec.ByName("park_tree").ExtentMetres, FoliageSpec.ByName("park_tree").PeakHeadroomDb, 8f),
        })
        {
            var (gain, reference) = Loudness.Place(level, extent);
            string cap = Path.Combine(dir, key.Replace(':', '_') + ".wav");
            double db = Capture(cap, ear, sec, provider => provider.PlaySpatialSound(new OpenFPS.Client.AudioEngine.Data.SpatialEmitter
            {
                EntityId = 777001,
                Type = OpenFPS.Client.AudioEngine.Data.EmitterType.EntityAttached,
                IsSynth = true,
                PhysicalKey = key,
                EngineRunning = true,
                Position = ear + new Vector3(0f, 0f, dist),
                Direction = Vector3.UnitZ,
                Volume = gain * OpenFPS.Client.AudioEngine.Fmod.PhysicalVoiceState.HeadroomGain(headroom),
                MinDistance = reference,
                ExtentMetres = extent,
                Range = MathF.Max(60f, Loudness.AudibleRange(level)),
                TargetRegionId = -1,
            }));
            double predicted = 20 * Math.Log10(Math.Max(1e-9, gain * reference / Math.Max(reference, dist))) - VehicleProfile.PeakHeadroomDb;
            results.Add((name, db, predicted));
        }

        Console.WriteLine();
        Console.WriteLine("At the master, through the real provider (rms over the last seconds; the law's prediction for the channel levels alone):");
        foreach (var r in results)
            Console.WriteLine($"  {r.Name,-32} {r.Db,6:F1} dBFS   law {r.Predicted,6:F1} dBFS   provider adds {r.Db - r.Predicted:+0.0;-0.0} dB");
        Console.WriteLine($"  captures in {dir}");
        return 0;
    }

    /// <summary>One provider, the listener at <paramref name="ear"/> facing north, <paramref name="each"/>
    /// called every frame; the master captured to <paramref name="path"/>; its rms after the first three
    /// seconds, dBFS.</summary>
    private static double Capture(string path, Vector3 ear, float sec, Action<OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider> each)
    {
        // The mixer into a file instead of the sound card: exact, and silent.
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", path);
        var provider = new OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider();
        if (!provider.Initialize()) throw new InvalidOperationException("provider init failed");
        provider.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < sec)
        {
            each(provider);
            provider.Update();
            System.Threading.Thread.Sleep(16);
        }
        provider.Dispose();
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", null);
        var (pcm, sr) = ReadAnyWav(path);
        int from = Math.Min(pcm.Length, 3 * sr);
        double e = 0; int count = 0;
        for (int i = from; i < pcm.Length; i++) if (float.IsFinite(pcm[i])) { e += pcm[i] * (double)pcm[i]; count++; }
        return 10 * Math.Log10(Math.Max(1e-15, e / Math.Max(1, count)));
    }

    /// <summary>A WAV of 16-bit or 32-bit float samples, as the rms over its channels per frame.</summary>
    private static (float[] Pcm, int Rate) ReadAnyWav(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(12);
        int channels = 1, rate = 44100, bits = 16, format = 1;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            string id = new string(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (size > 16) r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                long avail = r.BaseStream.Length - r.BaseStream.Position;
                if (size <= 0 || size > avail) size = (int)avail;
                int bytes = bits / 8;
                int frames = size / (bytes * channels);
                var pcm = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    double e = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = bits == 32 && format == 3 ? r.ReadSingle() : bits == 16 ? r.ReadInt16() / 32768f : r.ReadInt32() / 2147483648f;
                        e += v * (double)v;
                    }
                    pcm[i] = (float)Math.Sqrt(e / channels);
                }
                return (pcm, rate);
            }
            else r.ReadBytes(size);
        }
        throw new InvalidDataException($"{path}: no data chunk");
    }

    // ── The physics, printed ────────────────────────────────────────────────────────────────────

    private static int Physics()
    {
        Console.WriteLine("Rain rate -> drops (Marshall-Palmer, Atlas speeds, closed on the rate)");
        Console.WriteLine("  mm/h   class      MP raw rate  closure  drops/m²/s  KE J/m²/mm  van Dijk 2002");
        foreach (float r in new[] { 0.5f, Rainfall.LightRate, Rainfall.ModerateRate, 10f, Rainfall.HeavyRate, 50f, Rainfall.ViolentRate })
        {
            float ke = Rainfall.KineticPower(r) * 3600f / r;
            float vd = 28.3f * (1f - 0.52f * MathF.Exp(-0.042f * r));
            Console.WriteLine($"  {r,5:F1}  {Rainfall.Category(r),-9}  {Rainfall.UnclosedRate(r),8:F2}    {Rainfall.Closure(r),5:F3}  {Rainfall.DropsPerSquareMetreSecond(r),9:F0}  {ke,9:F1}   {vd,9:F1}");
        }
        Console.WriteLine();
        Console.WriteLine("Intensity -> rate (server): " + string.Join("  ", new[] { 0.05f, 0.1f, 0.3f, 0.5f, 0.6f, 0.8f, 1f }
            .Select(i => $"{i:F2}:{Rainfall.RateFromIntensity(i):F1}")));
        Console.WriteLine();
        var plates = new (string Name, RainPlate Plate)[]
        {
            ("0.7 mm steel sheet, 1.2 m bays (bus shelter canopy leaf)", new RainPlate("Metal", 0.0007f, 1.2f, 1.2f)),
            ("0.8 mm steel car panel (saloon, deadened)", new RainPlate("Metal", 0.0008f, 0.35f, 0.28f, 0.12f)),
            ("6 mm glass pane, 1.2 m", new RainPlate("Glass", 0.006f, 1.2f, 1.2f)),
            ("4.5 mm laminated windscreen", new RainPlate("Glass", RainSurfaces.CarGlassMetres, 1.4f, 0.5f, RainSurfaces.CarGlassLoss)),
            ("18 mm timber board", new RainPlate("Wood", 0.018f, 1.2f, 0.6f)),
            ("150 mm concrete slab", new RainPlate("Concrete", 0.15f, 1.2f, 1.2f)),
            ("300 mm concrete slab", new RainPlate("Concrete", 0.30f, 1.2f, 1.2f)),
        };
        // A listener 1 m under a roof 10 m across: ∫ dA / r² = π ln((R² + h²) / h²).
        float g = MathF.PI * MathF.Log((25f + 1f) / 1f);
        Console.WriteLine($"The plate law, one face, at a listener 1 m under the middle of a roof 10 m across (view factor {g:F1}):");
        Console.WriteLine("  plate                                                     fc Hz   overlap Hz   light  moderate  heavy  violent  (dB)   LI per m² under ISO heavy rain");
        foreach (var (name, plate) in plates)
        {
            string levels = string.Join("  ", new[] { Rainfall.LightRate, Rainfall.ModerateRate, Rainfall.HeavyRate, Rainfall.ViolentRate }
                .Select(r => $"{10 * MathF.Log10(plate.MeanSquarePressure(r, g) / 4e-10f),6:F1}"));
            Console.WriteLine($"  {name,-56} {plate.CriticalHz,7:F0}  {plate.OverlapHz,9:F0}  {levels}    {IsoIntensityDb(plate),5:F1} dB");
        }
        Console.WriteLine();
        Console.WriteLine("ISO 10140-1:2016 Annex K / ISO 140-18 'heavy' artificial rain, as I have it: 40 mm/h of 5 mm drops at 7 m/s");
        Console.WriteLine("(per the standard: check the figures before quoting). LI is the radiated sound intensity level, one face,");
        Console.WriteLine("with the same law: the number published rain-noise tests report for roofs and glazing.");
        return 0;
    }

    /// <summary>The plate law's radiated intensity under ISO's artificial heavy rain, dB re 1 pW/m².</summary>
    private static float IsoIntensityDb(RainPlate plate)
    {
        const float rate = 40f, d = 5f, v = 7f;
        float perDrop = MathF.PI / 6f * MathF.Pow(d * 1e-3f, 3f);
        float flux = rate / 3.6e6f / perDrop;                       // drops / m² s
        double w = 0;
        float tau = RainPlate.BlowSeconds(d, v), blow = RainPlate.BlowEnergy(d, v);
        for (float f = 31.5f; f < 20000f; f *= 2f)
        {
            float lo = f / MathF.Sqrt(2f), hi = f * MathF.Sqrt(2f);
            double eIn = plate.Mobility * blow * RainPlate.BlowShare(tau, lo, hi);
            w += flux * eIn / (2 * Math.PI * f * plate.Loss(f)) * RainPlate.AirImpedance * plate.RadiationEfficiency(f) / plate.SurfaceDensity;
        }
        // The near field: ρ0² ∫F² dt / (4π² m″²) per blow at a metre is a pressure; as an intensity per
        // m², the power of a baffled monopole of volume acceleration F/m″: ρ0 (F/m″)² / (2π c).
        w += flux * WallTransmission.AirDensity * blow / (2 * Math.PI * WallTransmission.SoundSpeed * plate.SurfaceDensity * plate.SurfaceDensity);
        return (float)(10 * Math.Log10(Math.Max(1e-20, w) / 1e-12));
    }

    // ── Measurement helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What to render falling: fall=KIND[:WORD...] as many times as wanted, the words as /weather takes
    /// them ("fall=rain:heavy:drops:3", "fall=drizzle", "fall=hail:golf", "fall=snow:heavy",
    /// "fall=rain:45:dbz"); or rate=light,moderate,... for rain; or the four rain rates.
    /// </summary>
    private static List<(string Label, Precipitation Fall)> Falls(string[] args)
    {
        var list = new List<(string, Precipitation)>();
        foreach (var a in args.Where(a => a.StartsWith("fall=", StringComparison.Ordinal)))
        {
            var words = a[5..].Split(':');
            string first = words[0].ToLowerInvariant();
            var kind = first switch
            {
                "sleet" => PrecipitationKind.Sleet, "snow" => PrecipitationKind.Snow, "hail" => PrecipitationKind.Hail,
                "freezing" => PrecipitationKind.FreezingRain, _ => PrecipitationKind.Rain,
            };
            var rest = first == "drizzle" ? words : words.Skip(1).ToArray();
            if (!OpenFPS.Server.Core.CommandHandler.TryReadPrecipitation(kind, rest, out var p, out string? error))
                throw new ArgumentException($"{a}: {error}");
            list.Add((a[5..].Replace(':', '_'), p));
        }
        if (list.Count > 0) return list;
        foreach (var (label, mmh) in Rates(args.FirstOrDefault(a => a.StartsWith("rate=", StringComparison.Ordinal))?[5..]))
            list.Add((label, new Precipitation(PrecipitationKind.Rain, mmh)));
        return list;
    }

    private static List<(string Label, float Mmh)> Rates(string? spec)
    {
        var all = new List<(string, float)>
        {
            ("light", Rainfall.LightRate), ("moderate", Rainfall.ModerateRate),
            ("heavy", Rainfall.HeavyRate), ("violent", Rainfall.ViolentRate),
        };
        if (spec == null) return all;
        var list = new List<(string, float)>();
        foreach (var part in spec.Split(','))
        {
            var hit = all.FirstOrDefault(a => a.Item1.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (hit.Item1 != null) list.Add(hit);
            else if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) list.Add(($"{v:F0}mmh", v));
        }
        return list;
    }

    private static double Db(float[] x)
    {
        double e = 0; foreach (float v in x) e += v * (double)v;
        return 10 * Math.Log10(Math.Max(1e-20, e / Math.Max(1, x.Length)) / 4e-10);
    }

    private static double Headroom(float[] x)
    {
        double e = 0; foreach (float v in x) e += v * (double)v;
        double rms = Math.Sqrt(e / Math.Max(1, x.Length));
        var peaks = new List<float>();
        int w = Rate / 100;
        for (int s = 0; s + w <= x.Length; s += w)
        {
            float pk = 0f;
            for (int i = s; i < s + w; i++) pk = MathF.Max(pk, MathF.Abs(x[i]));
            peaks.Add(pk);
        }
        peaks.Sort();
        return peaks.Count == 0 ? 0 : 20 * Math.Log10(Math.Max(1e-12, peaks[(int)(0.999 * (peaks.Count - 1))]) / Math.Max(1e-12, rms));
    }

    /// <summary>Octave band levels, dB SPL, centres 31.5 Hz to 16 kHz, by FFT.</summary>
    private static Dictionary<int, double> OctaveDb(float[] x)
    {
        int seg = 8192;
        var power = new double[seg / 2 + 1];
        var re = new double[seg]; var im = new double[seg];
        int segs = 0;
        for (int s = 0; s + seg <= x.Length && segs < 40; s += seg, segs++)
        {
            for (int i = 0; i < seg; i++) { double h = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / seg); re[i] = x[s + i] * h; im[i] = 0; }
            Fft(re, im);
            for (int k = 0; k <= seg / 2; k++) power[k] += re[k] * re[k] + im[k] * im[k];
        }
        var bands = new Dictionary<int, double>();
        double total = power.Sum();
        double e = 0; foreach (float v in x) e += v * (double)v;
        double scale = (e / Math.Max(1, x.Length)) / Math.Max(1e-30, total);
        foreach (int c in new[] { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 })
        {
            double lo = c / Math.Sqrt(2), hi = c * Math.Sqrt(2), sum = 0;
            for (int k = 1; k <= seg / 2; k++) { double f = k * (double)Rate / seg; if (f >= lo && f < hi) sum += power[k]; }
            bands[c] = 10 * Math.Log10(Math.Max(1e-30, sum * scale) / 4e-10);
        }
        return bands;
    }

    private static double Pow(Dictionary<int, double> oct, int a, int b)
        => 10 * Math.Log10(Math.Pow(10, oct[a] / 10) + Math.Pow(10, oct[b] / 10));

    private static double AWeighted(float[] x)
    {
        var oct = OctaveDb(x);
        var a = new Dictionary<int, double> { [31] = -39.4, [63] = -26.2, [125] = -16.1, [250] = -8.6, [500] = -3.2, [1000] = 0, [2000] = 1.2, [4000] = 1.0, [8000] = -1.1, [16000] = -6.6 };
        double sum = 0; foreach (var kv in oct) sum += Math.Pow(10, (kv.Value + a[kv.Key]) / 10);
        return 10 * Math.Log10(Math.Max(1e-30, sum));
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
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    /// <summary>
    /// The game's master limiter (FmodAudioProvider: FMOD's LIMITER at the head of the master, ceiling
    /// -2 dBFS, 50 ms release, no look-ahead), so a render is what the game lets out: the gain drops at
    /// once to hold a peak under the ceiling and comes back over the release. Returns the most it took off.
    /// </summary>
    private static double MasterLimiter(float[] left, float[] right)
    {
        const float Ceiling = 0.794f;                                   // -2 dBFS
        float release = MathF.Exp(-1f / (0.050f * Rate));
        float gain = 1f, least = 1f;
        for (int i = 0; i < left.Length; i++)
        {
            float peak = MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i]));
            float target = peak > Ceiling ? Ceiling / peak : 1f;
            gain = target < gain ? target : target - (target - gain) * release;
            left[i] *= gain; right[i] *= gain;
            least = MathF.Min(least, gain);
        }
        return -20.0 * Math.Log10(least);
    }

    /// <summary>A stereo WAV in 32-bit float (format 3). Rain at game level is -40 to -70 dBFS, and in
    /// 16 bits the quietest of it sat 32 dB over the step floor (docs/AUDIO_QUALITY_2026-10-06.md item 5).</summary>
    internal static void WriteStereo(string path, float[] left, float[] right)
    {
        using var w = new BinaryWriter(File.Create(path));
        int n = left.Length;
        w.Write("RIFF"u8); w.Write(36 + n * 8); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)3); w.Write((short)2);
        w.Write(Rate); w.Write(Rate * 8); w.Write((short)8); w.Write((short)32); w.Write("data"u8); w.Write(n * 8);
        for (int i = 0; i < n; i++)
        {
            w.Write(left[i]);
            w.Write(right[i]);
        }
    }

    private static Vector3 P(string s)
    {
        var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return new Vector3(f[0], f[1], f[2]);
    }

    private static float Arg(string[] args, string prefix, float fallback)
        => float.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length),
                          NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}
