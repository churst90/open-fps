using System.Globalization;
using System.Numerics;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Common;
using OpenFPS.Materials;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --beacon-count [map=city] tp=E,N,H [tp=...] [list] [root=DIR/]
///
/// How many of a map's door beacons blip from a spot, by the hand-rolled tracer's path (how a blip was decided
/// until 2026-10-11) and by the acoustic worker's answer (how every sustained voice is heard, and blips now:
/// docs/SPECIAL_CASES_AUDIT.md, P3d and P6). Every door is asked, not only the three nearest a blip picks, so the
/// count says which walls and doorways the two answers disagree about. The rule is the beacons' own: in sight,
/// or no more than half blocked (BeaconAids.Hears). tp= is a /tp spot (x east, y north, z the floor); the ear is
/// a standing body's eyes above it. list names every door whose answer changed, and why.
/// </summary>
public static class BeaconCountSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string mapId = args.FirstOrDefault(a => a.StartsWith("map="))?[4..] ?? "city";
        string root = args.FirstOrDefault(a => a.StartsWith("root="))?[5..] ?? AppContext.BaseDirectory;
        bool list = args.Contains("list");
        static float[] F(string s) => s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        var ears = args.Where(a => a.StartsWith("tp=")).Select(a => F(a[3..]))
                       .Select(f => new Vector3(f[0], f[2] + LocalPlayerState.StandingEyeHeight, f[1])).ToList();

        var world = PathProbeSpike.LoadAsClient(root, mapId);
        var doors = world.Entities.Values
            .Where(e => string.Equals(e.Definition.Identity.BeaconCategory, Beacons.Door, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Id).OrderBy(i => i).ToList();
        Console.WriteLine($"  {mapId}: {world.Entities.Count} entities, {doors.Count} door beacons");

        var acoustics = new SpatialAcoustics();
        using var worker = new AsyncAcousticWorker(acoustics);
        worker.UpdateWorld(world);
        worker.Start();
        var facade = new AudioEngineFacade();
        var tracer = new BeaconAids(facade, BeaconPreferences.InMemory(), acoustics);
        var asked = new BeaconAids(facade, BeaconPreferences.InMemory(), acoustics) { Worker = worker };

        foreach (var ear in ears)
        {
            // The first request builds the scene; wait until the worker answers one.
            var until = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < until)
            {
                worker.EnqueueRequest(new AcousticRequest { EntityId = 999, ListenerPos = ear, SourcePos = ear + Vector3.UnitX, SourceRadius = 0.1f });
                Thread.Sleep(200);
                if (worker.TryGetResult(999, out var p) && p.Count > 0) break;
            }
            Console.WriteLine($"  ear ({ear.X:F2}, {ear.Y:F2}, {ear.Z:F2}), /tp {ear.X:F2} {ear.Z:F2} {ear.Y - LocalPlayerState.StandingEyeHeight:F2}; Steam Audio {(worker.SteamAudioActive ? "on" : "OFF")}");

            // Asked a pool's worth at a time, each until it is answered for this ear.
            var points = new Dictionary<int, Vector3>();
            var answers = new Dictionary<int, AcousticPathData>();
            for (int from = 0; from < doors.Count; from += 48)
            {
                var batch = doors.Skip(from).Take(48).ToList();
                foreach (int d in batch) points[d] = asked.AskFor(world, d, ear);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline && batch.Any(d => !answers.ContainsKey(d)))
                {
                    Thread.Sleep(30);
                    foreach (int d in batch)
                    {
                        if (answers.ContainsKey(d)) continue;
                        if (asked.Answered(world, d, ear, points[d]) is { } a && a.Provenance != PathProvenance.Unknown
                            && a.SourcePosition == points[d]) answers[d] = a;
                        else asked.AskFor(world, d, ear);
                    }
                }
            }

            int before = 0, after = 0, simulated = 0, near = 0, nearBefore = 0, nearAfter = 0;
            var changed = new List<string>();
            foreach (int d in doors)
            {
                var at = points[d];
                bool inSight = tracer.InSight(world, d, ear, at);
                var old = tracer.Answered(world, d, ear, at);
                bool was = BeaconAids.Hears(old, () => inSight);
                answers.TryGetValue(d, out var now);
                bool answered = answers.ContainsKey(d);
                bool @is = BeaconAids.Hears(answered ? now : old, () => inSight);
                if (answered && now.Provenance is PathProvenance.Simulated or PathProvenance.Held) simulated++;
                if (was) before++;
                if (@is) after++;
                // Within a door beacon's reach (BeaconAids: 12 m, the nearest three of them blip).
                if (Vector3.Distance(ear, at) <= 12f) { near++; if (was) nearBefore++; if (@is) nearAfter++; }
                if (was != @is)
                    changed.Add($"    door {d} at /tp {at.X:F1} {at.Z:F1} {at.Y:F1}, {Vector3.Distance(ear, at):F1} m: "
                              + $"{(was ? "blipped" : "silent")} -> {(@is ? "blips" : "silent")}; in sight {(inSight ? "yes" : "no")}, "
                              + $"blocked {old?.Occlusion ?? 0f:F2} by the tracer, {(answered ? $"{now.Occlusion:F2} by the worker ({now.Provenance})" : "no answer")}"
                              + $"{(answered ? $", worker bands {Db(now.EqLow):F0}/{Db(now.EqMid):F0}/{Db(now.EqHigh):F0} dB" : "")}"
                              + $", in {RegionName(world, acoustics, at)}");
            }
            Console.WriteLine($"    {before} of {doors.Count} doors blip by the tracer, {after} by the worker's answer "
                            + $"({answers.Count} answered, {simulated} by the simulator); {changed.Count} changed. "
                            + $"Within 12 m: {near} doors, {nearBefore} blip by the tracer, {nearAfter} by the worker");
            foreach (var line in list ? changed : changed.Take(12)) Console.WriteLine(line);
        }
        return 0;
    }

    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));

    private static string RegionName(WorldSnapshot world, SpatialAcoustics acoustics, Vector3 at)
    {
        int r = acoustics.GetRegionAt(world, at);
        return world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(r, out var reg) && reg.FriendlyName.Length > 0
            ? $"'{reg.FriendlyName}'" : $"region {r}";
    }
}
