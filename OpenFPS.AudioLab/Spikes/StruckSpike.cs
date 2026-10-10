using System.Diagnostics;
using System.Globalization;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// Struck things (docs/MATTER.md section 7): taps, knocks and bumps from a thing's own material, shape and size.
///
///   --struck [material=Aluminium] [shape=block|bar|plate|freeplate|tube|shellbox] [size=0.2x0.2x0.2] [wall=M]
///            [support=hung|resting|held|built] [striker=finger|knuckle|palm|toe|body|rod] [speed=M/S]
///            [at=U,V] [seed=N] [out=FILE.wav]
///                         one strike: its first modes (note, ring time), its band balance and level, a WAV
///   --struck anchor       the level anchor: the model's heel on a slab against the footstep bank
///   --struck fit [recording=FILE]
///                         the knuckle against the door-knock recording (inbox/door sounds), band by band
///   --struck renders [out=DIR]
///                         the listening set, at game level, measured
///   --struck cost         render times
///
/// Every level is dB SPL at a metre, anchored to the footstep takes (StruckThings.LevelAnchorDb).
/// </summary>
public static class StruckSpike
{
    private const int Rate = 48000;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.Contains("anchor")) return Anchor(args);
        if (args.Contains("fit")) return Fit(args);
        if (args.Contains("renders")) return Renders(args);
        if (args.Contains("cost")) return Cost();
        return One(args);
    }

    // ── One strike ─────────────────────────────────────────────────────────────────────────────────

    private static int One(string[] args)
    {
        var thing = ThingFrom(args);
        var striker = Striker.TryByName(Arg(args, "striker=") ?? "knuckle", out var s) ? s : Striker.Knuckle;
        float speed = Num(args, "speed=", DefaultSpeed(striker));
        var at = (Arg(args, "at=") ?? "0.5,0.5").Split(',').Select(v => float.Parse(v, Ci)).ToArray();
        var strike = new Strike(thing, new[] { new Blow(striker, speed, at[0], at.Length > 1 ? at[1] : 0.5f) }, (int)Num(args, "seed=", 0));
        // A bump of the listening set by name (bump=glass-door): its thing and a walking body.
        if (Arg(args, "bump=") is { } bump && BumpTargets().FirstOrDefault(b => b.File == bump) is { Thing.Material: not null } target)
            strike = new Strike(target.Thing, StruckThings.BodyBump(Num(args, "speed=", 1.2f), 0.45f, 0.62f, target.LengthIsUp, 0f));
        _ = StruckThings.LevelAnchorDb;   // worked out with every part heard, before any are muted
        if (Arg(args, "only=") is { } only) StruckThings.Heard = int.Parse(only, Ci);
        Report(strike, Arg(args, "out="), args);
        StruckThings.Heard = -1;
        if (args.Contains("parts"))
        {
            // Each part alone: where the energy comes from.
            _ = StruckThings.LevelAnchorDb;
            foreach (var (bit, name) in new[] { (1, "listed modes"), (2, "dense field"), (4, "striker's stop"), (8, "own motion and ground"), (16, "near field"), (32, "loose fit") })
            {
                StruckThings.Heard = bit;
                var p = StruckThings.RenderPascals(strike, Rate, out float pk);
                StruckThings.Heard = -1;
                if (pk <= 0) continue;
                var bands = Octaves(p, 0, Rate * 120 / 1000);
                Console.WriteLine($"  {name,-22} peak {20 * MathF.Log10(pk / 2e-5f) + StruckThings.LevelAnchorDb,6:F1} dB  sound exposure by band (dB re 20 uPa^2 s): "
                                + string.Join(" ", bands.Select((e, i) => $"{Spectrum.BandEdges[i]:F0}:{10 * MathF.Log10(MathF.Max(e, 1e-30f) / Rate / 4e-10f),5:F0}")));
            }
        }
        return 0;
    }

    internal static StruckThing ThingFrom(string[] args)
    {
        var size = (Arg(args, "size=") ?? "0.2x0.2x0.2").Split('x').Select(v => float.Parse(v, Ci)).ToArray();
        var shape = Enum.Parse<StruckShape>(Arg(args, "shape=") ?? "block", ignoreCase: true);
        var support = Enum.Parse<StruckSupport>(Arg(args, "support=") ?? "resting", ignoreCase: true);
        return new StruckThing
        {
            Material = Arg(args, "material=") ?? "Aluminium", Shape = shape, Length = size[0], Width = size.Length > 1 ? size[1] : size[0],
            Thickness = size.Length > 2 ? size[2] : size[0], Wall = Num(args, "wall=", 0f), Support = support,
        };
    }

    private static float DefaultSpeed(Striker s) => s.Name switch
    {
        "finger" => 0.8f, "knuckle" => 1.5f, "palm" => 2f, "toe" => 1f, "body" => 1.4f, "rod" => 1f, _ => 1f,
    };

    private static void Report(Strike strike, string? outPath, string[]? args = null)
    {
        var t = strike.Thing;
        var (modes, dense) = StruckThings.ModesOf(t, strike.Blows[0].U, strike.Blows[0].V, strike.Seed);
        Console.WriteLine($"{t.Material} {t.Shape} {t.Length:0.###} x {t.Width:0.###} x {t.Thickness:0.###} m, {t.Support}, {t.MassKg:0.###} kg; "
                        + string.Join(", ", strike.Blows.Select(b => $"{b.Striker.Name} at {b.Speed:0.##} m/s")));
        Console.WriteLine($"  {modes.Count} modes listed{(dense > 0 ? $", a dense field above {dense:F0} Hz" : "")}; first ones:");
        foreach (var md in modes.Where((_, i) => i < 8 || (args != null && args.Contains("all") && i % 10 == 0)))
        {
            double t60 = 2.2 / (md.Loss * md.Hz);
            Console.WriteLine($"    {md.Hz,9:F1} Hz  T60 {t60,7:F3} s  loss {md.Loss:0.#####}  drive {md.Drive,6:F2}  radiation {md.Radiation,8:F2} Pa/(m/s)");
        }
        var sw = Stopwatch.StartNew();
        var p = StruckThings.RenderPascals(strike, Rate, out float peak);
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine($"  first blow: in contact {StruckThings.LastContactSeconds * 1000:F2} ms, peak force {StruckThings.LastPeakForce:F0} N");
        float anchored = 20f * MathF.Log10(MathF.Max(peak, 1e-9f) / 2e-5f) + StruckThings.LevelAnchorDb;
        Console.WriteLine($"  peak {anchored:F1} dB SPL at 1 m (anchored), loudest 20 ms {StruckThings.Loudest20msDb(p, Rate) + StruckThings.LevelAnchorDb:F1} dB, "
                        + $"{p.Length / (float)Rate:F2} s, rendered in {ms} ms");
        var bands = Shape(Octaves(p, 0, Rate * 120 / 1000));
        Console.WriteLine("  first 120 ms, dB of the total by band: " + string.Join("  ", bands.Select((b, i) => $"{Spectrum.BandEdges[i]:F0}:{b:F1}")));
        if (outPath != null)
        {
            var y = new float[p.Length];
            for (int i = 0; i < y.Length; i++) y[i] = peak > 0 ? p[i] / peak * 0.5f : 0f;
            WriteFloatWav(outPath, y);
            Console.WriteLine($"  wrote {outPath} (peak -6 dBFS)");
        }
    }

    // ── The level anchor ─────────────────────────────────────────────────────────────────────────

    private static int Anchor(string[] args)
    {
        // The takes, measured as the anchor measures the model: A-weighted, the loudest 20 ms.
        string takes = Arg(args, "takes=") ?? LabPaths.Sounds("FOOTSTEPS", "Concrete");
        if (Directory.Exists(takes))
        {
            var levels = new List<float>();
            foreach (var f in Directory.GetFiles(takes).Where(f => f.EndsWith(".ogg") || f.EndsWith(".wav")).OrderBy(f => f))
            {
                var pcm = Decode(f);
                if (pcm.Length == 0) continue;
                levels.Add(StruckThings.LoudestA20msDb(pcm, Rate) - 93.98f);
            }
            levels.Sort();
            if (levels.Count > 0)
                Console.WriteLine($"{levels.Count} takes in {takes}: loudest 20 ms A-weighted, median {levels[levels.Count / 2]:F1} dBFS "
                                + $"(quartiles {levels[levels.Count / 4]:F1} and {levels[3 * levels.Count / 4]:F1}); "
                                + $"StruckThings.FootstepTakeLoudestA20msDbfs is {StruckThings.FootstepTakeLoudestA20msDbfs:F1}");
        }
        float anchor = StruckThings.LevelAnchorDb;
        Console.WriteLine($"Heel on a 150 mm slab (StruckThings.Slab), the model's own: loudest 20 ms {StruckThings.ModelFootstepDb:F1} dB(A) at 1 m");
        Console.WriteLine($"The bank's concrete takes as played: {Loudness.FootstepDb:F1} + {StruckThings.FootstepTakeLoudestA20msDbfs:F1} = "
                        + $"{Loudness.FootstepDb + StruckThings.FootstepTakeLoudestA20msDbfs:F1} dB(A)");
        Console.WriteLine($"Anchor: {anchor:+0.0;-0.0} dB on every strike");
        return 0;
    }

    // ── The knuckle's fit ─────────────────────────────────────────────────────────────────────────

    /// <summary>The door the recording's knocks are on: a solid wooden door, 0.9 x 2.0 m and 40 mm, in its frame.</summary>
    internal static StruckThing KnockDoor => new()
    {
        Material = "Wood", Shape = StruckShape.Plate, Length = 2.0f, Width = 0.9f, Thickness = 0.04f, Support = StruckSupport.Built,
    };

    private static int Fit(string[] args)
    {
        string rec = Arg(args, "recording=") ?? Path.Combine(LabPaths.Checkout, "inbox", "door sounds", "Door Knocking SOUND EFFECT - Heavy Door Knocks.mp3");
        var pcm = Decode(rec);
        var onsets = Onsets(pcm, Rate);
        Console.WriteLine($"{Path.GetFileName(rec)}: {pcm.Length / (float)Rate:F1} s, {onsets.Count} knocks");
        var real = MeanBands(pcm, onsets, 0.12f);
        Console.WriteLine("  recording, first 120 ms, dB of the total: " + Bands(real));
        // The model, over a grid of the knuckle's three unknowns: the moving mass, the skin's stiffness, and how
        // thick the skin is over the bone. The door is an assumption (a solid wooden door), so the fit is judged
        // from 125 Hz up: the door's own fundamental and the recording's room decide the bottom two bands.
        var grid = new List<(float Kg, float Gpa, float PadMm, float Err)>();
        foreach (float kg in new[] { 0.06f, 0.1f, 0.15f })
            foreach (float gpa in new[] { 0.01f, 0.02f, 0.03f, 0.05f })
                foreach (float pad in new[] { 0f, 1f, 1.5f, 2f, 3f })
                {
                    var k = Striker.Knuckle with { MassKg = kg, ModulusGPa = gpa, PadM = pad / 1000f, CoreModulusGPa = pad > 0 ? Striker.BoneModulusGPa : 0f };
                    var p = StruckThings.RenderPascals(new Strike(KnockDoor, new[] { new Blow(k, 1.5f, 0.55f, 0.4f) }), Rate, out _);
                    var model = Shape(Octaves(p, 0, Rate * 12 / 100));
                    float err = Rms(model, real, from: 2);
                    grid.Add((kg, gpa, pad, err));
                    Console.WriteLine($"  {kg,5:F2} kg {gpa * 1000,4:F0} MPa pad {pad,3:F1} mm: rms {err,5:F1} dB  contact {StruckThings.LastContactSeconds * 1000:F2} ms  " + Bands(model));
                }
        var best = grid.OrderBy(g => g.Err).First();
        Console.WriteLine($"Best: {best.Kg} kg, {best.Gpa * 1000:F0} MPa, pad {best.PadMm} mm, rms {best.Err:F1} dB over 125 Hz-16 kHz "
                        + $"(Striker.Knuckle: {Striker.Knuckle.MassKg} kg, {Striker.Knuckle.ModulusGPa * 1000:F0} MPa, pad {Striker.Knuckle.PadM * 1000:F1} mm)");
        var now = StruckThings.RenderPascals(new Strike(KnockDoor, new[] { new Blow(Striker.Knuckle, 1.5f, 0.55f, 0.4f) }), Rate, out _);
        var shape = Shape(Octaves(now, 0, Rate * 12 / 100));
        Console.WriteLine($"Striker.Knuckle as it stands: rms {Rms(shape, real, from: 2):F1} dB  " + Bands(shape));
        Console.WriteLine("  band by band, model minus recording (after the mean): "
                        + string.Join(" ", shape.Select((v, i) => $"{Spectrum.BandEdges[i]:F0}:{v - real[i] - (shape.Skip(2).Sum() - real.Skip(2).Sum()) / (shape.Length - 2),5:F1}")));
        return 0;
    }

    private static float Rms(float[] a, float[] b, int from)
    {
        // Shapes compared after removing their mean difference: the recording's level is unknown.
        double mean = 0; int n = 0;
        for (int i = from; i < a.Length; i++) { mean += a[i] - b[i]; n++; }
        mean /= n;
        double s = 0;
        for (int i = from; i < a.Length; i++) { double d = a[i] - b[i] - mean; s += d * d; }
        return (float)Math.Sqrt(s / n);
    }

    private static string Bands(float[] db) => string.Join(" ", db.Select((v, i) => $"{Spectrum.BandEdges[i]:F0}:{v,5:F1}"));

    /// <summary>The mean band shape of each event's first <paramref name="seconds"/>, energy-averaged.</summary>
    internal static float[] MeanBands(float[] pcm, List<int> onsets, float seconds)
    {
        var sum = new double[Spectrum.BandCount];
        foreach (int o in onsets)
        {
            int len = Math.Min((int)(seconds * Rate), pcm.Length - o);
            if (len < Rate / 100) continue;
            var e = Octaves(pcm, o, len);
            double total = e.Sum(x => (double)x);
            for (int i = 0; i < e.Length; i++) sum[i] += e[i] / Math.Max(total, 1e-30);
        }
        double all = sum.Sum();
        return sum.Select(v => (float)(10 * Math.Log10(Math.Max(v / all, 1e-12)))).ToArray();
    }

    /// <summary>Onsets: where a 1 ms envelope first rises 25 dB over the 50 ms before it, at least 150 ms apart,
    /// started 1 ms early.</summary>
    internal static List<int> Onsets(float[] pcm, int rate)
    {
        var env = new float[pcm.Length];
        float a = 1f - MathF.Exp(-1f / (0.001f * rate)), e = 0f;
        for (int i = 0; i < pcm.Length; i++) { e += a * (MathF.Abs(pcm[i]) - e); env[i] = e; }
        float peak = env.Max();
        var onsets = new List<int>();
        int gap = rate * 15 / 100, look = rate / 20;
        for (int i = look; i < pcm.Length; i++)
        {
            if (onsets.Count > 0 && i - onsets[^1] < gap) continue;
            if (env[i] < peak * 0.05f) continue;
            float before = 1e-9f;
            for (int j = i - look; j < i - look / 2; j++) before = MathF.Max(before, env[j]);
            if (env[i] > before * 17.8f) onsets.Add(Math.Max(0, i - rate / 1000));
        }
        return onsets;
    }

    internal static float[] Decode(string path)
    {
        var psi = new ProcessStartInfo("ffmpeg", $"-v quiet -i \"{path}\" -ac 1 -ar {Rate} -f f32le -")
        { RedirectStandardOutput = true, UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit();
        var b = ms.ToArray();
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
        return f;
    }

    // ── The listening set ────────────────────────────────────────────────────────────────────────

    /// <summary>One file of the set: its name, what it is, and the strike.</summary>
    internal sealed record Item(string File, string What, Strike Strike);

    internal static List<Item> Set()
    {
        var items = new List<Item>();
        int n = 1;
        string N() => (n++).ToString("00", Ci);
        Blow Finger(float u = 0.5f, float v = 0.5f) => new(Striker.Fingertip, 0.8f, u, v);
        Blow Knuckle(float u = 0.5f, float v = 0.5f) => new(Striker.Knuckle, 1.5f, u, v);
        // Cubes of eight materials at three sizes, resting on the ground, a fingertip and a knuckle on the top face
        // a little off its middle.
        foreach (var mat in new[] { "Aluminium", "Metal", "Lead", "Glass", "Oak", "Concrete", "Brick", "Rubber" })
            foreach (float size in new[] { 0.05f, 0.2f, 1f })
            {
                var cube = new StruckThing { Material = mat, Shape = StruckShape.Block, Length = size, Width = size, Thickness = size, Support = StruckSupport.Resting };
                string name = mat == "Metal" ? "steel" : mat.ToLowerInvariant();
                string cm = (size * 100).ToString("0", Ci);
                items.Add(new($"{N()}-{name}-cube-{cm}cm-finger.wav", $"a {cm} cm {name} cube on the ground, tapped with a fingertip",
                              new Strike(cube, new[] { Finger(0.35f, 0.4f) })));
                items.Add(new($"{N()}-{name}-cube-{cm}cm-knuckle.wav", $"the same, knocked with a knuckle",
                              new Strike(cube, new[] { Knuckle(0.35f, 0.4f) })));
            }
        // An aluminium sheet hung by a corner: knuckle, then a palm.
        var sheet = new StruckThing { Material = "Aluminium", Shape = StruckShape.FreePlate, Length = 1f, Width = 0.5f, Thickness = 0.001f, Support = StruckSupport.Hung };
        items.Add(new($"{N()}-aluminium-sheet-1x0.5m-1mm-knuckle.wav", "a 1 x 0.5 m aluminium sheet, 1 mm, hung on a string, knocked with a knuckle",
                      new Strike(sheet, new[] { Knuckle(0.3f, 0.35f) })));
        items.Add(new($"{N()}-aluminium-sheet-1x0.5m-1mm-palm.wav", "the same sheet slapped with a palm",
                      new Strike(sheet, new[] { new Blow(Striker.Palm, 2f, 0.3f, 0.35f) })));
        // An aluminium bar, for the bar's own pattern of notes, and a steel rod on it.
        var bar = new StruckThing { Material = "Aluminium", Shape = StruckShape.Bar, Length = 0.4f, Width = 0.04f, Thickness = 0.01f, Support = StruckSupport.Hung };
        items.Add(new($"{N()}-aluminium-bar-400mm-rod.wav", "a 400 x 40 x 10 mm aluminium bar hung at its nodes, struck in the middle with a steel rod",
                      new Strike(bar, new[] { new Blow(Striker.Rod, 1f, 0.5f, 0.5f) })));
        // Bumps: a person walking into each, with the old bump for comparison.
        foreach (var (file, what, thing, up) in BumpTargets())
            items.Add(new($"{N()}-bump-{file}.wav", $"walking into {what} (1.2 m/s: a palm, the toe of a shoe, the shoulder)",
                          new Strike(thing, StruckThings.BodyBump(1.2f, 0.45f, 0.62f, up, 0f))));
        foreach (var (file, what, thing, up) in BumpTargets())
            items.Add(new($"{N()}-run-into-{file}.wav", $"running into {what} (2 m/s)",
                          new Strike(thing, StruckThings.BodyBump(2f, 0.45f, 0.62f, up, 0.5f), 1)));
        return items;
    }

    /// <summary>The same five boxes as the old bump took them: material, collider size (met on its x face), moving.</summary>
    internal static List<(string File, string What, string Material, System.Numerics.Vector3 Size, bool Moves)> OldBumpTargets() => new()
    {
        ("plaster-stud-wall", "a plasterboard stud wall", "Plaster", new(0.1f, 2.6f, 3f), false),
        ("concrete-wall", "a 200 mm concrete wall", "Concrete", new(0.2f, 3f, 4f), false),
        ("glass-door", "a glass door", "Glass", new(0.01f, 2.1f, 0.9f), true),
        ("steel-fence", "a steel palisade fence", "Fence", new(0.05f, 1.8f, 2.4f), false),
        ("car", "the side of a parked car", "Metal", new(1.8f, 1.4f, 4.5f), true),
    };

    /// <summary>What the bumps meet: as the game describes each from the map's own boxes (StruckThings.Describe).</summary>
    internal static List<(string File, string What, StruckThing Thing, bool LengthIsUp)> BumpTargets()
    {
        var studWall = StruckThings.Describe("Plaster", new System.Numerics.Vector3(0.1f, 2.6f, 3f), System.Numerics.Vector3.UnitX,
                                             0.0125f, 0.6f, moves: false, isVehicle: false, out bool wallUp);
        var concrete = StruckThings.Describe("Concrete", new System.Numerics.Vector3(0.2f, 3f, 4f), System.Numerics.Vector3.UnitX,
                                             0f, 0f, moves: false, isVehicle: false, out bool concreteUp);
        var glassDoor = StruckThings.Describe("Glass", new System.Numerics.Vector3(0.01f, 2.1f, 0.9f), System.Numerics.Vector3.UnitX,
                                              0f, 0f, moves: true, isVehicle: false, out bool doorUp);
        var fence = StruckThings.Describe("Fence", new System.Numerics.Vector3(0.05f, 1.8f, 2.4f), System.Numerics.Vector3.UnitX,
                                          0f, 0f, moves: false, isVehicle: false, out bool fenceUp);
        var car = StruckThings.Describe("Metal", new System.Numerics.Vector3(1.8f, 1.4f, 4.5f), System.Numerics.Vector3.UnitX,
                                        0f, 0f, moves: true, isVehicle: true, out bool carUp);
        return new()
        {
            ("plaster-stud-wall", "a plasterboard stud wall (12.5 mm boards on studs 600 mm apart)", studWall, wallUp),
            ("concrete-wall", "a 200 mm concrete wall", concrete, concreteUp),
            ("glass-door", "a glass door shut in its frame (10 mm glass, 2 mm of play at the latch)", glassDoor, doorUp),
            ("steel-fence", "a steel palisade fence (a pale between rails, loose on its bolt)", fence, fenceUp),
            ("car", "the side of a parked car", car, carUp),
        };
    }

    private static int Renders(string[] args)
    {
        string dir = Arg(args, "out=") ?? Path.Combine(LabPaths.Checkout, "inbox", "struck-things-2026-10-10");
        Directory.CreateDirectory(dir);
        var items = Set();
        var lines = new List<string>
        {
            "file\twhat\tdeclared peak dB SPL at 1 m\tgame gain\tpeak dBFS\tRMS (loudest 100 ms) dBFS\tlength s\tclipped\tlongest exact-zero run ms\tbands of the first 120 ms (dB of the total, 30 Hz-16 kHz)\trender ms",
        };
        float anchor = StruckThings.LevelAnchorDb;
        Console.WriteLine($"Level anchor {anchor:+0.0;-0.0} dB (heel on a slab {StruckThings.ModelFootstepDb:F1} dB against the bank's "
                        + $"{Loudness.FootstepDb + StruckThings.FootstepTakeLoudestA20msDbfs:F1})");
        foreach (var it in items)
        {
            var sw = Stopwatch.StartNew();
            var pcm = StruckThings.Render(it.Strike, Rate, out float db);
            long ms = sw.ElapsedMilliseconds;
            var played = GameLevel(pcm, db);
            Measure(played, out float peakDbfs, out float rmsDbfs, out int clipped, out float zeroMs);
            var bands = Shape(Octaves(pcm, 0, Rate * 12 / 100));
            WriteFloatWav(Path.Combine(dir, it.File), played);
            lines.Add(string.Join("\t", it.File, it.What, db.ToString("F1", Ci), Loudness.Place(db).Gain.ToString("F4", Ci),
                                  peakDbfs.ToString("F1", Ci), rmsDbfs.ToString("F1", Ci), (played.Length / (float)Rate).ToString("F2", Ci),
                                  clipped.ToString(Ci), zeroMs.ToString("F1", Ci), Bands(bands), ms.ToString(Ci)));
            Console.WriteLine($"{it.File,-46} {db,6:F1} dB  peak {peakDbfs,6:F1} dBFS  rms {rmsDbfs,6:F1}  {played.Length / (float)Rate,5:F2} s  {ms,5} ms  {Bands(bands)}");
        }
        // The old bump, as the game played it before (ImpactAcoustics through TransientSynth), for comparison.
        foreach (var (file, what, material, size, moves) in OldBumpTargets())
        {
            var old = OldBump(material, size, moves, out float db);
            var played = GameLevel(old, db);
            Measure(played, out float peakDbfs, out float rmsDbfs, out int clipped, out float zeroMs);
            string name = $"old-bump-{file}.wav";
            WriteFloatWav(Path.Combine(dir, name), played);
            var bands = Shape(Octaves(old, 0, Rate * 12 / 100));
            lines.Add(string.Join("\t", name, $"the old bump into {what}", db.ToString("F1", Ci), Loudness.Place(db).Gain.ToString("F4", Ci),
                                  peakDbfs.ToString("F1", Ci), rmsDbfs.ToString("F1", Ci), (played.Length / (float)Rate).ToString("F2", Ci),
                                  clipped.ToString(Ci), zeroMs.ToString("F1", Ci), Bands(bands), "0"));
            Console.WriteLine($"{name,-46} {db,6:F1} dB  peak {peakDbfs,6:F1} dBFS  rms {rmsDbfs,6:F1}  {Bands(bands)}");
        }
        File.WriteAllLines(Path.Combine(dir, "levels.tsv"), lines);
        Console.WriteLine($"wrote {items.Count + 5} files and levels.tsv to {dir}");
        return 0;
    }

    /// <summary>
    /// The old bump (WallBumps.Sound before 2026-10-10): ImpactAcoustics.Between, a body (Skin) of 4 kg at the
    /// walking pace against the face's own material, size and thickness, each sound through TransientSynth at its
    /// own declared level, summed at their delays. The level returned is the loudest of its sounds.
    /// </summary>
    private static float[] OldBump(string material, System.Numerics.Vector3 size, bool moves, out float db)
    {
        // As WallBumps.Sound had it: the face met is the collider's (its x the thickness here), the mass its whole box.
        var body = AcousticRegistry.GetProperties("Skin");
        var props = AcousticRegistry.GetProperties(material);
        float mass = Math.Clamp(props.DensityKgM3 * size.X * size.Y * size.Z, 1f, 1e6f);
        var sounds = ImpactAcoustics.Between(body, props, System.Numerics.Vector3.Zero, 1.2f, 4f, mass, size.Z, size.Y, size.X, struckIsFixed: !moves);
        db = sounds.Max(s => s.LevelDb);
        int len = (int)(Rate * 1.5f);
        var sum = new float[len];
        foreach (var s in sounds)
        {
            var pcm = OpenFPS.Client.AudioEngine.Core.TransientSynth.Render(s, 1);
            float g = MathF.Pow(10f, (s.LevelDb - db) / 20f);
            int off = (int)(s.DelaySeconds * Rate);
            for (int i = 0; i < pcm.Length && off + i < len; i++) sum[off + i] += g * pcm[i];
        }
        float peak = sum.Max(MathF.Abs);
        if (peak > 0) for (int i = 0; i < len; i++) sum[i] /= peak;
        return sum;
    }

    /// <summary>A buffer of peak one declared at <paramref name="db"/>, as the game plays it within its reference
    /// distance: the loudness law's gain at the shipped /levels (Loudness.Place). No room, no ear-model tone.</summary>
    internal static float[] GameLevel(float[] pcm, float db)
    {
        float g = Loudness.Place(db).Gain;
        var y = new float[pcm.Length];
        for (int i = 0; i < y.Length; i++) y[i] = pcm[i] * g;
        return y;
    }

    internal static void Measure(float[] x, out float peakDbfs, out float rmsDbfs, out int clipped, out float longestZeroMs)
    {
        float peak = 0f; clipped = 0;
        int run = 0, longest = 0;
        foreach (float v in x)
        {
            peak = MathF.Max(peak, MathF.Abs(v));
            if (MathF.Abs(v) >= 0.999f) clipped++;
            if (v == 0f) { run++; longest = Math.Max(longest, run); } else run = 0;
        }
        int w = Rate / 10; double acc = 0, best = 0;
        for (int i = 0; i < x.Length; i++) { acc += x[i] * x[i]; if (i >= w) acc -= x[i - w] * x[i - w]; best = Math.Max(best, acc / w); }
        peakDbfs = 20f * MathF.Log10(MathF.Max(peak, 1e-9f));
        rmsDbfs = (float)(10 * Math.Log10(Math.Max(best, 1e-18)));
        longestZeroMs = longest * 1000f / Rate;
    }

    // ── Cost ───────────────────────────────────────────────────────────────────────────────────────

    private static int Cost()
    {
        foreach (var it in Set().Where((_, i) => i % 4 == 0))
        {
            var sw = Stopwatch.StartNew();
            var p = StruckThings.RenderPascals(it.Strike, Rate, out _);
            Console.WriteLine($"{it.File,-46} {sw.ElapsedMilliseconds,5} ms for {p.Length / (float)Rate:F2} s");
        }
        return 0;
    }

    /// <summary>
    /// Energy in Spectrum's nine bands of <paramref name="len"/> samples from <paramref name="start"/>, by one FFT
    /// of the segment unwindowed and zero-padded to twice its length. Not Spectrum.BandEnergy: its Hann window
    /// runs from the first sample, so a strike at the start of the segment, whose top dies in 20 ms, lost 20 to
    /// 30 dB of its top end to the window's rising edge (measured here 2026-10-10: a rod on glass read 34 dB
    /// dull). A decaying transient is its own taper.
    /// </summary>
    internal static float[] Octaves(float[] x, int start, int len)
    {
        len = Math.Max(0, Math.Min(len, x.Length - start));
        int n = 1; while (n < 2 * Math.Max(len, 1024)) n <<= 1;
        var buf = new System.Numerics.Complex[n];
        for (int i = 0; i < len; i++) buf[i] = new System.Numerics.Complex(x[start + i], 0);
        Spectrum.Fft(buf);
        var e = new float[Spectrum.BandCount];
        for (int b = 0; b < e.Length; b++)
        {
            int k0 = (int)(Spectrum.BandEdges[b] * n / Rate), k1 = Math.Min(n / 2, (int)(Spectrum.BandEdges[b + 1] * n / Rate));
            double acc = 0;
            for (int k = k0; k < k1; k++) acc += buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
            e[b] = (float)(2 * acc / n);   // Parseval: the segment's energy, in (unit)^2 samples
        }
        return e;
    }

    /// <summary>Band energies as dB of their total.</summary>
    internal static float[] Shape(float[] energy)
    {
        double total = Math.Max(1e-30, energy.Sum(v => (double)v));
        return energy.Select(v => (float)(10 * Math.Log10(Math.Max(v / total, 1e-12)))).ToArray();
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────────────────────

    private static string? Arg(string[] args, string prefix) => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static float Num(string[] args, string prefix, float fallback)
        => float.TryParse(Arg(args, prefix), NumberStyles.Float, Ci, out float v) ? v : fallback;

    internal static void WriteFloatWav(string path, float[] x)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = x.Length * 4;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)3); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)32); w.Write("data"u8); w.Write(bytes);
        foreach (float v in x) w.Write(v);
    }
}
