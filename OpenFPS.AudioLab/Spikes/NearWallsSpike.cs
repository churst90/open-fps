using System.Globalization;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Systems;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --near-walls [map=city] [from=E,N,H] [to=E,N,H] [steps=40] [open=ID,...] [heading=DEG] [at=E,N,H;...]
///
/// The near-wall copies (BoundaryModel) along a walk on a real map, through the client's own probes over the
/// triangle world the client builds: at each step every probe that meets something, how far, how much of a
/// wall it is (its share), and what the copies add at each ear against the direct sound, as the mixer sums and
/// trims them (FmodAudioProvider.UpdateBoundaries), beside what they added when every surface was a whole wall.
/// Points are as /tp takes them. The default is Marlow Tower's front entrance (door 1158 opened) out to the
/// pavement on Main Street, facing east; at= adds spots to stand at, printed after the walk.
/// Written for the jamb counted as a wall (Resonance, Sean in a shop's open doorway, 2026-10-14).
/// </summary>
public static class NearWallsSpike
{
    private static string? Arg(string[] args, string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string mapId = Arg(args, "map") ?? "city";
        if (!WalkPairSpike.Load(mapId, out var maps)) return 1;
        maps.TryGetMap(mapId, out var ecs, out _, out var grid, out _);
        maps.TryGetMapData(mapId, out var data);

        var authored = maps.AuthoredEntities(mapId);
        var doors = new DoorSystem();
        foreach (var part in (Arg(args, "open") ?? "1158").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (authored.TryGetValue(int.Parse(part, CultureInfo.InvariantCulture), out var door) && ecs.Has<DoorComponent>(door))
                Console.WriteLine($"  door #{part} {(DoorSystem.Set(ecs, door, open: true, by: Parse(Arg(args, "from") ?? "-7,-103,0.2")) ? "opens" : "does not open")}");
        for (int i = 0; i < 120; i++) doors.Update(ecs, PhysicsConstants.FixedDeltaTime, _ => { });

        var defs = EntityDefinitionFactory.StaticDefinitions(ecs);
        var (_, snap) = GeometryParitySpike.ClientSnapshots(defs, data.TileMetres);
        Console.WriteLine($"  client: {defs.Count:N0} definitions, triangle world {(snap.Geometry?.TriangleCount ?? 0):N0} triangles");

        var from = Parse(Arg(args, "from") ?? "-12,-103,0.1");
        var to = Parse(Arg(args, "to") ?? "-7,-103,0.2");
        int steps = int.Parse(Arg(args, "steps") ?? "40", CultureInfo.InvariantCulture);
        var walk = to - from;
        float yaw = Arg(args, "heading") is { } h ? float.Parse(h, CultureInfo.InvariantCulture) * MathF.PI / 180f
                                                  : MathF.Atan2(-walk.X, -walk.Z);
        var head = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var spatial = new SpatialService();
        var prober = new ClientAudioSystem.BoundaryProber();
        var probes = new BoundaryProbe[BoundaryModel.ProbeDirections.Length];
        float c = AudioPhysics.SpeedOfSound;
        string[] names = { "right", "left", "up", "down", "ahead", "behind" };

        // What the copies add at each ear against the direct sound, dB, summed in power and trimmed as the mixer does;
        // the sum is their gains before the trim, which scales the set back to MaxBoundaryReflectionSum.
        (float L, float R, float Sum) Copies(bool shared)
        {
            Span<BoundaryTap> taps = stackalloc BoundaryTap[probes.Length];
            Span<bool> live = stackalloc bool[probes.Length];
            var set = new BoundaryProbe[probes.Length];
            for (int i = 0; i < probes.Length; i++) set[i] = shared ? probes[i] : probes[i] with { Share = 1f };
            float trim = FmodAudioProvider.BoundaryTaps(set, c, 48000, taps, live) * OpenFPS.Client.AudioEngine.Core.MixSettings.FromEnvironment().CopiesTrim;
            double l = 0, r = 0;
            float total = 0f;
            for (int i = 0; i < probes.Length; i++)
                if (live[i])
                {
                    l += Sq(taps[i].GainL * trim); r += Sq(taps[i].GainR * trim);
                    total += MathF.Max(MathF.Abs(taps[i].GainL), MathF.Abs(taps[i].GainR));
                }
            return ((float)(10 * Math.Log10(l + 1e-12)), (float)(10 * Math.Log10(r + 1e-12)), total);
        }
        static double Sq(float x) => (double)x * x;
        static string Db(float db) => db < -100f ? "  none" : $"{db,6:F1}";

        long ticks = 0; int casts = 0;
        void Stand(Vector3 feet, string label)
        {
            var ear = feet + new Vector3(0f, LocalPlayerState.StandingEyeHeight, 0f);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            prober.Probe(spatial, snap, ear, head, c, probes);
            ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; casts++;
            var now = Copies(shared: true);
            var was = Copies(shared: false);
            var hits = string.Join(" ", probes.Select((p, i) => p.Distance < BoundaryModel.MaxDistance && !BoundaryModel.IsFloor(p.HeadDirection)
                ? $"{names[i]} {p.Distance:F2}m x{p.Share:F2}" : null).Where(s => s != null));
            Console.WriteLine($"  {label,-24} copies L {Db(now.L)} R {Db(now.R)} dB, sum {now.Sum:F2}"
                            + $" (all whole: L {Db(was.L)} R {Db(was.R)}, sum {was.Sum:F2})   {hits}");
        }

        Console.WriteLine($"  walk {PlayerCoordinates.Format(from)} to {PlayerCoordinates.Format(to)}, facing {yaw * 180f / MathF.PI:F0} degrees (0 north)");
        for (int k = 0; k <= steps; k++)
        {
            var feet = Vector3.Lerp(from, to, k / (float)steps);
            Stand(feet, $"/tp {feet.X:F2} {feet.Z:F2} {feet.Y:F2}");
        }
        if (Arg(args, "at") is { } atArg)
        {
            Console.WriteLine("  standing:");
            foreach (var part in atArg.Split(';')) { var feet = Parse(part); Stand(feet, $"/tp {feet.X:F2} {feet.Z:F2} {feet.Y:F2}"); }
        }
        // survey: every room's standing spot (StandingSpots.InRoom), turned through eight headings, and what the shares
        // change there: rooms are where nothing should.
        if (args.Contains("survey"))
        {
            var changes = new List<(float Db, string Where)>();
            ecs.Query(new Arch.Core.QueryDescription().WithAll<RegionComponent>(), (Arch.Core.Entity e, ref RegionComponent region) =>
            {
                if (StandingSpots.InRoom(ecs, grid, e) is not { } spot) return;
                var ear = spot + new Vector3(0f, LocalPlayerState.StandingEyeHeight, 0f);
                for (int turn = 0; turn < 8; turn++)
                {
                    prober.Probe(spatial, snap, ear, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn * MathF.PI / 4f), c, probes);
                    var now = Copies(shared: true);
                    var was = Copies(shared: false);
                    float loud = MathF.Max(now.L, now.R), before = MathF.Max(was.L, was.R);
                    if (before < -100f) continue;
                    changes.Add((loud - before, $"{region.FriendlyName} /tp {StandingSpots.Typed(spot)} heading {turn * 45}"));
                }
            });
            changes.Sort((a, b) => a.Db.CompareTo(b.Db));
            int same = changes.Count(x => MathF.Abs(x.Db) < 0.05f);
            Console.WriteLine($"  survey: {changes.Count} room standing spots and headings with a copy; {same} unchanged (within 0.05 dB); "
                            + $"louder ear's copies {changes.First().Db:F1} to {changes.Last().Db:F1} dB, median {changes[changes.Count / 2].Db:F2}");
            foreach (var x in changes.Take(8)) Console.WriteLine($"    {x.Db,6:F1} dB  {x.Where}");
        }

        // Warm, over the walk again a hundred times: what a frame pays for the six probes and their looks.
        ticks = 0; casts = 0;
        for (int round = 0; round < 100; round++)
            for (int k = 0; k <= steps; k++)
            {
                var ear = Vector3.Lerp(from, to, k / (float)steps) + new Vector3(0f, LocalPlayerState.StandingEyeHeight, 0f);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                prober.Probe(spatial, snap, ear, head, c, probes);
                ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; casts++;
            }
        Console.WriteLine($"  the probes and their looks: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / casts:F3} ms a frame on average over {casts}");
        return 0;
    }

    private static Vector3 Parse(string s)
    {
        var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return PlayerCoordinates.ToWorld(f[0], f[1], f[2]);
    }
}
