using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// What can be heard from a spot on a map, in order, and under which distance law.
///
///   --earshot [map=city] [at=x,z] [law=inverse|linear|both] [top=25]
///
/// Written for one question that no other instrument could answer: "I should hear the rumble of
/// the upcoming street if I'm a block away — is the radius of sound as far as it should be?"
/// That is a question about every source at once, and about the interaction between three things
/// that were each checked separately and never together: what a source DECLARES, what
/// <see cref="Loudness"/> does with the declaration, and which rolloff the mixer then applies.
///
/// It reads the map's own vehicle list — so it is the real fleet, at their real start positions —
/// looks each preset's level and size up exactly as ClientAudioSystem does, and prints the
/// rendered level at the listener. The two laws side by side is the point: the same map under
/// linear and inverse rolloff is two different worlds, and the column that differs is the one
/// that decides whether distance means anything.
/// </summary>
public static class EarshotSpike
{
    public static int Run(string[] args)
    {
        string mapId = Str(args, "map") ?? "city";
        int top = (int)Num(args, "top", 25f);
        string law = Str(args, "law") ?? "both";
        var at = Vec(Str(args, "at")) ?? new Vector3(0f, 1.6f, -40f);

        string? mapPath = FindUp(System.IO.Path.Combine("OpenFPS.Server", "maps", mapId + ".json"));
        if (mapPath == null) { Console.WriteLine($"  FAIL: no maps/{mapId}.json above {Environment.CurrentDirectory}"); return 1; }
        var data = System.Text.Json.JsonSerializer.Deserialize<MapFile>(System.IO.File.ReadAllText(mapPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (data?.Vehicles == null) { Console.WriteLine("  FAIL: the map declares no vehicles."); return 1; }

        Console.WriteLine($"\n  {mapId}: what is audible from ({at.X:F0}, {at.Z:F0}), at each source's CLOSEST approach.");
        Console.WriteLine("  Rendered level in dBFS — what the mixer hands the master, before the master's own gain.\n");
        Console.WriteLine("    source                       level dB   extent   closest   inverse   linear   delta");

        var rows = new List<(string Name, float Inv, float Lin, float Dist, float Level, float Ext)>();
        foreach (var vd in data.Vehicles)
        {
            if (!LevelAndExtent(vd.Preset, out float levelDb, out float extent)) continue;
            // Where it actually is. A vehicle that names a TRACK is somewhere round that track at
            // its own start offset, and its RoadStart is unset — so reading RoadStart for one put
            // every lapping vehicle at the origin, and the first run of this reported two thirds of
            // the fleet at an identical distance. The map's own racing line answers it properly.
            Vector3 pos;
            float d;
            if (!string.IsNullOrEmpty(vd.Track) && Line(data, vd.Track) is { } line)
            {
                // Its CLOSEST APPROACH round the lap, not where it happens to start.
                //
                // "The motorcycles are very quiet from here, I can hardly hear them drive by" is a
                // question about the pass-by, and a snapshot cannot answer it: a bike 140 m away at
                // its start offset comes within a few metres later in the same lap and that is the
                // moment being asked about. Walked at five-metre steps, which is finer than
                // anything a listener resolves at these distances.
                d = float.MaxValue; pos = default;
                for (float sAt = 0f; sAt < line.Length; sAt += 5f)
                {
                    line.Sample(sAt, out var p, out _, out _);
                    float dd = Vector3.Distance(p, at);
                    if (dd < d) { d = dd; pos = p; }
                }
            }
            else
            {
                pos = new Vector3(vd.RoadStart.X, vd.RoadStart.Y, vd.RoadStart.Z);
                d = Vector3.Distance(pos, at);
            }

            var (gain, reference) = Loudness.Place(levelDb, extent);
            float range = Loudness.AudibleRange(levelDb);
            float inv = Loudness.RenderedGain(gain, reference, range, d);
            // FMOD's linear law: full at the reference, silent at the range, straight line between.
            float lin = gain * Math.Clamp((range - d) / MathF.Max(1e-6f, range - reference), 0f, 1f);
            rows.Add((vd.Name ?? vd.Preset, Db(inv), Db(lin), d, levelDb, extent));
        }

        foreach (var r in rows.OrderByDescending(r => law == "linear" ? r.Lin : r.Inv).Take(top))
            Console.WriteLine($"    {Trim(r.Name, 26),-26} {r.Level,9:F0} {r.Ext,8:F1} {r.Dist,9:F0}  {r.Inv,8:F1} {r.Lin,8:F1}  {r.Lin - r.Inv,6:F1}");

        // What the street actually adds up to, which is the thing a listener hears rather than any
        // one vehicle: incoherent sources, so power sums.
        double pInv = rows.Sum(r => Math.Pow(10, r.Inv / 10.0));
        double pLin = rows.Sum(r => Math.Pow(10, r.Lin / 10.0));
        Console.WriteLine($"\n    {rows.Count} vehicles summing to {10 * Math.Log10(Math.Max(1e-12, pInv)),6:F1} dBFS inverse, "
                        + $"{10 * Math.Log10(Math.Max(1e-12, pLin)),6:F1} dBFS linear.");
        int near = rows.Count(r => r.Dist < 150f);
        Console.WriteLine($"    {near} of them within 150 m — about a block.\n");
        return 0;
    }

    private static readonly Dictionary<string, RaceLine?> _lines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The map's racing line for a track id, built once.</summary>
    private static RaceLine? Line(MapFile data, string id)
    {
        if (_lines.TryGetValue(id, out var cached)) return cached;
        var t = data.Tracks?.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        RaceLine? line = null;
        if (t != null && t.Waypoints.Count >= 3)
        {
            var wp = t.Waypoints.ConvertAll(w => new Vector3(w.X, w.Y, w.Z));
            try { line = new RaceLine(wp, 0f, 30f, 1f, 5f, t.BankingDegrees); }
            catch { line = null; }
        }
        _lines[id] = line;
        return line;
    }

    /// <summary>The same lookup ClientAudioSystem does, so the two cannot disagree.</summary>
    private static bool LevelAndExtent(string preset, out float levelDb, out float extent)
    {
        levelDb = 0f; extent = 1f;
        try
        {
            if (AircraftProfile.Presets.ContainsKey(preset))
            {
                var a = AircraftProfile.ByName(preset);
                levelDb = a.SourceLevelDb;
                extent = a.EngineSpanMetres > 0f ? a.EngineSpanMetres
                       : MathF.Max(2f, a.Propeller?.DiameterMetres ?? a.Turbine?.Fan?.DiameterMetres ?? 2f);
                return true;
            }
            if (SmallMachineSpec.Presets.ContainsKey(preset))
            {
                var m = SmallMachineSpec.ByName(preset);
                levelDb = m.SourceLevelDb; extent = m.ExtentMetres; return true;
            }
            if (MachineRegistry.Knows(preset))
            {
                var v = MachineRegistry.VehicleFor(preset);
                levelDb = v.SourceLevelDb;
                extent = MathF.Max(1f, MathF.Abs(v.ExhaustOffsetZ - v.IntakeOffsetZ));
                return true;
            }
        }
        catch { }
        return false;
    }

    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-9f, g));
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    private sealed class MapFile
    {
        public List<Veh>? Vehicles { get; set; }
        public List<Trk>? Tracks { get; set; }
    }
    private sealed class Trk
    {
        public string Id { get; set; } = "";
        // Vec3 and not Vector3: System.Text.Json binds PROPERTIES by default and Vector3's X, Y
        // and Z are fields, so a List<Vector3> deserialises as a list of zeroes — silently, which
        // put every lapping vehicle at the origin on the first run of this.
        public List<Vec3> Waypoints { get; set; } = new();
        public float BankingDegrees { get; set; }
    }
    private sealed class Veh
    {
        public string Preset { get; set; } = "";
        public string? Name { get; set; }
        public Vec3 RoadStart { get; set; } = new();
        public string? Track { get; set; }
        public float StartOffsetMetres { get; set; }
    }
    private sealed class Vec3 { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } }

    private static string? Str(string[] a, string k)
    {
        string? s = a.FirstOrDefault(x => x.StartsWith(k + "=", StringComparison.Ordinal));
        return s?[(k.Length + 1)..];
    }
    private static float Num(string[] a, string k, float d)
        => float.TryParse(Str(a, k), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : d;
    private static Vector3? Vec(string? s)
    {
        if (s == null) return null;
        var p = s.Split(',');
        return p.Length >= 2 && float.TryParse(p[0], out float x) && float.TryParse(p[^1], out float z)
             ? new Vector3(x, 1.6f, z) : null;
    }
    private static string? FindUp(string rel)
    {
        var d = new System.IO.DirectoryInfo(Environment.CurrentDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string c = System.IO.Path.Combine(d.FullName, rel);
            if (System.IO.File.Exists(c)) return c;
        }
        return null;
    }
}
