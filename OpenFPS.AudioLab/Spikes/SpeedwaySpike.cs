using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The speedway, audible, without a server and a client.
///
/// It reads the SHIPPED map — the same maps/speedway.json the server loads — builds its walls into
/// image-source reflectors, puts every car on the same racing line the server would give it, and
/// stands the listener on the grandstand deck at the map's own spawn point. What comes out of the
/// speakers is what a player logging in hears, minus occlusion and the region reverb.
///
/// It exists because the alternative way to check that a wall answers a car is to start a server,
/// start a client, log in, and listen — which is fine for the final judgement and useless for the
/// forty times you change a number and want to know whether it helped.
/// </summary>
public static class SpeedwaySpike
{
    private const float C = 343f;

    private sealed record Car(string Name, string Preset, RaceLine Line, float Start);

    /// <summary>Command line: --speedway [map] [seconds=60] [voices=4]</summary>
    public static int Run(string[] args)
    {
        string mapName = args.FirstOrDefault(a => !a.StartsWith("--") && a.IndexOf('=') < 0) ?? "speedway";
        float seconds = Arg(args, "seconds", 60f);
        int voices = (int)Arg(args, "voices", 4f);
        bool noEcho = args.Contains("noecho");     // isolate: engines only, no walls answering

        AcousticRegistry.Initialize();
        string mapPath = Path.Combine(AppContext.BaseDirectory, "maps", mapName + ".json");
        if (!File.Exists(mapPath)) { Console.WriteLine($"  no such map: {mapPath}"); return 1; }

        using var doc = JsonDocument.Parse(File.ReadAllText(mapPath), new JsonDocumentOptions
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;

        Vector3 ear = ReadVec(root.GetProperty("SpawnPoint").GetProperty("Position")) + new Vector3(0, 1.7f, 0);

        // ── The track and the field ──────────────────────────────────────────────────────────────
        var tracks = new Dictionary<string, (List<Vector3> Pts, float Width, float Banking)>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Tracks", out var tj))
            foreach (var t in tj.EnumerateArray())
                tracks[t.GetProperty("Id").GetString()!] =
                    (t.GetProperty("Waypoints").EnumerateArray().Select(ReadVec).ToList(),
                     t.TryGetProperty("WidthMetres", out var w) ? w.GetSingle() : 15f,
                     t.TryGetProperty("BankingDegrees", out var bk) ? bk.GetSingle() : 0f);

        var cars = new List<Car>();
        foreach (var v in root.GetProperty("Vehicles").EnumerateArray())
        {
            string preset = v.GetProperty("Preset").GetString()!;
            string trackId = Str(v, "Track") ?? "";
            if (!tracks.TryGetValue(trackId, out var track)) continue;
            float half = MathF.Max(0f, track.Width * 0.5f - 1.2f);
            var line = new RaceLine(track.Pts,
                                    Math.Clamp(Num(v, "LaneOffsetMetres", 0f), -half, half),
                                    Num(v, "TopSpeedKmh", 200f) / 3.6f,
                                    Num(v, "CorneringG", 1f),
                                    Num(v, "BrakingMps2", 5.5f),
                                    track.Banking);
            cars.Add(new Car(Str(v, "Name") ?? preset, preset, line, Num(v, "StartOffsetMetres", 0f)));
        }
        if (cars.Count == 0) { Console.WriteLine("  the map has no cars on a track."); return 1; }

        // ── The walls ────────────────────────────────────────────────────────────────────────────
        var colliders = LoadPrefabColliders();
        var surfaces = new List<ReflectingSurface>();
        var six = new ReflectingSurface[6];
        int sid = 1;
        foreach (var e in root.GetProperty("Entities").EnumerateArray())
        {
            string prefab = e.GetProperty("PrefabId").GetString()!;
            if (!colliders.TryGetValue(prefab, out var box)) continue;
            Vector3 scale = e.TryGetProperty("Scale", out var sc) ? ReadVec(sc) : Vector3.One;
            Quaternion rot = e.TryGetProperty("Rotation", out var rj) ? ReadQuat(rj) : Quaternion.Identity;
            int n = ImageSource.FacesOfBox(ReadVec(e.GetProperty("Position")), box.Size * scale, rot,
                                           AcousticRegistry.GetProperties(box.Material).Absorption, sid, six);
            sid += 6;
            for (int i = 0; i < n; i++)
            {
                var f = six[i];
                if (f.HalfU.Length() < 1.5f && f.HalfV.Length() < 1.5f) continue;
                surfaces.Add(f);
            }
        }

        if (args.Contains("probe"))
        {
            // Stand a car at a few places round the lap and print, in full, what comes back and
            // from where. When "the walls are not answering" this is the difference between a
            // geometry bug, a gain below the threshold and a path too long to count.
            Span<Reflection> got = stackalloc Reflection[6];
            foreach (var (label, src) in new (string, Vector3)[]
            {
                ("start/finish, right in front", new Vector3(0f, 0.3f, -150f)),
                ("front straight, 120 m east",   new Vector3(120f, 0.3f, -150f)),
                ("turn one",                     new Vector3(280f, 7.3f, -80f)),
                ("back straight, opposite",      new Vector3(0f, 0.3f, 150f)),
            })
            {
                float d = Vector3.Distance(src, ear);
                int n = ImageSource.FirstOrder(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(surfaces),
                                               src, ear, C, got);
                Console.WriteLine($"\n  car {label}: {d:F0} m away, {n} reflection(s)");
                for (int i = 0; i < n; i++)
                    Console.WriteLine($"    surface {got[i].SurfaceId,5}  path {got[i].PathLength,6:F1} m  "
                        + $"+{got[i].DelaySeconds * 1000f,5:F0} ms  gain {got[i].Gain,6:F3}  off {got[i].BouncePoint}");
            }
            Console.WriteLine();
            return 0;
        }

        // --speedway speedway walk: step the listener back from the track and meter the mix at each
        // stop. The only honest answer to "it sounds the same however far away I am" is a column of
        // numbers that either fall or do not.
        float[]? walk = args.Contains("walk") ? new[] { 0f, 40f, 100f, 200f, 400f, 700f } : null;

        Console.WriteLine($"\n  {mapName}: {cars.Count} cars, {surfaces.Count} reflecting faces, listener at {ear}.");
        Console.WriteLine($"  {voices} engines live at a time — the nearest ones. {seconds:F0} seconds.\n");
        foreach (var c in cars)
            Console.WriteLine($"    {c.Name,-16} {c.Preset,-11} {c.Line.MinSpeed * 3.6f,4:F0}-{c.Line.MaxSpeed * 3.6f,3:F0} km/h   lap {c.Line.Length:F0} m");
        Console.WriteLine();

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            provider.SetSimulatedReverbDecay(900f);
            provider.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            for (int i = 0; i < 30; i++) { provider.Update(); Thread.Sleep(8); }

            // Per vehicle, the same way the client does it: a stock car and a muscle car are
            // fourteen decibels apart and placing both at one level throws the field's balance away.
            var place = new Dictionary<string, (float Gain, float Reference, float Range)>();
            foreach (var key in cars.Select(c => c.Preset).Distinct())
            {
                float lvl = VehicleProfile.ByName(key).SourceLevelDb;
                var (g, r) = Loudness.Place(lvl);
                place[key] = (g, MathF.Max(r, 3f), Loudness.AudibleRange(lvl));
            }

            var lap = new float[cars.Count];
            var speed = new float[cars.Count];
            var brake = new float[cars.Count];
            for (int i = 0; i < cars.Count; i++)
            {
                lap[i] = cars[i].Start;
                cars[i].Line.Sample(lap[i], out _, out _, out speed[i]);
                brake[i] = 8f;
            }

            var live = new HashSet<int>();
            var started = new double[cars.Count];
            var echoes = new Dictionary<(int Car, int Surface), int>();
            int nextEcho = -700000;
            var reflections = new Reflection[2];
            var near = new List<ReflectingSurface>();

            var clock = System.Diagnostics.Stopwatch.StartNew();
            double last = 0, lastReport = -5;
            int walkAt = -1;
            var earNow = ear;
            double walkHold = 9.0;
            if (walk != null) seconds = (float)(walk.Length * walkHold);
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                double now = clock.Elapsed.TotalSeconds;

                if (walk != null)
                {
                    int step = Math.Min(walk.Length - 1, (int)(now / walkHold));
                    if (step != walkAt)
                    {
                        walkAt = step;
                        // Straight back from the grandstand, away from the front straight.
                        earNow = ear - new Vector3(0f, 0f, walk[step]);
                        Console.WriteLine($"\n  -- listener {walk[step]:F0} m back from the stand, at {earNow}");
                        provider.ResetLoudnessMeter();
                    }
                }
                float dt = (float)Math.Min(0.05, now - last);
                last = now;

                // Advance every car, whether or not it can be heard.
                var pos = new Vector3[cars.Count];
                var head = new float[cars.Count];
                for (int i = 0; i < cars.Count; i++)
                {
                    var line = cars[i].Line;
                    float look = MathF.Max(8f, speed[i] * speed[i] / (2f * brake[i]));
                    line.Sample(lap[i] + look, out _, out _, out float ahead);
                    line.Sample(lap[i], out _, out _, out float hereLimit);
                    float want = MathF.Min(hereLimit, ahead);
                    speed[i] = want > speed[i] ? MathF.Min(want, speed[i] + 6f * dt)
                                               : MathF.Max(want, speed[i] - brake[i] * dt);
                    lap[i] = (lap[i] + speed[i] * dt) % line.Length;
                    line.Sample(lap[i], out pos[i], out head[i], out _);
                }

                // The nearest few get a voice — with the same hysteresis the client uses, or the cars
                // trade places several times a lap and every trade restarts a synthesis.
                var order = Enumerable.Range(0, cars.Count)
                    .OrderBy(i => Vector3.DistanceSquared(pos[i], earNow)
                                  * (live.Contains(i) && now - started[i] > 0 ? 0.5625f : 1f))
                    .Take(voices).ToHashSet();
                foreach (int i in live.Except(order).ToList())
                {
                    if (now - started[i] < 2.5) { order.Add(i); continue; }   // minimum hold
                    // Faded, not cut — the same way the client does it. A synthesized engine has no
                    // zero-crossing to stop on, so cutting one is a step in the waveform.
                    if (!provider.FadeOutEngine(-91000 - i)) { order.Add(i); continue; }
                    provider.StopSound(-91000 - i);
                    foreach (var k in echoes.Keys.Where(k => k.Car == i).ToList())
                    { provider.StopSound(echoes[k]); echoes.Remove(k); }
                    live.Remove(i);
                }
                while (order.Count > voices) order.Remove(order.OrderByDescending(i => Vector3.DistanceSquared(pos[i], earNow)).First());

                foreach (int i in order)
                {
                    var v = VehicleProfile.ByName(cars[i].Preset);
                    var rot = Quaternion.CreateFromYawPitchRoll(head[i], 0f, 0f);
                    Vector3 p = pos[i] + Vector3.Transform(new Vector3(0f, 0.3f, v.ExhaustOffsetZ * 0.6f), rot);
                    var vel = new Vector3(MathF.Sin(head[i]), 0f, MathF.Cos(head[i])) * speed[i];
                    var em = new SpatialEmitter
                    {
                        EntityId = -91000 - i, SoundId = "engine:" + cars[i].Preset, IsSynth = true,
                        EngineKey = cars[i].Preset, EngineSpeed = speed[i], EngineRunning = true,
                        Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
                        Position = p, Velocity = vel,
                        Volume = place[cars[i].Preset].Gain, Range = place[cars[i].Preset].Range,
                        MinDistance = place[cars[i].Preset].Reference, Pitch = 1f,
                        TargetRegionId = AcousticConstants.GlobalRegionId, EnableReverb = true,
                    };
                    if (live.Add(i)) { started[i] = now; provider.PlaySpatialSound(em); }
                    else provider.UpdateSpatialAttributes(em);

                    // ...and the walls answer it.
                    if (noEcho) continue;
                    Vector3 mid = (p + earNow) * 0.5f;
                    near.Clear();
                    foreach (var s in surfaces)
                        if (Vector3.DistanceSquared(s.Centre, mid) < 260f * 260f) near.Add(s);
                    float directDist = MathF.Max(1f, Vector3.Distance(p, earNow));
                    int nr = ImageSource.FirstOrder(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(near),
                                                    p, earNow, C, reflections);
                    // Walls this car has stopped bouncing off give their voices back.
                    foreach (var k in echoes.Keys.Where(k => k.Car == i).ToList())
                    {
                        bool still = false;
                        for (int j = 0; j < nr; j++) if (reflections[j].SurfaceId == k.Surface) { still = true; break; }
                        if (still) continue;
                        provider.StopSound(echoes[k]);
                        echoes.Remove(k);
                    }
                    for (int k = 0; k < nr; k++)
                    {
                        var r = reflections[k];
                        float g = Math.Clamp(r.Gain * r.PathLength / directDist, 0f, 1f);
                        var key = (i, r.SurfaceId);
                        if (!echoes.TryGetValue(key, out int vid)) echoes[key] = vid = nextEcho--;
                        var echo = new SpatialEmitter
                        {
                            EntityId = vid, SoundId = "engine-echo", IsSynth = true,
                            EchoOfEntity = -91000 - i, EchoDelaySeconds = r.DelaySeconds, EchoGain = g,
                            EngineKey = "", Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
                            Position = r.ApparentPosition, Velocity = Vector3.Zero,
                            Volume = place[cars[i].Preset].Gain, Range = place[cars[i].Preset].Range,
                            MinDistance = place[cars[i].Preset].Reference, Pitch = 1f,
                            TargetRegionId = AcousticConstants.GlobalRegionId, EnableReverb = false,
                        };
                        if (provider.IsPlaying(vid)) provider.UpdateSpatialAttributes(echo);
                        else provider.PlaySpatialSound(echo);
                    }
                }

                if (now - lastReport >= 4.0)
                {
                    lastReport = now;
                    int nearest = order.OrderBy(i => Vector3.DistanceSquared(pos[i], earNow)).First();
                    // What the ENGINE is doing, not just what the car is doing. A car that is
                    // genuinely lifting for a turn and an engine that is failing to hold the speed it
                    // was given sound exactly the same from a chair and are nothing alike in here.
                    string engineState = "";
                    if (provider.TryGetEngineTelemetry(-91000 - nearest, out float told, out float own, out float rpm, out int gear))
                        engineState = $"  engine told {told * 3.6f:F0}, driveline {own * 3.6f:F0} km/h, {rpm:F0} rpm, gear {gear}";
                    Serilog.Log.Information("  {Now,5:F1}s  nearest {Name} {Dist:F0} m  {Kmh:F0} km/h{Engine} — {Live} engines, {Walls} walls answering",
                                            now, cars[nearest].Name, Vector3.Distance(pos[nearest], earNow),
                                            speed[nearest] * 3.6f, engineState, live.Count, echoes.Count);
                }

                provider.UpdateListener(earNow, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
                provider.Update();
                Thread.Sleep(6);
            }

            foreach (int i in live) provider.StopSound(-91000 - i);
            foreach (int v in echoes.Values) provider.StopSound(v);
            for (int i = 0; i < 40; i++) { provider.Update(); Thread.Sleep(8); }
            return 0;
        }
        finally { provider.Dispose(); }
    }

    // ── Reading the map ─────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, (Vector3 Size, string Material)> LoadPrefabColliders()
    {
        var map = new Dictionary<string, (Vector3, string)>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.Combine(AppContext.BaseDirectory, "prefabs");
        if (!Directory.Exists(dir)) return map;
        foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(file));
                var r = d.RootElement;
                if (!r.TryGetProperty("Id", out var idj)) continue;
                if (!r.TryGetProperty("ColliderSize", out var cs)) continue;
                // A prefab that emits sound is a source, not geometry — the same rule the game uses.
                if (r.TryGetProperty("HasEmitter", out var he) && he.ValueKind == JsonValueKind.True) continue;
                bool solid = !r.TryGetProperty("IsSolid", out var sj) || sj.ValueKind != JsonValueKind.False;
                if (!solid) continue;
                map[idj.GetString()!] = (ReadVec(cs),
                    r.TryGetProperty("Material", out var mj) ? mj.GetString() ?? "Generic" : "Generic");
            }
            catch { /* a prefab that will not parse is the prefab loader's problem, not ours */ }
        }
        return map;
    }

    private static Vector3 ReadVec(JsonElement e) => new(
        e.GetProperty("X").GetSingle(), e.GetProperty("Y").GetSingle(), e.GetProperty("Z").GetSingle());

    private static Quaternion ReadQuat(JsonElement e) => new(
        e.GetProperty("X").GetSingle(), e.GetProperty("Y").GetSingle(),
        e.GetProperty("Z").GetSingle(), e.GetProperty("W").GetSingle());

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static float Num(JsonElement e, string name, float fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    private static float Arg(string[] args, string key, float fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal));
        return a != null && float.TryParse(a[(key.Length + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : fallback;
    }
}
