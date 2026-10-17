using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Systems;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;
using OpenFPS.Materials;
using OpenFPS.Physics;
using OpenFPS.Environment;
using OpenFPS.Models;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --extent-walk [walks=creek,creek-out,gutter,shore,fountain,fountain-out,fire,oak,oak-out] [render] [out=DIR] [speed=1]
///               [rule=middle|pieces] [rain=60]
///
/// The walks away from a source face across the way they go, so the source is at one side. rule=middle plays the
/// extended sources as before 2026-10-16 (ExtendedSources.PlaceRule.Middle), for the before and after; rain= is how
/// many minutes of heavy rain Albany's drainage has had (60: its creeks well up).
///
/// Walks beside, along and away from the extended sources of the real maps at standing height (docs/RUNNING_WATER.md
/// 15): a creek of Albany's drainage lines, its gutter and fountain and fire pit on the city, a stretch of
/// Albany's pond shores. Every 0.5 m it reports what the client places there, through ClientAudioSystem over a
/// mixer that only records: the power of every voice of the walk's sources by the mixer's own law
/// (Loudness.RenderedGain, each voice at its share of its source), where it comes from (the bearing of their
/// power-weighted directions), how wide it is (twice the arc cosine of their resultant: two equal sources ±θ
/// give 2θ) and how many voices it took. A uniform line should be flat beside it.
///
/// render: the same walks through the game's own mixer (FMOD and Steam Audio, so copy libphonon.so and libfmod.so
/// beside the lab), captured, and every 0.5 m the level in each octave from 125 Hz to 8 kHz, the two ears'
/// coherence (IACC) in each, and the interaural delay as a bearing left or right of the way you walk. Writes
/// DIR/&lt;walk&gt;.csv and DIR/&lt;walk&gt;.wav.
/// </summary>
public static class ExtentWalkSpike
{
    private const float Step = 0.5f;

    /// <summary>This instrument's audio world's options, from the environment as the game's mixer starts with
    /// (rule= sets the wide sources' rule), shared with the mixers and the client it makes.</summary>
    private static readonly OpenFPS.Audio.Core.AudioWorldOptions LabOptions = OpenFPS.Audio.Core.AudioWorldOptions.FromEnvironment();

    /// <summary>A walk: the feet at every step, the way the walker faces, and which sources it is about.</summary>
    private sealed record Walk(string Name, string Map, string About, List<Vector3> Feet, Vector3 Heading, Func<string, bool> Source,
                               float WindMetresPerSecond = 1.5f);

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        // log: the game's own log lines too (its budgets giving voices up and taking them back).
        if (!args.Contains("log")) Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string outDir = Arg(args, "out=") ?? Path.Combine(Path.GetTempPath(), "openfps-extent-walk");
        Directory.CreateDirectory(outDir);
        var wanted = (Arg(args, "walks=") ?? "creek,creek-out,gutter,shore,fountain,fountain-out,fire").Split(',');
        bool render = args.Contains("render");
        float speed = ArgF(args, "speed=", render ? 0.5f : 1f);
        float rainMinutes = ArgF(args, "rain=", 60f);
        if (Arg(args, "rule=") is { } rule)
        {
            LabOptions.WideSources.Rule = rule == "middle" ? ExtendedSources.PlaceRule.Middle : ExtendedSources.PlaceRule.Pieces;
        }
        Console.WriteLine($"Extent walk: {(render ? "rendered through the game's mixer" : "the client's placing, summed by the mixer's law")}, {speed:F1} m/s, a row every {Step} m; " +
                          $"places by the {(LabOptions.WideSources.Rule == ExtendedSources.PlaceRule.Middle ? "old rule (held to the middle)" : "rule of 2026-10-16 (each piece as loud as what it holds)")}");

        int failures = 0;
        foreach (var map in new[] { "albany_or", "city" })
        {
            var names = wanted.Where(w => MapOf(w) == map).ToList();
            if (names.Count == 0) continue;
            if (!OpenFPS.AudioLab.Spikes.WalkPairSpike.Load(map, out var maps)) return 1;
            maps.TryGetMap(map, out var ecs, out _, out var grid, out _);
            var all = EntityDefinitionFactory.StaticDefinitions(ecs);
            var defs = all.Where(d => d.SoundEmitter.IsSynth && d.SoundEmitter.SoundId != null).ToList();
            float[]? ground = null;
            if (map == "albany_or")
            {
                // Heavy rain on the place's drainage, as GroundWaterSpike's scenes: the lines running.
                maps.GroundWater.Settle(map, 0f);
                for (int t = 1; t <= (int)(rainMinutes * 60f); t++) maps.GroundWater.Update(map, Rainfall.HeavyRate, 0.05f, 1f);
                ground = maps.GroundWater.WaterOf(map)!.Save();
            }
            float Floor(Vector3 near)
            {
                float y = PhysicsUtils.GetGroundHeight(ecs, grid, near + new Vector3(0f, 0.4f, 0f), out _);
                return y < -900f ? near.Y : y;
            }
            foreach (string name in names)
            {
                var walk = Make(name, defs, all, Floor);
                if (walk == null) { Console.WriteLine($"FAIL: no {name} walk on {map}"); failures++; continue; }
                var near = defs.Where(d => walk.Source(d.SoundEmitter.SoundId!) && walk.Feet.Any(f => Reach(d, f) < 150f)).ToList();
                Console.WriteLine();
                Console.WriteLine($"== {walk.Name}: {walk.About}; {walk.Feet.Count} steps, {near.Count} sources within 150 m");
                Console.WriteLine($"   from /tp {PlayerCoordinates.Format(walk.Feet[0])} to /tp {PlayerCoordinates.Format(walk.Feet[^1])}");
                if (render) Render(walk, near, ground, speed, outDir);
                else Headless(walk, near, ground, speed, outDir);
            }
        }
        return failures;
    }

    private static string MapOf(string walk) => walk is "creek" or "creek-out" or "shore" or "oak" or "oak-out" ? "albany_or" : "city";

    // ── The walks ───────────────────────────────────────────────────────────────────────────────

    private static Walk? Make(string name, List<EntityDefinition> defs, List<EntityDefinition> all, Func<Vector3, float> floor)
    {
        static bool GroundLine(string id) => id.StartsWith("flow:ground/", StringComparison.OrdinalIgnoreCase);
        switch (name)
        {
            case "creek":
            case "creek-out":
            {
                // The longest run of the drainage's creek voices end to end (not a mapped creek's), beside it 1.5 m off its
                // line. Heard as whole lines (2026-10-17), the run that holds the creek the walks of 2026-10-16 measured,
                // centred where they were.
                var creeks = defs.Where(d => d.SoundEmitter.SoundId!.StartsWith("flow:ground/creek/", StringComparison.OrdinalIgnoreCase)
                                             && !(d.Identity.PrefabId ?? "").StartsWith("waterway_", StringComparison.Ordinal)).ToList();
                float LengthOfPart(EntityDefinition d) => GroundChannels.TryParse(d.SoundEmitter.SoundId![5..], out var s) ? s.LengthMetres : 20f;
                var chain = creeks.Any(d => PointsOf(d) != null) ? ChainNear(creeks, LengthOfPart, MeasuredCreek) : LongestChain(creeks, LengthOfPart);
                if (chain.Count < 3 && chain.Sum(d => PointsOf(d) is { } l ? LengthOf(l.ToList()) : 0f) < 60f) return null;
                var line = Polyline(chain);
                float centre = PointsOf(chain[0]) != null ? AlongOf(line, MeasuredCreek) : 0.5f * LengthOf(line);
                if (name == "creek")
                {
                    var (feet, heading) = Beside(line, 1.5f, 60f, floor, centre);
                    return new Walk(name, "albany_or", $"along a creek of {chain.Count} drainage-line voices ({LengthOf(line):F0} m), 1.5 m off its line",
                                    feet, heading, GroundLine);
                }
                var (mid, along) = PointAt(line, centre);
                var side = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitY));
                var outFeet = new List<Vector3>();
                for (float d = 0.5f; d <= 60f + 1e-3f; d += Step)
                {
                    var p = mid + side * d;
                    outFeet.Add(p with { Y = floor(p with { Y = mid.Y }) });
                }
                return new Walk(name, "albany_or", "straight out from the middle of the same creek, 0.5 to 60 m, facing along it", outFeet, along, GroundLine);
            }
            case "oak":
            case "oak-out":
            {
                // Albany's Oak Creek as the map draws it (the water strips tools/gen_osm.py lays along OpenStreetMap's
                // line): beside its longest run 1.5 m out from its edge, and out from that run's middle. Every running
                // water near counts, the drainage's lines beside it too.
                var strips = all.Where(d => d.Identity.Name == "Oak Creek" && d.Identity.PrefabId == "water_surface" && d.Collider.Size.X > 0.5f).ToList();
                var chain = LongestChain(strips, d => d.Collider.Size.X);
                if (chain.Count < 3) return null;
                var line = Polyline(chain);
                float half = 0.5f * chain.Average(d => d.Collider.Size.Z);
                static bool Running(string id) => id.StartsWith("flow:", StringComparison.OrdinalIgnoreCase);
                if (name == "oak")
                {
                    var (feet, heading) = Beside(line, half + 1.5f, 60f, floor);
                    return new Walk(name, "albany_or", $"along Oak Creek as the map draws it ({chain.Count} strips, {LengthOf(line):F0} m, {2f * half:F1} m wide), 1.5 m out from its edge",
                                    feet, heading, Running);
                }
                var (mid, along) = PointAt(line, 0.5f * LengthOf(line));
                var side = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitY));
                var outFeet = new List<Vector3>();
                for (float d = half + 0.5f; d <= 60f + 1e-3f; d += Step)
                {
                    var p = mid + side * d;
                    outFeet.Add(p with { Y = floor(p with { Y = mid.Y }) });
                }
                return new Walk(name, "albany_or", "straight out from the middle of the same run of Oak Creek to 60 m from its line, facing along it", outFeet, along, Running);
            }
            case "shore":
            {
                // A whole outline heard as one source (2026-10-17): the pond the walks of 2026-10-16 measured, the same
                // 60 m of it, 2 m inland (its outline runs anticlockwise, the water on its left).
                var outlines = defs.Where(d => d.SoundEmitter.SoundId!.StartsWith("shore:", StringComparison.OrdinalIgnoreCase) && PointsOf(d) != null).ToList();
                if (outlines.Count > 0)
                {
                    var pond = outlines.MinBy(d => Vector3.Distance(new LinePath(PointsOf(d)!).Nearest(MeasuredShore with { Y = PointsOf(d)![0].Y }, out _) with { Y = 0f }, MeasuredShore))!;
                    var ring = PointsOf(pond)!.ToList();
                    var (around, aroundHeading) = Beside(ring, 2f, 60f, floor, AlongOf(ring, MeasuredShore));
                    return new Walk(name, "albany_or", $"along a pond's shore, its whole outline one source ({LengthOf(ring):F0} m round), 2 m back from the edge",
                                    around, aroundHeading, id => id.StartsWith("shore:", StringComparison.OrdinalIgnoreCase), WindMetresPerSecond: 8f);
                }
                var chain = LongestChain(defs.Where(d => d.SoundEmitter.SoundId!.StartsWith("shore:", StringComparison.OrdinalIgnoreCase)).ToList(),
                                         d => d.Collider.Size.X);
                if (chain.Count < 3) return null;
                var line = Polyline(chain);
                // Two metres inland: away from the water, which lies on each stretch's own +z.
                var water = Vector3.Transform(Vector3.UnitZ, chain[chain.Count / 2].Transform.Rotation);
                var (_, dir) = PointAt(line, 0.5f * LengthOf(line));
                float sign = Vector3.Dot(Vector3.Cross(dir, Vector3.UnitY), water) > 0f ? -1f : 1f;
                var (feet, heading) = Beside(line, 2f * sign, 60f, floor);
                return new Walk(name, "albany_or", $"along a pond's shore of {chain.Count} stretches ({LengthOf(line):F0} m), 2 m back from the edge",
                                feet, heading, id => id.StartsWith("shore:", StringComparison.OrdinalIgnoreCase), WindMetresPerSecond: 8f);
            }
            case "gutter":
            {
                var g = defs.FirstOrDefault(d => d.SoundEmitter.SoundId == "flow:gutter");
                if (g == null) return null;
                var axis = Vector3.Transform(Vector3.UnitX, g.Transform.Rotation);
                var side = -Vector3.Transform(Vector3.UnitZ, g.Transform.Rotation);
                var feet = new List<Vector3>();
                for (float a = -15f; a <= 15f + 1e-3f; a += Step)
                {
                    var p = g.Transform.Position + axis * a + side * 2f;
                    feet.Add(p with { Y = floor(p) });
                }
                return new Walk(name, "city", "along Foundry Street's gutter (15 m) in heavy rain, 2 m off it, from 7.5 m before it to 7.5 m past",
                                feet, axis, id => id == "flow:gutter");
            }
            case "fountain":
            case "fountain-out":
            {
                var taps = defs.Where(d => d.SoundEmitter.SoundId!.StartsWith("water:park_fountain/elm_park/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (taps.Count == 0) return null;
                var c = taps.Aggregate(Vector3.Zero, (s, d) => s + d.Transform.Position) / taps.Count;
                var feet = new List<Vector3>();
                if (name == "fountain")
                {
                    const float r = 7f;
                    int n = (int)MathF.Round(MathF.Tau * r / Step);
                    for (int i = 0; i <= n; i++)
                    {
                        float a = MathF.Tau * i / n;
                        var p = c + new Vector3(MathF.Sin(a) * r, 0f, -MathF.Cos(a) * r);
                        feet.Add(p with { Y = floor(p with { Y = 0.2f }) });
                    }
                    return new Walk(name, "city", "round Elm Park's fountain (11 m basin), 7 m from its middle, from the south going east",
                                    feet, Vector3.UnitX, id => id.StartsWith("water:", StringComparison.OrdinalIgnoreCase));
                }
                for (float d = 6f; d <= 40f + 1e-3f; d += Step)
                {
                    var p = c + new Vector3(0f, 0f, -d);
                    feet.Add(p with { Y = floor(p with { Y = 0.2f }) });
                }
                return new Walk(name, "city", "straight away south from Elm Park's fountain, 6 to 40 m from its middle, facing east",
                                feet, Vector3.UnitX, id => id.StartsWith("water:", StringComparison.OrdinalIgnoreCase));
            }
            case "fire":
            {
                var pit = defs.FirstOrDefault(d => d.SoundEmitter.SoundId!.StartsWith("fire:", StringComparison.OrdinalIgnoreCase));
                if (pit == null) return null;
                var feet = new List<Vector3>();
                for (float d = 1.5f; d <= 30f + 1e-3f; d += Step)
                {
                    var p = pit.Transform.Position + new Vector3(-d, 0f, 0f);
                    feet.Add(p with { Y = floor(p with { Y = 0.2f }) });
                }
                return new Walk(name, "city", "straight away west from 58 Alder Street's fire pit, 1.5 to 30 m, facing north",
                                feet, Vector3.UnitZ, id => id.StartsWith("fire:", StringComparison.OrdinalIgnoreCase));
            }
        }
        return null;
    }

    /// <summary>The longest run of straight stretches laid end to end (each along its own x, centred on its
    /// position), in order along it.</summary>
    private static List<EntityDefinition> LongestChain(List<EntityDefinition> parts, Func<EntityDefinition, float> lengthOf)
    {
        var ends = parts.Select(d =>
        {
            // A source laid along a line of its own (SoundEmitterComponent.Line): that line's ends.
            if (PointsOf(d) is { } line) return (Def: d, A: line[0], B: line[^1]);
            var axis = Vector3.Transform(Vector3.UnitX, d.Transform.Rotation);
            float half = 0.5f * lengthOf(d);
            return (Def: d, A: d.Transform.Position - axis * half, B: d.Transform.Position + axis * half);
        }).ToList();
        const float Join = 4f;
        var best = new List<EntityDefinition>();
        var used = new HashSet<int>();
        for (int s = 0; s < ends.Count; s++)
        {
            if (used.Contains(s)) continue;
            var chain = new LinkedList<int>();
            chain.AddFirst(s);
            used.Add(s);
            // Grow from either end while a stretch's end meets it.
            foreach (bool forward in new[] { true, false })
            {
                Vector3 tip = forward ? ends[s].B : ends[s].A;
                while (true)
                {
                    int next = -1; bool flip = false; float bestD = Join;
                    for (int k = 0; k < ends.Count; k++)
                    {
                        if (used.Contains(k)) continue;
                        float da = Flat(ends[k].A - tip), db = Flat(ends[k].B - tip);
                        if (da < bestD) { bestD = da; next = k; flip = false; }
                        if (db < bestD) { bestD = db; next = k; flip = true; }
                    }
                    if (next < 0) break;
                    used.Add(next);
                    if (forward) chain.AddLast(next); else chain.AddFirst(next);
                    // Its far end is the new tip.
                    tip = forward ? (flip ? ends[next].A : ends[next].B) : (flip ? ends[next].B : ends[next].A);
                }
            }
            // Stretches laid straight, the most of them (as before 2026-10-17); lines of their own, the longest.
            float Long(IEnumerable<EntityDefinition> c) => c.Sum(d => PointsOf(d) is { } l ? LengthOf(l.ToList()) : lengthOf(d));
            var defs = chain.Select(i => ends[i].Def).ToList();
            bool lines = defs.Any(d => PointsOf(d) != null);
            if (best.Count == 0 || (lines ? Long(defs) > Long(best) : defs.Count > best.Count)) best = defs;
        }
        return best;

        static float Flat(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);
    }

    /// <summary>The run's line: through every stretch's middle, in order.</summary>
    private static List<Vector3> Polyline(List<EntityDefinition> chain)
    {
        // Stretches laid straight: through each one's middle. Lines of their own: their points, each turned to go on
        // from where the last ended.
        if (chain.All(d => PointsOf(d) == null)) return chain.Select(d => d.Transform.Position).ToList();
        var line = new List<Vector3>();
        foreach (var d in chain)
        {
            var points = PointsOf(d) ?? new[] { d.Transform.Position };
            if (line.Count > 0 && Vector3.Distance(points[^1], line[^1]) < Vector3.Distance(points[0], line[^1])) points = points.Reverse().ToArray();
            if (line.Count == 0 && chain.Count > 1 && PointsOf(chain[1]) is { } next
                && MathF.Min(Vector3.Distance(points[0], next[0]), Vector3.Distance(points[0], next[^1])) < MathF.Min(Vector3.Distance(points[^1], next[0]), Vector3.Distance(points[^1], next[^1])))
                points = points.Reverse().ToArray();
            line.AddRange(points);
        }
        return line;
    }

    /// <summary>The middle of the creek the extent walks of 2026-10-16 measured on Albany (map metres): the drainage's
    /// creek beside which the stretches rose and fell 10 dB every 20 m.</summary>
    private static readonly Vector3 MeasuredCreek = new(901.9f, 0f, -1281.0f);

    /// <summary>The middle of the pond's shore the walks of 2026-10-16 measured (map metres).</summary>
    private static readonly Vector3 MeasuredShore = new(-401.7f, 0f, 1344.8f);

    /// <summary>The run of parts end to end that passes nearest a point.</summary>
    private static List<EntityDefinition> ChainNear(List<EntityDefinition> parts, Func<EntityDefinition, float> lengthOf, Vector3 near)
    {
        var nearest = parts.MinBy(d => Flat(PointsOf(d) is { } l ? new LinePath(l).Nearest(near with { Y = l[0].Y }, out _) - near : d.Transform.Position - near))!;
        var rest = new List<EntityDefinition>(parts);
        // Grow from it: the longest chain that holds it.
        var chain = LongestChain(rest, lengthOf);
        while (!chain.Contains(nearest) && rest.Count > 1)
        {
            rest.RemoveAll(chain.Contains);
            chain = LongestChain(rest, lengthOf);
        }
        return chain.Contains(nearest) ? chain : new List<EntityDefinition> { nearest };

        static float Flat(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);
    }

    /// <summary>How far along a polyline its point nearest <paramref name="p"/> is, m.</summary>
    private static float AlongOf(List<Vector3> line, Vector3 p)
    {
        new LinePath(line.Select(q => q with { Y = 0f }).ToArray()).Nearest(p with { Y = 0f }, out float along);
        return along + 0.5f * LengthOf(line);
    }

    /// <summary>A source's own line in the world, or null.</summary>
    private static Vector3[]? PointsOf(EntityDefinition d)
        => d.SoundEmitter.Line is { Length: >= 2 } l ? l.Select(p => d.Transform.Position + Vector3.Transform(p, d.Transform.Rotation)).ToArray() : null;

    /// <summary>How near a source comes to a point: its line's nearest point, or its middle.</summary>
    private static float Reach(EntityDefinition d, Vector3 to)
        => PointsOf(d) is { } line ? Vector3.Distance(new LinePath(line).Nearest(to, out _), to) : Vector3.Distance(d.Transform.Position, to);

    private static float LengthOf(List<Vector3> line)
    {
        float l = 0f;
        for (int i = 1; i < line.Count; i++) l += Vector3.Distance(line[i - 1], line[i]);
        return l;
    }

    /// <summary>The point <paramref name="s"/> m along the line and the way it runs there (flat).</summary>
    private static (Vector3 At, Vector3 Along) PointAt(List<Vector3> line, float s)
    {
        for (int i = 1; i < line.Count; i++)
        {
            float seg = Vector3.Distance(line[i - 1], line[i]);
            if (s <= seg || i == line.Count - 1)
            {
                var d = line[i] - line[i - 1];
                var flat = Vector3.Normalize(d with { Y = 0f });
                return (line[i - 1] + d * Math.Clamp(s / MathF.Max(1e-3f, seg), 0f, 1f), flat);
            }
            s -= seg;
        }
        return (line[^1], Vector3.UnitX);
    }

    /// <summary>Feet every step along the middle <paramref name="metres"/> of a line, <paramref name="off"/> m to
    /// its right (negative: left), on the floor there.</summary>
    private static (List<Vector3> Feet, Vector3 Heading) Beside(List<Vector3> line, float off, float metres, Func<Vector3, float> floor,
                                                                float centre = float.NaN)
    {
        float total = LengthOf(line);
        float from = Math.Clamp((float.IsNaN(centre) ? 0.5f * total : centre) - 0.5f * metres, 0f, MathF.Max(0f, total - metres));
        var feet = new List<Vector3>();
        for (float s = from; s <= MathF.Min(total, from + metres) + 1e-3f; s += Step)
        {
            var (at, along) = PointAt(line, s);
            var right = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitY));
            var p = at + right * off;
            feet.Add(p with { Y = floor(p with { Y = at.Y }) });
        }
        return (feet, PointAt(line, from + 0.5f * metres).Along);
    }

    // ── Through the client's placing ────────────────────────────────────────────────────────────

    /// <summary>What the client hands the mixer, and nothing played.</summary>
    private sealed class LabMixer : IAudioProvider
    {
        public readonly Dictionary<int, SpatialEmitter> Latest = new();
        public readonly HashSet<int> Live = new();
        public readonly Dictionary<int, ExtendedSources.LineCut> Pieces = new();
        public void PlanPieces(int voiceId, ExtendedSources.LineCut cut) => Pieces[voiceId] = cut;
        public bool Initialize() => true;
        public void Update() { }
        public void UpdateListener(Vector3 p, Quaternion r, Vector3 v, int region) { }
        public void UpdateShelter(float f) { }
        public void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes) { }
        public void SetAcousticMap(AcousticMap map) { }
        public void PlaySpatialSound(SpatialEmitter e) { Latest[e.EntityId] = e; Live.Add(e.EntityId); }
        public void UpdateSpatialAttributes(SpatialEmitter e) => Latest[e.EntityId] = e;
        public void SetAcousticPath(int id, AcousticPathData p) { }
        public void SetSimulatedReverbDecay(float ms, float enclosure, float hf, float lf) { }
        public void SetAirTemperature(float c) { }
        public float MixerLoad => 0f;
        public int SpatialVoicesFree => 160;
        public void ReviveEngine(int id) { }
        public bool FadeOutEngine(int id) => true;
        public bool FadeOutVoice(int id) => true;
        public void CancelVoiceFade(int id) { }
        public void StopSound(int id) { Live.Remove(id); Latest.Remove(id); }
        public bool IsPlaying(int id) => Live.Contains(id);
        public Vector3 GetSoundPosition(int id) => Latest.TryGetValue(id, out var e) ? e.Position : Vector3.Zero;
        public IEnumerable<int> GetActiveSpatialSoundIds() => Live.ToList();
        public void Preload(string id) { }
        public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate) => true;
        public void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume) { }
        public IReadOnlyList<VoiceLevel> LoudestVoices(int count) => Array.Empty<VoiceLevel>();
        public IReadOnlyList<string> OutputDevices() => Array.Empty<string>();
        public IReadOnlyList<string> InputDevices() => Array.Empty<string>();
        public bool SetOutputDevice(string name) => true;
        public void StartDiagnosticSound() { }
        public void SetDiagnosticPosition(Vector3 p) { }
        public void StopDiagnosticSound() { }
        public void Dispose() { }
    }

    private sealed class Rig
    {
        public required AudioEngineFacade Facade;
        public required ClientWorldState World;
        public required LocalPlayerState Player;
        public required ClientAudioSystem Audio;
    }

    private static Rig MakeRig(IAudioProvider provider, Func<double> now, bool prewarm, string? sounds)
    {
        var facade = new AudioEngineFacade(provider, options: LabOptions);
        facade.InitializeForTest(sounds);
        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        world.Clock = facade.Clock;
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        if (sounds != null) mapping.Initialize(sounds);
        var audio = new ClientAudioSystem(facade, mapping, player, now, prewarm: prewarm);
        return new Rig { Facade = facade, World = world, Player = player, Audio = audio };
    }

    private static void Wet(Rig rig, float[]? ground, float wind)
    {
        // Heavy rain run off and held, its sound not heard: the water heard, not the rain on everything else.
        rig.Facade.Weather.Runoff.Settle(Rainfall.HeavyRate);
        rig.Facade.Weather.Runoff.Held = true;
        if (ground != null)
        {
            rig.Facade.Weather.Ground.Held = false;
            rig.Facade.Weather.Ground.Load(ground);
            rig.Facade.Weather.Ground.Held = true;
        }
        rig.Facade.Weather.Wind = WindWeather.Steady(wind, 250f, 0.2f);
        rig.World.UpdateAtmosphere(new WorldStateUpdate
        {
            Temperature = 14f, Humidity = 0.8f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f,
            PrecipitationIntensity = 0f, RainRateMmPerHour = 0f,
        });
    }

    /// <summary>For running water: what its lines near the walk carry now, against what their beds were made for.</summary>
    private static void Flows(Rig rig, Walk walk, List<EntityDefinition> sources)
    {
        var flows = new List<(float Now, float Made)>();
        foreach (var d in sources)
        {
            string id = d.SoundEmitter.SoundId!;
            if (!id.StartsWith("flow:", StringComparison.OrdinalIgnoreCase)) continue;
            if (walk.Feet.Min(f => Reach(d, f)) > 30f) continue;
            var spec = ModelLibrary.Presets.Flow(id[5..]);
            flows.Add((spec.FlowNow(rig.Facade.Weather), spec.ReferenceFlow));
        }
        if (flows.Count > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"   the water within 30 m of the walk: {flows.Min(f => f.Now):F2} to {flows.Max(f => f.Now):F2} L/s ({flows.Count} voices; their beds made for {flows.Min(f => f.Made):F1} to {flows.Max(f => f.Made):F1} L/s)"));
    }

    private static void Face(LocalPlayerState player, Vector3 heading)
    {
        float yaw = MathF.Atan2(heading.X, heading.Z);
        player.Yaw = yaw;
        player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }

    private static void Headless(Walk walk, List<EntityDefinition> sources, float[]? ground, float speed, string outDir)
    {
        double now = 1.0;
        var mixer = new LabMixer();
        var rig = MakeRig(mixer, () => now, prewarm: false, sounds: null);
        Wet(rig, ground, walk.WindMetresPerSecond);
        foreach (var d in sources) rig.World.RegisterDefinition(d);
        Flows(rig, walk, sources);
        Face(rig.Player, walk.Heading);
        int framesPerStep = Math.Max(1, (int)MathF.Round(Step / speed * (float)ClientAudioSystem.UpdateHz));
        var update = new List<double>();
        void Tick()
        {
            now += 1.0 / ClientAudioSystem.UpdateHz;
            var sw = Stopwatch.StartNew();
            rig.Audio.Update(rig.World.GetSnapshot());
            update.Add(sw.Elapsed.TotalMilliseconds);
            rig.Facade.PumpForTest();
            rig.Facade.PumpForTest();
        }
        // Stood at the start long enough for every voice to be admitted and spread.
        rig.Player.Position = walk.Feet[0];
        for (int i = 0; i < 4 * (int)ClientAudioSystem.UpdateHz; i++) Tick();
        update.Clear();

        var rows = new List<(Vector3 Feet, AudioWorld.Together H)>();
        for (int s = 0; s < walk.Feet.Count; s++)
        {
            var from = s == 0 ? walk.Feet[0] : walk.Feet[s - 1];
            for (int f = 1; f <= framesPerStep; f++)
            {
                rig.Player.Position = Vector3.Lerp(from, walk.Feet[s], f / (float)framesPerStep);
                Tick();
            }
            var ear = walk.Feet[s] + new Vector3(0f, LocalPlayerState.StandingEyeHeight, 0f);
            rows.Add((walk.Feet[s], AudioWorld.HeardTogether(mixer.Latest, mixer.Live, mixer.Pieces, walk.Source, ear, LabOptions.WideSources)));
        }
        rig.Facade.Dispose();

        double mean = 10 * Math.Log10(rows.Average(r => r.H.Power));
        var csv = new StringBuilder("metres,east,north,height,level_db,bearing_deg,width_deg,voices,absolute_db\n");
        Console.WriteLine("   metres   level dB   from °   wide °   voices");
        for (int s = 0; s < rows.Count; s++)
        {
            var (feet, h) = rows[s];
            double db = 10 * Math.Log10(Math.Max(1e-30, h.Power)) - mean;
            csv.Append(CultureInfo.InvariantCulture, $"{s * Step:F1},{feet.X:F2},{feet.Z:F2},{feet.Y:F2},{db:F2},{h.BearingDegrees:F1},{h.WidthDegrees:F1},{h.Voices},{db + mean:F2}\n");
            if (s % 4 == 0 || s == rows.Count - 1)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   {s * Step,6:F1}   {db,8:F1}   {h.BearingDegrees,6:F0}   {h.WidthDegrees,6:F0}   {h.Voices,6}"));
        }
        var levels = rows.Select(r => 10 * Math.Log10(Math.Max(1e-30, r.H.Power))).ToList();
        update.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"   level: mean {mean:F1} dB (the voices' gain squared, for before and after), highest {levels.Max() - mean:F1} dB, lowest {levels.Min() - mean:F1} dB, swing {levels.Max() - levels.Min():F1} dB; " +
            $"voices at most {rows.Max(r => r.H.Voices)}; width {rows.Min(r => r.H.WidthDegrees):F0} to {rows.Max(r => r.H.WidthDegrees):F0}°; " +
            $"ClientAudioSystem.Update median {update[update.Count / 2]:F2} ms, 99th {update[(int)(0.99 * (update.Count - 1))]:F2} ms"));
        File.WriteAllText(Path.Combine(outDir, walk.Name + ".csv"), csv.ToString());
    }

    // ── Through the game's mixer ────────────────────────────────────────────────────────────────

    private static void Render(Walk walk, List<EntityDefinition> sources, float[]? ground, float speed, string outDir)
    {
        string capture = Path.Combine(outDir, walk.Name + ".wav");
        System.Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", capture);
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var provider = new FmodAudioProvider { Options = LabOptions };
        var clock = Stopwatch.StartNew();
        var rig = MakeRig(provider, () => clock.Elapsed.TotalSeconds, prewarm: false, sounds: LabPaths.Sounds());
        if (!rig.Facade.IsInitialized) { Console.WriteLine("FAIL: the mixer did not start"); return; }
        provider.EarWindEnabled = false;
        Wet(rig, ground, walk.WindMetresPerSecond);
        // The ground under the walk, as GroundWaterSpike's scenes stand on.
        var centre = walk.Feet.Aggregate(Vector3.Zero, (a, f) => a + f) / walk.Feet.Count;
        rig.World.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1, Type = EntityType.StaticObject,
            Transform = new Transform { Position = centre - new Vector3(0f, 0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(400f, 1f, 400f), IsSolid = true },
            Material = new MaterialComponent { Material = "Grass" },
        });
        foreach (var d in sources) rig.World.RegisterDefinition(d);
        Flows(rig, walk, sources);
        Face(rig.Player, walk.Heading);
        int mostVoices = 0, flat = 0, leastFree = int.MaxValue;
        var playing = new List<(double At, int Voices)>();
        void Pump(double seconds, Func<double, Vector3>? at = null)
        {
            double start = clock.Elapsed.TotalSeconds;
            int frame = 0;
            while (clock.Elapsed.TotalSeconds - start < seconds)
            {
                if (at != null) rig.Player.Position = at(clock.Elapsed.TotalSeconds - start);
                rig.Audio.Update(rig.World.GetSnapshot());
                rig.Facade.PumpForTest();
                if (at != null && ++frame % 10 == 0)
                {
                    var census = provider.VoiceCensus();
                    playing.Add((clock.Elapsed.TotalSeconds, census.Active));
                    mostVoices = Math.Max(mostVoices, census.Active);
                    flat = Math.Max(flat, census.NoHrtf);
                    leastFree = Math.Min(leastFree, census.BinauralFree);
                }
                Thread.Sleep(4);
            }
        }
        rig.Player.Position = walk.Feet[0];
        Pump(6.0);
        double walkStart = clock.Elapsed.TotalSeconds;
        double stepSeconds = Step / speed;
        Pump(walk.Feet.Count * stepSeconds, t =>
        {
            double s = t / stepSeconds;
            int i = Math.Clamp((int)s, 0, walk.Feet.Count - 1), j = Math.Min(i + 1, walk.Feet.Count - 1);
            return Vector3.Lerp(walk.Feet[i], walk.Feet[j], (float)(s - i));
        });
        Pump(0.5);
        Console.WriteLine($"   mixer: at most {mostVoices} voices playing, {flat} direct voice(s) without HRTF at most, {provider.VoiceCensus().Refused} refused for want of a binaural voice, {leastFree} binaural voices free at least");
        rig.Facade.Dispose();

        // The mixer's voices at a moment of the walk: the census taken nearest it.
        int Playing(double at) => playing.Count == 0 ? 0 : playing.MinBy(p => Math.Abs(p.At - at)).Voices;
        var (l, r, rate) = ReadStereo(capture);
        int Sample(double seconds) => Math.Clamp((int)(seconds * rate), 0, l.Length);
        double[] octaves = { 125, 250, 500, 1000, 2000, 4000, 8000 };
        var csv = new StringBuilder("metres,level_db," + string.Join(",", octaves.Select(f => $"db_{f:F0}")) + "," + string.Join(",", octaves.Select(f => $"iacc_{f:F0}")) + ",side_deg,ild_db,voices_playing\n");
        Console.WriteLine("   metres   level dB   " + string.Join(" ", octaves.Select(f => $"{(f < 1000 ? f.ToString("F0") : (f / 1000).ToString("F0") + "k"),5}")) + "   IACC 500 1k 2k   side °");
        var levels = new List<double>();
        var rows = new List<string>();
        for (int s = 0; s < walk.Feet.Count; s++)
        {
            int a = Sample(walkStart + s * stepSeconds - stepSeconds / 2), b = Sample(walkStart + s * stepSeconds + stepSeconds / 2);
            if (b - a < rate / 10) continue;
            var wl = l[a..b]; var wr = r[a..b];
            double total = 10 * Math.Log10(Math.Max(1e-30, (Energy(wl) + Energy(wr)) / (2.0 * wl.Length)));
            var bandDb = new double[octaves.Length];
            var iacc = new double[octaves.Length];
            for (int k = 0; k < octaves.Length; k++)
            {
                if (octaves[k] * 1.414 > 0.45 * rate) { bandDb[k] = double.NaN; iacc[k] = double.NaN; continue; }
                var bl = Band(wl, octaves[k], rate); var br = Band(wr, octaves[k], rate);
                bandDb[k] = 10 * Math.Log10(Math.Max(1e-30, (Energy(bl) + Energy(br)) / (2.0 * bl.Length)));
                iacc[k] = Iacc(bl, br, rate);
            }
            var (side, ild) = Side(wl, wr, rate);
            levels.Add(total);
            csv.Append(CultureInfo.InvariantCulture, $"{s * Step:F1},{total:F2},{string.Join(",", bandDb.Select(v => v.ToString("F2", CultureInfo.InvariantCulture)))},{string.Join(",", iacc.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)))},{side:F1},{ild:F2},{Playing(walkStart + s * stepSeconds)}\n");
            if (s % 4 == 0 || s == walk.Feet.Count - 1)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"   {s * Step,6:F1}   {total,8:F1}   {string.Join(" ", bandDb.Select(v => v.ToString("F0", CultureInfo.InvariantCulture).PadLeft(5)))}   {iacc[2],4:F2} {iacc[3],4:F2} {iacc[4],4:F2}   {side,6:F0}"));
        }
        if (levels.Count > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"   level (dBFS): mean {10 * Math.Log10(levels.Average(v => Math.Pow(10, v / 10))):F1}, highest {levels.Max():F1}, lowest {levels.Min():F1}, swing {levels.Max() - levels.Min():F1} dB (1 s windows hold the water's own unevenness too)"));
        File.WriteAllText(Path.Combine(outDir, walk.Name + ".render.csv"), csv.ToString());
        Console.WriteLine($"   wrote {capture}");
    }

    private static double Energy(float[] x)
    {
        double e = 0;
        foreach (float v in x) e += v * (double)v;
        return e;
    }

    /// <summary>An octave about <paramref name="centre"/>: two RBJ band-pass sections at Q √2.</summary>
    private static float[] Band(float[] x, double centre, int rate)
    {
        var y = (float[])x.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            double w = 2 * Math.PI * centre / rate, q = Math.Sqrt(2), alpha = Math.Sin(w) / (2 * q);
            double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w), a2 = 1 - alpha;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double v = (b0 * y[i] + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = y[i]; y2 = y1; y1 = v;
                y[i] = (float)v;
            }
        }
        return y;
    }

    /// <summary>The largest normalised cross-correlation of the two ears within a millisecond either way.</summary>
    private static double Iacc(float[] l, float[] r, int rate)
    {
        int max = rate / 1000;
        double ll = Energy(l), rr = Energy(r), best = 0;
        if (ll <= 1e-30 || rr <= 1e-30) return double.NaN;
        for (int lag = -max; lag <= max; lag++)
        {
            double s = 0;
            for (int i = Math.Max(0, -lag); i < l.Length && i + lag < r.Length; i++) s += l[i] * (double)r[i + lag];
            best = Math.Max(best, Math.Abs(s) / Math.Sqrt(ll * rr));
        }
        return best;
    }

    /// <summary>Which side it comes from: the interaural delay of the 500 Hz-1.5 kHz band at its correlation's
    /// peak as an angle off straight ahead (Woodworth's 0.66 ms at the side; positive right), and the level
    /// difference (right over left, dB).</summary>
    private static (double Degrees, double IldDb) Side(float[] l, float[] r, int rate)
    {
        var bl = Band(l, 866, rate); var br = Band(r, 866, rate);
        int max = rate / 1000;
        double best = double.NegativeInfinity; int at = 0;
        for (int lag = -max; lag <= max; lag++)
        {
            double s = 0;
            for (int i = Math.Max(0, -lag); i < bl.Length && i + lag < br.Length; i++) s += bl[i] * (double)br[i + lag];
            if (s > best) { best = s; at = lag; }
        }
        // The right ear late (lag > 0): the sound reached the left first.
        double itd = -at / (double)rate;
        double deg = Math.Asin(Math.Clamp(itd / 0.00066, -1.0, 1.0)) * 180.0 / Math.PI;
        double ild = 10 * Math.Log10(Math.Max(1e-30, Energy(r)) / Math.Max(1e-30, Energy(l)));
        return (deg, ild);
    }

    internal static (float[] L, float[] R, int Rate) ReadStereo(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(12);
        int sr = 48000, ch = 2, bits = 16;
        while (br.BaseStream.Position < br.BaseStream.Length - 8)
        {
            string id = new string(br.ReadChars(4)); int size = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16(); ch = br.ReadInt16(); sr = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                long bytes = size > 0 ? size : br.BaseStream.Length - br.BaseStream.Position;
                int frames = (int)(bytes / (ch * bits / 8));
                var L = new float[frames]; var R = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float a = bits == 32 ? br.ReadSingle() : br.ReadInt16() / 32768f;
                    float b = ch > 1 ? (bits == 32 ? br.ReadSingle() : br.ReadInt16() / 32768f) : a;
                    for (int k = 2; k < ch; k++) br.ReadBytes(bits / 8);
                    L[i] = a; R[i] = b;
                }
                return (L, R, sr);
            }
            else br.ReadBytes(size);
        }
        return (Array.Empty<float>(), Array.Empty<float>(), sr);
    }

    private static string? Arg(string[] args, string prefix) => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static float ArgF(string[] args, string prefix, float fallback)
        => float.TryParse(Arg(args, prefix), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}
