using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --pop-hunt [map=city] ear=x,y,z [sec=60] [cars=40] [extra=30] [seed=1] [verbose] [lines]
///
/// "Now and then I hear sounds like a siren pop through for an instant when I'm deep inside a
/// building" (Cody, 2026-10-02). The game's [POP] line names the voice; this finds the answer that
/// made it. Cars drive the city's streets at 50 km/h, each asked about at the game's own cadence (a
/// source past 50 m every tenth frame of the 60 Hz audio update, past 15 m every second frame), and
/// `extra` still sources round the ear stand for the walkers and machines that share the worker's
/// source pool. Every frame each car's answer is read as the game reads it (TryGetResult), turned
/// into the dB the provider is handed per band, and watched as the game's detector watches a voice:
/// up 15 dB or more within 150 ms and back within 400 ms is an excursion, printed with what the
/// worker says produced the answer before and during. Game coordinates, y up.
/// </summary>
public static class PopHuntSpike
{
    private sealed class Car
    {
        public int Id;
        public Vector3 A, B;      // the lane's two ends
        public float S, Speed;    // metres along it, metres a second (sign is the direction)
        public Vector3 Pos;
        public float Mid = float.NaN;
        public Watch MidWatch = new(), HighWatch = new();
        public string Prov = "";
        public Vector3 AnsweredAt;
    }

    private sealed class Watch
    {
        public float Base = float.NaN; public double BaseAt; public string BaseProv = "";
        public double RiseAt = -1; public float Peak; public string PeakProv = "";
        public Vector3 BaseAt3, PeakAt3;
    }

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string mapId = Str(args, "map") ?? "city";
        static Vector3 P(string s) { var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new Vector3(f[0], f[1], f[2]); }
        var ear = P(Str(args, "ear") ?? "7,1.65,140");
        float seconds = Num(args, "sec", 60f);
        int carCount = (int)Num(args, "cars", 40f);
        int extra = (int)Num(args, "extra", 30f);
        bool verbose = args.Contains("verbose");
        bool lines = args.Contains("lines");
        var rng = new Random((int)Num(args, "seed", 1f));

        var world = PathProbeSpike.LoadAsClient(OpenFPS.AudioLab.LabPaths.Server(), mapId);
        var acoustics = new SpatialAcoustics();
        AsyncAcousticWorker.TraceProvenance = true;
        using var worker = new AsyncAcousticWorker(acoustics);
        worker.UpdateWorld(world);
        worker.Start();
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed.TotalSeconds < 120)
        {
            worker.EnqueueRequest(new AcousticRequest { EntityId = 999_999, ListenerPos = ear, SourcePos = ear + Vector3.UnitX, SourceRadius = 0.1f });
            Thread.Sleep(200);
            if (worker.TryGetResult(999_999, out var p0) && p0.Count > 0) break;
        }
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        int earRegion = acoustics.GetRegionAt(world, ear);
        string earName = world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(earRegion, out var er) ? er.FriendlyName : "";
        Console.WriteLine($"  ear ({ear.X:F1}, {ear.Y:F2}, {ear.Z:F1}) region {earRegion} '{earName}'; Steam Audio {(worker.SteamAudioActive ? "on" : "OFF")}; "
                        + $"{carCount} cars, {extra} still sources");

        // The city's streets as their lanes run: centre lines, lanes 1.5 and 4.5 m either side.
        var roads = new (Vector3 A, Vector3 B)[]
        {
            (new(0, 0, -300), new(0, 0, 420)),       // Main Street
            (new(-130, 0, -140), new(-130, 0, 272)), // Wharf Avenue
            (new(130, 0, -140), new(130, 0, 272)),   // Calder Avenue
            (new(-166, 0, -130), new(140, 0, -130)), // Dock Street
            (new(-448, 0, 0), new(190, 0, 0)),       // Central Street
            (new(-140, 0, 130), new(228, 0, 130)),   // Foundry Street
            (new(-140, 0, 260), new(140, 0, 260)),   // North Street
        };
        var cars = new List<Car>();
        for (int i = 0; i < carCount; i++)
        {
            var (a, b) = roads[i % roads.Length];
            Vector3 along = Vector3.Normalize(b - a), side = new(along.Z, 0, -along.X);
            float lane = (i / roads.Length % 2 == 0 ? 1.5f : 4.5f) * (i % 2 == 0 ? 1f : -1f);
            const float exhaust = 0.4f;
            var off = side * lane + new Vector3(0, 0.05f + exhaust, 0);
            float len = Vector3.Distance(a, b);
            cars.Add(new Car
            {
                Id = 6460 + i, A = a + off, B = b + off,
                S = (float)rng.NextDouble() * len,
                Speed = (lane > 0 ? 1f : -1f) * (11f + 4f * (float)rng.NextDouble()),
            });
        }
        // The walkers, birds and machines that hold sources in the same pool: still, round the ear.
        var still = new List<(int Id, Vector3 At)>();
        for (int i = 0; i < extra; i++)
        {
            float ang = (float)(rng.NextDouble() * Math.PI * 2), r = 5f + 55f * (float)rng.NextDouble();
            still.Add((20_000 + i, new Vector3(ear.X + r * MathF.Cos(ang), 1.2f, ear.Z + r * MathF.Sin(ang))));
        }

        const double frame = 1.0 / 60.0;
        var clock = Stopwatch.StartNew();
        int n = 0, excursions = 0, excursionsHigh = 0;
        double next = 0;
        var worst = new List<(float Rise, string Line)>();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            double now = clock.Elapsed.TotalSeconds;
            foreach (var c in cars)
            {
                float len = Vector3.Distance(c.A, c.B);
                c.S += c.Speed * (float)frame;
                if (c.S > len) c.S -= len; else if (c.S < 0) c.S += len;
                c.Pos = Vector3.Lerp(c.A, c.B, c.S / len);
                float dist = Vector3.Distance(ear, c.Pos);
                int rate = dist > 50f ? 10 : dist > 15f ? 2 : 1;
                if (n % rate == c.Id % rate)
                    worker.EnqueueRequest(new AcousticRequest { EntityId = c.Id, ListenerPos = ear, SourcePos = c.Pos, SourceRadius = 0.4f });
            }
            foreach (var (id, at) in still)
            {
                float dist = Vector3.Distance(ear, at);
                int rate = dist > 50f ? 10 : dist > 15f ? 2 : 1;
                if (n % rate == id % rate)
                    worker.EnqueueRequest(new AcousticRequest { EntityId = id, ListenerPos = ear, SourcePos = at, SourceRadius = 0.5f });
            }
            foreach (var c in cars)
            {
                if (!worker.TryGetResult(c.Id, out var paths) || paths.Count == 0) continue;
                var p = paths[0];
                var (_, mid, high) = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.PathEqDb(p.EqLow, p.EqMid, p.EqHigh, p.AirLowDb, p.AirMidDb, p.AirHighDb);
                mid = MathF.Max(-80f, mid); high = MathF.Max(-80f, high);
                if (worker.Provenance.TryGetValue(c.Id, out var prov)) c.Prov = prov;
                c.AnsweredAt = p.SourcePosition;
                if (verbose && (MathF.Abs(mid - c.Mid) > 3f || float.IsNaN(c.Mid)))
                    Console.WriteLine($"  {now,7:F3} e{c.Id} ({c.Pos.X:F0}, {c.Pos.Z:F0}) {Vector3.Distance(ear, c.Pos):F0} m: mid {mid:F0} high {high:F0} occ {p.Occlusion:F2}  [{c.Prov}]");
                c.Mid = mid;
                if (Check(c.MidWatch, mid, now, c.Prov, c.AnsweredAt, out var midLine))
                {
                    excursions++;
                    string line = $"  {now,7:F3} MID  e{c.Id} at ({c.Pos.X:F0}, {c.Pos.Z:F0}) {Vector3.Distance(ear, c.Pos):F0} m: {midLine}";
                    Console.WriteLine(line);
                    if (lines) Line(boxes, acoustics, ear, c.MidWatch.BaseAt3, "before");
                    if (lines) Line(boxes, acoustics, ear, c.MidWatch.PeakAt3, "during");
                    worst.Add((c.MidWatch.Peak - c.MidWatch.Base, line));
                }
                if (Check(c.HighWatch, high, now, c.Prov, c.AnsweredAt, out var highLine))
                {
                    excursionsHigh++;
                    Console.WriteLine($"  {now,7:F3} HIGH e{c.Id} at ({c.Pos.X:F0}, {c.Pos.Z:F0}) {Vector3.Distance(ear, c.Pos):F0} m: {highLine}");
                }
            }
            n++;
            next += frame;
            double sleep = next - clock.Elapsed.TotalSeconds;
            if (sleep > 0) Thread.Sleep(TimeSpan.FromSeconds(sleep));
        }
        Console.WriteLine($"  {n} frames in {clock.Elapsed.TotalSeconds:F1} s; {excursions} mid-band excursion(s), {excursionsHigh} high-band; worker: {worker.RouteCostSummary}; degraded {worker.IsDegraded}");
        return 0;
    }

    /// <summary>The game's detector (FmodAudioProvider.WatchForPops) on one band of the path's target:
    /// up 15 dB within 150 ms, back by 12 within 400.</summary>
    private static bool Check(Watch w, float level, double now, string prov, Vector3 at, out string line)
    {
        line = "";
        if (float.IsNaN(w.Base) || now - w.BaseAt > 0.15 || level < w.Base)
            if (w.RiseAt < 0) { w.Base = level; w.BaseAt = now; w.BaseProv = prov; w.BaseAt3 = at; }
        if (w.RiseAt < 0)
        {
            if (level > w.Base + 15f && now - w.BaseAt <= 0.15) { w.RiseAt = now; w.Peak = level; w.PeakProv = prov; w.PeakAt3 = at; }
            return false;
        }
        if (level > w.Peak) { w.Peak = level; w.PeakProv = prov; w.PeakAt3 = at; }
        if (now - w.RiseAt > 0.4) { w.RiseAt = -1; w.Base = level; w.BaseAt = now; w.BaseProv = prov; return false; }
        if (level >= w.Peak - 12f) return false;
        line = $"{w.Base:F0} -> {w.Peak:F0} dB and back in {(now - w.RiseAt) * 1000:F0} ms\n      before: {w.BaseProv}\n      during: {w.PeakProv}\n      after:  {prov}";
        w.RiseAt = -1; w.Base = level; w.BaseAt = now; w.BaseProv = prov;
        return true;
    }

    /// <summary>What the straight line from the ear to where the answer was asked about passes through,
    /// and what the barrier search makes of it.</summary>
    private static void Line(List<SteamAudioScene.Box> boxes, SpatialAcoustics acoustics, Vector3 ear, Vector3 src, string label)
    {
        Vector3 dir = Vector3.Normalize(src - ear);
        float len = Vector3.Distance(ear, src);
        var hits = new List<(float At, SteamAudioScene.Box B)>();
        foreach (var b in boxes)
            if (OpenFPS.Common.GeometryUtils.RayIntersectsOBB(ear, dir, b.Center, b.Size, b.Rotation, out float at) && at <= len)
                hits.Add((at, b));
        hits.Sort((x, y) => x.At.CompareTo(y.At));
        float d = acoustics.Routes?.BarrierPathDifference(src, ear, out var edge, out bool v) ?? -1f;
        Console.WriteLine($"      {label}: src ({src.X:F2}, {src.Y:F2}, {src.Z:F2}); {hits.Count} box(es) on the line; barrier {d:F2} m");
        foreach (var (at, b) in hits.Take(8))
            Console.WriteLine($"        {at,6:F1} m  {b.Material,-16} centre ({b.Center.X:F1}, {b.Center.Y:F1}, {b.Center.Z:F1}) size ({b.Size.X:F2}, {b.Size.Y:F2}, {b.Size.Z:F2}) e{b.EntityId}");
        if (acoustics.Routes != null) Console.Write(acoustics.Routes.ExplainBarrier(src, ear));
    }

    private static string? Str(string[] a, string k) => a.FirstOrDefault(x => x.StartsWith(k + "=", StringComparison.Ordinal))?[(k.Length + 1)..];
    private static float Num(string[] a, string k, float f) => Str(a, k) is { } s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : f;
}
