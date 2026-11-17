using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Geometry;
using OpenFPS.Materials;
using OpenFPS.Physics;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;
using OpenFPS.Sound.Hearing;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --drops [out=DIR] [cost]
///
/// Dropped and thrown things as real bodies (docs/PHYSICS_BOUNDARY.md 18.2 step 7, 18.11): three items of
/// different mass and material — a glass bottle, a wooden block, a steel crowbar — let go over four floors
/// (wood, concrete, carpet, metal), and two of them thrown, all stepped in a <see cref="BodyWorld"/> and every
/// impact heard through the same contact and the same <see cref="ImpactAcoustics.Between"/> the server uses. Each
/// drop is rendered to a WAV beside the old scripted drop of the same item, floor and height, at game level (the
/// shipped hearing's placement), and every file's peak and RMS is printed. <c>cost</c> (or always, after) also
/// prices the server's warm-up and a tick with 1, 10 and 50 moving items on the city's own tiles.
/// </summary>
public static class DropsSpike
{
    private const int Rate = TransientSynth.SampleRate;

    private static readonly (string Material, float X)[] Floors =
    {
        ("Wood", -9f), ("Concrete", -3f), ("Carpet", 3f), ("Metal", 9f),
    };

    private static readonly (string Name, float MassKg, string Material, Vector3 Size)[] Items =
    {
        ("glass bottle", 0.5f, "Glass", new Vector3(0.08f, 0.28f, 0.08f)),
        ("wooden block", 2f, "Wood", new Vector3(0.2f, 0.2f, 0.2f)),
        ("steel crowbar", 5f, "Metal", new Vector3(0.03f, 0.03f, 0.9f)),
    };

    /// <summary>Where the hand is: the item's middle, a metre and a third above the floor.</summary>
    private const float HandMetres = 1.3f;

    private static readonly Vector3 FloorPanel = new(DroppedThings.FloorPanelWidthMetres,
                                                     DroppedThings.FloorPanelHeightMetres,
                                                     DroppedThings.FloorPanelThicknessMetres);

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string outDir = Arg(args, "out=") ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".cache", "openfps-build", "drops", "renders");
        Directory.CreateDirectory(outDir);
        var hearing = HearingSettings.FromEnvironment();
        Console.WriteLine($"Drops: game level, compression {hearing.Compression:F2}; writing to {outDir}\n");

        var files = Render(outDir, hearing);
        Cost(args);
        return files;
    }

    // ═══ The render ═════════════════════════════════════════════════════════════════════════════

    private static int Render(string outDir, HearingSettings hearing)
    {
        Console.WriteLine("  file                                      secs   peak   rms  clip  nan  impact(s) at (s:knock dB/peak dBFS)");
        int bad = 0, n = 0;

        // One item at a time on each floor, so nothing meets another item and each fall is heard alone.
        foreach (var (material, x) in Floors)
        {
            foreach (var item in Items)
            {
                n++;
                var start = new Vector3(x, HandMetres, 0f);
                var flight = Fly(item, start, Vector3.Zero, material);
                string stem = $"{n:00}-new-{Slug(item.Name)}-on-{Slug(material)}";
                var pcm = Mix(flight.Impacts, hearing, out var times);
                bad += Write(outDir, stem + ".wav", pcm, times);
                if (item.Name.Contains("bottle"))
                {
                    var knocks = flight.Impacts.Select(i => i.Sounds.Where(x => x.Character == SoundCharacter.Knock)
                                                                    .Select(x => (float?)x.LevelDb).FirstOrDefault())
                                               .Where(v => v is not null).Select(v => v!.Value).ToList();
                    if (knocks.Count >= 2 && knocks[1] >= knocks[0])
                    {
                        Console.WriteLine($"  FAIL: {stem}: the second impact is {knocks[1]:F1} dB against the first's {knocks[0]:F1}");
                        bad++;
                    }
                }

                var old = OldScript(item, material, start);
                string oldStem = $"{n:00}-old-{Slug(item.Name)}-on-{Slug(material)}";
                var oldPcm = Mix(old, hearing, out var oldTimes);
                bad += Write(outDir, oldStem + ".wav", oldPcm, oldTimes);

                // The same floor and item, thrown: the bottle at concrete and the crowbar at wood.
                if ((item.Name.Contains("bottle") && material == "Concrete") || (item.Name.Contains("crowbar") && material == "Wood"))
                {
                    n++;
                    var from = new Vector3(x, HandMetres, -2f);
                    var forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f));
                    var thrown = Fly(item, from, forward * DroppedThings.ThrowSpeed(item.MassKg), material);
                    string thrownStem = $"{n:00}-new-{Slug(item.Name)}-thrown-on-{Slug(material)}";
                    var thrownPcm = Mix(thrown.Impacts, hearing, out var thrownTimes);
                    bad += Write(outDir, thrownStem + ".wav", thrownPcm, thrownTimes);

                    n++;
                    var oldThrown = OldScript(item, material, from);
                    string oldThrownStem = $"{n:00}-old-{Slug(item.Name)}-from-the-hand-on-{Slug(material)}";
                    var oldThrownPcm = Mix(oldThrown, hearing, out var oldThrownTimes);
                    bad += Write(outDir, oldThrownStem + ".wav", oldThrownPcm, oldThrownTimes);
                }
            }
        }
        Console.WriteLine(bad == 0
            ? "\nEvery file is below full scale and holds only finite samples."
            : $"\n{bad} file(s) clip or hold a non-finite sample.");
        return bad == 0 ? 0 : 1;
    }

    private sealed record ImpactAt(float Time, List<TransientSound> Sounds);
    private sealed record Flight(List<ImpactAt> Impacts, int Ticks, float Seconds);

    /// <summary>
    /// One body falling (or thrown) in a small scene: four static floors, a body of the item's declared size and
    /// mass, and its contacts read as the server reads them. The floor is struck as the game's tile panel: the
    /// server's floors are mesh tiles and carry no panel size of their own.
    /// </summary>
    private static Flight Fly((string Name, float MassKg, string Material, Vector3 Size) item, Vector3 start, Vector3 velocity,
                              string floorMaterial)
    {
        using var world = new BodyWorld();
        foreach (var (material, x) in Floors)
        {
            int shape = world.AddBox(new Vector3(3f, 0.1f, 3f));
            world.Add(shape, BodyMotion.Static, new Vector3(x, -0.1f, 0f), Quaternion.Identity, material);
        }
        var contacts = new ContactStream();
        world.ReportContacts = true;
        int bodyShape = world.AddBox(item.Size * 0.5f);
        int body = world.Add(bodyShape, BodyMotion.Dynamic, start, Quaternion.Identity, item.Material, item.MassKg);
        world.SetVelocity(body, velocity);

        var impacts = new List<ImpactAt>();
        int ticks = 0;
        for (; ticks < 400; ticks++)
        {
            contacts.Begin();
            world.Tick(contacts);
            contacts.Close();
            float time = ticks * BodyWorld.StepSeconds * BodyWorld.StepsPerTick;
            foreach (var contact in contacts.Events)
            {
                if (contact.Phase != ContactPhase.Began || contact.Body != BodyWorld.ContactId(body)) continue;
                var sounds = DroppedThings.Impact(contact, item.MassKg, DroppedThings.FixedMassKg, FloorPanel, struckFixed: true);
                if (sounds.Count > 0) impacts.Add(new ImpactAt(time, sounds));
            }
            if (world.State(body).Sleeping) break;
        }
        world.Remove(body);
        world.RemoveShape(bodyShape);
        return new Flight(impacts, ticks, ticks * BodyWorld.StepSeconds * BodyWorld.StepsPerTick);
    }

    /// <summary>The old scripted drop exactly: one impact at once, at sqrt(2gh), mass m into 500 m of floor, a 2 m square 0.2 m thick.</summary>
    private static List<ImpactAt> OldScript((string Name, float MassKg, string Material, Vector3 Size) item, string floorMaterial, Vector3 from)
    {
        float fall = MathF.Max(0.05f, from.Y);
        float speed = MathF.Sqrt(2f * Earth.StandardGravity * fall);
        var at = new Vector3(from.X, 0f, from.Z);
        var sounds = ImpactAcoustics.Between(AcousticRegistry.GetProperties(item.Material),
                                             AcousticRegistry.GetProperties(floorMaterial), at, speed,
                                             item.MassKg, item.MassKg * 500f, 2f, 2f, 0.2f);
        return sounds.Count == 0 ? new List<ImpactAt>() : new List<ImpactAt> { new(0f, sounds) };
    }

    /// <summary>Every impact's transients at their contact times, each at the level the shipped hearing places it.
    /// The times also carry the impact's knock level at a metre and the peak of its own transients, dBFS.</summary>
    private static float[] Mix(List<ImpactAt> impacts, HearingSettings hearing, out List<(float Time, float KnockDb, float PeakDb)> times)
    {
        float end = impacts.Count == 0 ? 0.5f : impacts.Max(i => i.Time) + 3f;
        var mix = new float[(int)(end * Rate)];
        var peaks = new float[impacts.Count];
        int seed = 1;
        for (int k = 0; k < impacts.Count; k++)
        {
            var impact = impacts[k];
            int offset = (int)(impact.Time * Rate);
            foreach (var sound in impact.Sounds)
            {
                var pcm = TransientSynth.Render(sound, seed++);
                float gain = hearing.Place(sound.LevelDb).Gain;
                int at = offset + (int)(sound.DelaySeconds * Rate);
                for (int i = 0; i < pcm.Length && at + i < mix.Length; i++)
                {
                    mix[at + i] += pcm[i] * gain;
                    peaks[k] = MathF.Max(peaks[k], MathF.Abs(pcm[i] * gain));
                }
            }
        }
        times = impacts.Select((i, k) => (i.Time,
            i.Sounds.Where(x => x.Character == SoundCharacter.Knock).Select(x => x.LevelDb)
                    .DefaultIfEmpty(i.Sounds.Count > 0 ? i.Sounds.Max(x => x.LevelDb) : 0f).First(),
            20f * MathF.Log10(MathF.Max(1e-9f, peaks[k])))).ToList();
        return mix;
    }

    /// <summary>Writes one file, measures it, and prints its line. True when it clips or holds a non-finite sample.</summary>
    private static int Write(string outDir, string name, float[] pcm, List<(float Time, float KnockDb, float PeakDb)> times)
    {
        float peak = 0f;
        double sum = 0;
        int clip = 0, nan = 0;
        foreach (float v in pcm)
        {
            if (!float.IsFinite(v)) { nan++; continue; }
            float a = MathF.Abs(v);
            if (a > peak) peak = a;
            if (a >= 0.999f) clip++;
            sum += (double)v * v;
        }
        float peakDb = 20f * MathF.Log10(MathF.Max(1e-9f, peak));
        float rmsDb = 10f * MathF.Log10(MathF.Max(1e-12f, (float)(sum / Math.Max(1, pcm.Length))));
        File.WriteAllBytes(Path.Combine(outDir, name), WeaponSynth.ToWav16(pcm, Rate));
        string at = string.Join(", ", times.Select(t => $"{t.Time.ToString("0.000", CultureInfo.InvariantCulture)}:{t.KnockDb.ToString("0.0", CultureInfo.InvariantCulture)}/{t.PeakDb.ToString("0.0", CultureInfo.InvariantCulture)}"));
        Console.WriteLine($"  {name,-42} {pcm.Length / (float)Rate,4:F2} {peakDb,6:F1} {rmsDb,6:F1} {clip,4} {nan,3}  {at}");
        return clip > 0 || nan > 0 ? 1 : 0;
    }

    private static string Slug(string s) => s.ToLowerInvariant().Replace(' ', '-');

    // ═══ What the server's bodies cost ══════════════════════════════════════════════════════════

    private static void Cost(string[] args)
    {
        Console.WriteLine("\nBodies: tick cost with moving items (four 120 Hz steps a tick)");
        using (var world = new BodyWorld())
        {
            world.Add(world.AddBox(new Vector3(60f, 0.5f, 60f)), BodyMotion.Static, new Vector3(0f, -0.5f, 0f),
                      Quaternion.Identity, "Concrete");
            int shape = world.AddBox(new Vector3(0.15f, 0.15f, 0.15f));
            foreach (int n in new[] { 1, 10, 50 })
            {
                var ids = new List<int>();
                for (int i = 0; i < n; i++)
                    ids.Add(world.Add(shape, BodyMotion.Dynamic, new Vector3((i % 10) * 0.5f - 2f, 60f + i / 10, 0.25f),
                                      Quaternion.Identity, "Wood", 2f));
                var times = new List<double>();
                for (int i = 0; i < 100; i++) times.Add(Time(world));
                Console.WriteLine($"  {n,2} moving item(s), four floors, no map tiles: {Stat(times)}");
                foreach (int id in ids) world.Remove(id);
            }
        }

        if (args.Contains("no-city")) return;
        CityCost();
    }

    /// <summary>The city's own tiles through PieceBodies: the warm-up's adds a tick, then ticks with 0, 1, 10 and 50 moving items.</summary>
    private static void CityCost()
    {
        var clock = Stopwatch.StartNew();
        string dir = Path.Combine(Path.GetTempPath(), "openfps-drops-city-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "maps"));
        File.Copy(LabPaths.Server("maps", "city.json"), Path.Combine(dir, "maps", "city.json"));
        var maps = new MapManager(new MapRepository(Path.Combine(dir, "maps")),
                                  new PrefabRepository(LabPaths.Server("prefabs")));
        maps.Initialize();
        Directory.Delete(dir, true);
        if (!maps.TryGetMap("city", out var mapWorld, out _, out _, out _) || !maps.TryGetGeometry("city", out var geometry))
        { Console.WriteLine("  city: no triangle world"); return; }
        double loadMs = clock.Elapsed.TotalMilliseconds;

        using var world = new BodyWorld(PhysicsConstants.GravityOf(mapWorld));
        using var pieces = new PieceBodies(world);
        var ready = new Dictionary<(TileKey, ulong), PreparedPiece>();
        var leaves = new Dictionary<ulong, PreparedPiece>();
        var made = Stopwatch.StartNew();
        int prepared = 0, preparedLeaves = 0;
        foreach (ref readonly var inst in geometry.World.Instances)
        {
            if (inst.Piece.IsEmpty) continue;
            if (inst.Owner >= 0)
            {
                if (leaves.ContainsKey(inst.Piece.Signature)) continue;
                leaves[inst.Piece.Signature] = PieceBodies.Prepare(inst.Piece);
                preparedLeaves++;
                continue;
            }
            var key = (inst.Piece.Key, inst.Piece.Signature);
            if (ready.ContainsKey(key)) continue;
            ready[key] = PieceBodies.Prepare(inst.Piece);
            prepared++;
        }
        made.Stop();
        PreparedPiece? Take(GeometryPiece piece)
        {
            if (leaves.Remove(piece.Signature, out var leaf)) return leaf;
            var key = (piece.Key, piece.Signature);
            if (!ready.Remove(key, out var p)) return null;
            return p;
        }
        pieces.Follow(geometry.World, Take);
        int tiles = pieces.TileCount;
        var warm = new List<double>();
        while (pieces.PendingBodies > 0)
        {
            var t = Stopwatch.StartNew();
            pieces.Follow(geometry.World, Take);
            pieces.Pump(4);
            world.Tick();
            t.Stop();
            warm.Add(t.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"  city: map {loadMs:F0} ms, {prepared} tile pieces and {preparedLeaves} leaf builds made in {made.Elapsed.TotalMilliseconds:F0} ms, "
                        + $"{tiles} tiles, {pieces.LeafCount} door leaves, {pieces.PendingBodies} queued; warm-up {warm.Count} ticks, "
                        + $"worst {warm.Max():F2} ms, mean {warm.Average():F2} ms");
        var steady = new List<double>();
        for (int i = 0; i < 100; i++)
        {
            var t = Stopwatch.StartNew();
            pieces.Follow(geometry.World, Take);
            pieces.Pump(4);
            world.Tick();
            t.Stop();
            steady.Add(t.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"  city, no moving bodies ({world.BodyCount} static/kinematic bodies): {Stat(steady)}");

        int box = world.AddBox(new Vector3(0.15f, 0.15f, 0.15f));
        foreach (int n in new[] { 1, 10, 50 })
        {
            var ids = new List<int>();
            for (int i = 0; i < n; i++)
                ids.Add(world.Add(box, BodyMotion.Dynamic, new Vector3(i * 0.5f, 60f + i, 0f), Quaternion.Identity, "Wood", 2f));
            var times = new List<double>();
            for (int i = 0; i < 100; i++) times.Add(Time(world));
            Console.WriteLine($"  city, {n,2} moving item(s): {Stat(times)}");
            foreach (int id in ids) world.Remove(id);
        }
        world.RemoveShape(box);

        // A drop's first contact on the city's ground: the tick it begins in, against the ticks beside it.
        var any = new AcceptAll();
        var spawn = maps.GetSpawnPoint("city").Position;
        float floor = geometry.World.FloorAt(spawn.X, spawn.Z, spawn.Y + 2f, GeometryLayers.Ground, ref any, out var hit);
        if (floor < -900f) { Console.WriteLine("  city: no floor at the spawn point for a first-contact check"); return; }
        int ballShape = world.AddSphere(0.2f);
        int ball = world.Add(ballShape, BodyMotion.Dynamic, new Vector3(spawn.X, floor + 1.3f, spawn.Z), Quaternion.Identity, "Metal", 0.5f);
        var contacts = new ContactStream();
        world.ReportContacts = true;
        var drop = new List<double>();
        int firstContactTick = -1;
        for (int i = 0; i < 120; i++)
        {
            contacts.Begin();
            var t = Stopwatch.StartNew();
            world.Tick(contacts);
            t.Stop();
            drop.Add(t.Elapsed.TotalMilliseconds);
            contacts.Close();
            if (firstContactTick < 0 && contacts.Events.Any(e => e.Phase == ContactPhase.Began && BodyWorld.IsContactId(e.Body)))
                firstContactTick = i;
            if (world.State(ball).Sleeping) break;
        }
        Console.WriteLine($"  city, a 0.5 kg ball dropped from 1.3 m at the spawn: first contact {firstContactTick} tick(s) after release, "
                        + $"that tick {drop[firstContactTick]:F2} ms; the drop's ticks {Stat(drop)}");
    }

    private static double Time(BodyWorld world)
    {
        var sw = Stopwatch.StartNew();
        world.Tick();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static string Stat(List<double> t)
    {
        var s = t.OrderBy(v => v).ToList();
        return $"mean {t.Average():F3} ms, p95 {s[(int)(s.Count * 0.95)]:F3} ms, worst {s[^1]:F3} ms";
    }

    private static string? Arg(string[] args, string name)
        => args.FirstOrDefault(a => a.StartsWith(name, StringComparison.Ordinal))?[name.Length..];
}
