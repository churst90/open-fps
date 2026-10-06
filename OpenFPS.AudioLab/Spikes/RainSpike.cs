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
///        east is to the right) at ONE shared gain: 94 dB SPL is −6 dBFS, so the files compare by level
///        as well as by ear.
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
    private const float PascalsToFull = 0.5f;     // 94 dB SPL = −6 dBFS

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
            }
            return 0;
        }
        if (args.Contains("physics")) return Physics();

        float sec = Arg(args, "sec=", 20f);
        var rates = Rates(args.FirstOrDefault(a => a.StartsWith("rate=", StringComparison.Ordinal))?[5..]);
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
            var (world, ear, about) = make();
            var survey = new RainSurvey { TraceColumns = args.Contains("columns") }.Run(world, ear, -1, -1);
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
            foreach (var (label, mmh) in rates)
            {
                var r = Render(survey, paths, ear, mmh, sec, seed: 11);
                Console.WriteLine();
                Console.WriteLine($"  -- {name}, {label} rain ({mmh:F1} mm/h, {Rainfall.Category(mmh)}): " +
                                  $"{r.Voices} voices, {r.CostPerVoice * 100:F2} % of a core each (worst {r.WorstCost * 100:F2} %)");
                foreach (var line in r.PatchLines) Console.WriteLine("    " + line);
                NatureSpike.Report($"{name} {label}", r.Mono, Rate, calibrated: true);
                summary.Add($"{name,-10} {label,-9} {mmh,5:F1} mm/h  Leq {Db(r.Mono),5:F1} dB  " +
                            $"LAeq {AWeighted(r.Mono),5:F1} dB(A)  headroom {Headroom(r.Mono),4:F1} dB  " +
                            $"low/mid/high octaves (125+250 / 1k+2k / 8k+16k) {r.BandSummary}");
                if (dir != null)
                {
                    string path = Path.Combine(dir, $"rain_{name}_{label}.wav");
                    WriteStereo(path, r.Left, r.Right);
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
        public int Voices;
        public double CostPerVoice, WorstCost;
        public List<string> PatchLines = new();
        public string BandSummary = "";
    }

    private static Rendered Render(RainSurvey.Result survey, OpenFPS.Client.AudioEngine.Data.AcousticPathData?[] paths,
                                   Vector3 ear, float mmh, float sec, int seed)
    {
        int n = (int)(sec * Rate);
        var r = new Rendered { Mono = new float[n], Left = new float[n], Right = new float[n] };
        double costSum = 0;
        for (int s = 0; s < survey.Patches.Length; s++)
        {
            var patch = survey.Patches[s];
            if (patch == null) continue;
            var synth = new RainSynth(Rate, seed * 31 + s) { Patch = patch, RainRate = mmh };
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
            // The path: three bands as the mixer's THREE_EQ takes them, and the air.
            (float lo, float mid, float hi) eq = s == RainSurvey.OverheadSlot ? survey.OverheadEq : (1f, 1f, 1f);
            (float lo, float mid, float hi) air = (0f, 0f, 0f);
            if (paths[s] is { } p)
            {
                eq = (p.EqLow, p.EqMid, p.EqHigh);
                air = (p.AirLowDb, p.AirMidDb, p.AirHighDb);
            }
            ThreeEq(x, eq.lo * MathF.Pow(10f, air.lo / 20f), eq.mid * MathF.Pow(10f, air.mid / 20f), eq.hi * MathF.Pow(10f, air.hi / 20f));
            double e = 0; foreach (float v in x) e += v * (double)v;
            double db = 10 * Math.Log10(Math.Max(1e-20, e / n) / 4e-10);
            // Placed: azimuth from the listener facing north (+z), east (+x) to the right.
            Vector3 at = s == RainSurvey.OverheadSlot ? ear + Vector3.UnitY : (paths[s]?.ApparentPosition ?? survey.Centres[s]);
            var to = at - ear;
            float az = MathF.Atan2(to.X, to.Z);                      // 0 ahead, +π/2 right
            float pan = s == RainSurvey.OverheadSlot ? 0f : MathF.Sin(az);
            float gl = MathF.Cos((pan + 1f) * MathF.PI / 4f), gr = MathF.Sin((pan + 1f) * MathF.PI / 4f);
            for (int i = 0; i < n; i++) { r.Mono[i] += x[i]; r.Left[i] += x[i] * gl; r.Right[i] += x[i] * gr; }
            string layers = string.Join(", ", patch.Layers.Select(l => $"{l.Kind}/{l.Material}{(l.FromBelow ? "↑" : "")}"));
            string extra = "";
            if (s == RainSurvey.OverheadSlot)
            {
                float p2 = 0f;
                foreach (var l in patch.Layers) if (l.Kind == RainSurfaceKind.Plate) p2 += l.Plate.MeanSquarePressure(mmh, l.ViewFactor);
                extra = $"; plate law predicts {10 * Math.Log10(Math.Max(1e-20, p2) / 4e-10):F1} dB before the path";
            }
            r.PatchLines.Add($"{RainSurvey.SlotName(s),-10} {db,5:F1} dB at the ear, peaks +{Headroom(x),4:F1} dB, centre {patch.ReferenceDistance,4:F1} m, " +
                             $"path {Db20(eq.lo):F0}/{Db20(eq.mid):F0}/{Db20(eq.hi):F0} dB, cost {cost * 100:F2} %: {layers}{extra}");
        }
        r.CostPerVoice = r.Voices > 0 ? costSum / r.Voices : 0;
        var oct = OctaveDb(r.Mono);
        r.BandSummary = $"{Pow(oct, 125, 250):F1} / {Pow(oct, 1000, 2000):F1} / {Pow(oct, 8000, 16000):F1} dB";
        return r;
    }

    private static float Db20(float g) => 20f * MathF.Log10(MathF.Max(1e-5f, g));

    /// <summary>A three-band EQ split as FMOD's THREE_EQ is, at 400 Hz and 4 kHz (24 dB/octave).</summary>
    private static void ThreeEq(float[] x, float low, float mid, float high)
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

    private static void Car(WorldSnapshot w, Vector3 restsAt, string preset)
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

    private static void WriteStereo(string path, float[] left, float[] right)
    {
        using var w = new BinaryWriter(File.Create(path));
        int n = left.Length;
        w.Write("RIFF"u8); w.Write(36 + n * 4); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(n * 4);
        for (int i = 0; i < n; i++)
        {
            w.Write((short)Math.Clamp(left[i] * PascalsToFull * 32767f, -32768f, 32767f));
            w.Write((short)Math.Clamp(right[i] * PascalsToFull * 32767f, -32768f, 32767f));
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
