using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Environment;
using OpenFPS.Materials;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Geometry;
using OpenFPS.Server.Systems;
using OpenFPS.Sound;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>The people replay runs alone: it steps the server's systems, whose statics a parallel class could share.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PeopleReplayCollection
{
    public const string Name = "People replays";
}

/// <summary>
/// Where the people replay's stored copies live (OpenFPS.Tests/PeopleBoundary) and how they change: only in
/// the commit that adds the guard, by running the test with OPENFPS_PEOPLE_WRITE=1, and after that never by a
/// move: a law that changes a stored line is a bug to fix, not a baseline to refresh
/// (docs/ENGINE_PACKAGES.md, stage 10). OPENFPS_PEOPLE_DUMP=dir writes readable copies beside the run.
/// Each store starts with the maths it was made on (CrossMachine.MathsFingerprint). On other maths (GitHub's
/// runner, or OPENFPS_PEOPLE_FOREIGN=1) an hour's last bits drift and a person's choice flips at a near tie, so a
/// timed store is compared exactly up to the tick the two runs part, which must be at least
/// <see cref="ForeignHorizonSeconds"/> in.
/// </summary>
public static class PeopleStore
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    public static string PathOf(params string[] parts)
        => Path.Combine(new[] { RepoRoot(), "OpenFPS.Tests", "PeopleBoundary" }.Concat(parts).ToArray());

    public static bool Writing => System.Environment.GetEnvironmentVariable("OPENFPS_PEOPLE_WRITE") == "1";

    public static bool Foreign => System.Environment.GetEnvironmentVariable("OPENFPS_PEOPLE_FOREIGN") == "1";

    /// <summary>How long a run on other maths must match before it may part: the 2026-10-19 runner matched
    /// Alex's whole hour and parted from the doors' changes somewhere before the hour's last two seconds.</summary>
    public const double ForeignHorizonSeconds = 300;

    private const string MathsLine = "# maths ";

    private static readonly Lazy<string> MathsOnce = new(CrossMachine.MathsFingerprint);

    public static string? DumpFolder => System.Environment.GetEnvironmentVariable("OPENFPS_PEOPLE_DUMP") is { Length: > 0 } dir ? dir : null;

    public static string Read(string path)
    {
        if (!File.Exists(path)) return "";
        if (!path.EndsWith(".gz", StringComparison.Ordinal)) return File.ReadAllText(path).Replace("\r\n", "\n");
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!path.EndsWith(".gz", StringComparison.Ordinal)) { File.WriteAllText(path, text.Replace("\r\n", "\n")); return; }
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.SmallestSize);
        var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
        gz.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Writes a readable copy of what a run recorded, for reading by eye.</summary>
    public static void Dump(string name, string text)
    {
        if (DumpFolder is not { } dir) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text.Replace("\t", "  "));
    }

    /// <summary>
    /// Compares a run with its stored copy, or stores it when writing; the first line that differs is shown.
    /// A timed store names how its lines are read on other maths: each line's group (a door, or the whole store)
    /// and its tick, null for a line that carries its group's last tick (a closing door's steps).
    /// </summary>
    public static void Check(string path, string now, string what, Action<string>? log = null,
                             double secondsPerTick = 0, Func<string, (string Group, int? Tick)>? timed = null)
    {
        if (Writing) { Write(path, MathsLine + MathsOnce.Value + "\n" + now); return; }
        string all = Read(path);
        Assert.False(all.Length == 0, $"no stored {what} at {path}: run with OPENFPS_PEOPLE_WRITE=1 to make it");
        string maths = "", stored = all;
        if (all.StartsWith(MathsLine, StringComparison.Ordinal))
        {
            int end = all.IndexOf('\n');
            (maths, stored) = (all[MathsLine.Length..end], all[(end + 1)..]);
        }
        var a = stored.TrimEnd('\n').Split('\n');
        var b = now.TrimEnd('\n').Split('\n');
        if (timed != null && (maths != MathsOnce.Value || Foreign))
        {
            int? parted = Parting(a, b, timed);
            string whose = Foreign ? "OPENFPS_PEOPLE_FOREIGN" : maths.Length == 0 ? "none stored" : maths + " stored";
            log?.Invoke($"{what}: other maths ({whose}; {MathsOnce.Value} here), "
                + (parted is { } p ? $"exact until the runs part at tick {p} ({p * secondsPerTick:F1} s)" : "exact throughout"));
            if (parted is { } t)
                Assert.True(t * secondsPerTick >= ForeignHorizonSeconds,
                    $"{what} parted from its stored copy at tick {t} ({t * secondsPerTick:F1} s), before the "
                    + $"{ForeignHorizonSeconds} s other maths may drift for. If this is a move changing a law, it is a bug.");
            return;
        }
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] == b[i]) continue;
            Assert.Fail($"{what} changed from its stored copy at line {i + 1} of {a.Length} (now {b.Length}).\n"
                + $"  stored: {a[i]}\n  now:    {b[i]}\n"
                + (i > 0 ? $"  before: {a[i - 1]}\n" : "")
                + "If this is a move changing a law, it is a bug: fix the law, not the store.");
        }
        if (a.Length != b.Length)
            Assert.Fail($"{what} changed from its stored copy: {a.Length} lines stored, {b.Length} now. First extra: "
                + (b.Length > a.Length ? b[a.Length] : a[b.Length]));
    }

    /// <summary>The earliest tick at which two runs' lines differ, group by group; null when they agree.</summary>
    private static int? Parting(string[] a, string[] b, Func<string, (string Group, int? Tick)> timed)
    {
        static Dictionary<string, List<(int Tick, string Line)>> Groups(string[] lines, Func<string, (string Group, int? Tick)> timed)
        {
            var groups = new Dictionary<string, List<(int, string)>>();
            var last = new Dictionary<string, int>();
            foreach (string line in lines)
            {
                var (group, tick) = line.StartsWith('#') ? ("#", 0) : timed(line);
                int at = tick ?? last.GetValueOrDefault(group);
                last[group] = at;
                if (!groups.TryGetValue(group, out var list)) groups[group] = list = new();
                list.Add((at, line));
            }
            return groups;
        }
        var ga = Groups(a, timed);
        var gb = Groups(b, timed);
        int? parted = null;
        foreach (string group in ga.Keys.Union(gb.Keys))
        {
            var x = ga.GetValueOrDefault(group) ?? new();
            var y = gb.GetValueOrDefault(group) ?? new();
            int i = 0;
            while (i < x.Count && i < y.Count && x[i].Line == y[i].Line) i++;
            if (i == x.Count && i == y.Count) continue;
            int t = Math.Min(i < x.Count ? x[i].Tick : int.MaxValue, i < y.Count ? y[i].Tick : int.MaxValue);
            parted = Math.Min(parted ?? int.MaxValue, t);
        }
        return parted;
    }
}

/// <summary>
/// The people replay (docs/ENGINE_PACKAGES.md, stage 10): an hour of the city at a cold 23:00 with
/// CharacterSystem at pace 6 and parking every twenty seconds, stepped as NpcDoorTests' city hour is
/// (vehicles, characters, speech, doors, parents, then occupancy so riding is exercised), with the wall clock
/// replaced by the tick. It records the places HauntFinder finds, every door's manner and GoingIn examples,
/// Alex's ViewOf and body every tenth of a second, every door whose target, openness or hand changes, and every
/// line said; then ten minutes with a player on Market Square and Alex kept there, so his asking and idle lines
/// are heard. Positions are centimetres and angles degrees in the stored lines. Seeds: the map's own
/// StreetLife and vehicles, CharacterSystem's 20261006, PedestrianSpeech's 20261019.
/// </summary>
[Collection(PeopleReplayCollection.Name)]
public class PeopleReplayTests
{
    private const float Dt = 1f / 30f;
    private const string Map = "city";
    private const double HourSeconds = 3600, SquareSeconds = 600;
    private const double CaptureAt = 4.0;
    private const int CharacterSeed = 20261006, SpeechSeed = 20261019;

    private readonly ITestOutputHelper _o;

    public PeopleReplayTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    private sealed record CityDay(string Haunts, string Doors, string Alex, string DoorEvents, string Speech,
                                  double Seconds);

    [Fact]
    public void TheCityDayAndTheSquareSayWhatTheyStored()
    {
        ResetVoices();
        var clock = Stopwatch.StartNew();
        var day = RunCityDay();
        clock.Stop();
        _o.WriteLine($"the hour: {day.Seconds:F1} s simulated in {clock.Elapsed.TotalSeconds:F1} s, "
                   + $"{day.Alex.Count(c => c == '\n')} Alex rows, {day.DoorEvents.Count(c => c == '\n')} door rows, "
                   + $"{day.Speech.Count(c => c == '\n')} say rows");

        PeopleStore.Dump("haunts.txt", day.Haunts);
        PeopleStore.Dump("doors.txt", day.Doors);
        PeopleStore.Dump("alex.txt", day.Alex);
        PeopleStore.Dump("door-events.txt", day.DoorEvents);
        PeopleStore.Dump("speech.txt", day.Speech);

        clock.Restart();
        var square = RunSquare();
        clock.Stop();
        _o.WriteLine($"the square: {square.Seconds:F1} s simulated in {clock.Elapsed.TotalSeconds:F1} s, "
                   + $"{square.Speech.Count(c => c == '\n')} say rows");

        PeopleStore.Dump("speech-square.txt", square.Speech);

        // Where and how the doors are is read before anyone moves, so it is exact on any maths.
        PeopleStore.Check(PeopleStore.PathOf("haunts.tsv"), day.Haunts, "the places HauntFinder finds");
        PeopleStore.Check(PeopleStore.PathOf("doors.tsv"), day.Doors, "the doors' manner and GoingIn");
        PeopleStore.Check(PeopleStore.PathOf("alex.tsv.gz"), day.Alex, "Alex's hour", _o.WriteLine, Dt, ByTick);
        PeopleStore.Check(PeopleStore.PathOf("door-events.tsv.gz"), day.DoorEvents, "the doors' changes", _o.WriteLine, Dt, ByDoor);
        PeopleStore.Check(PeopleStore.PathOf("speech.tsv"), day.Speech, "what was said on the hour", _o.WriteLine, Dt, ByTick);
        PeopleStore.Check(PeopleStore.PathOf("speech-square.tsv.gz"), square.Speech, "what Alex said on the square", _o.WriteLine, Dt, ByTick);
    }

    /// <summary>A store in tick order: one group, the tick first.</summary>
    private static (string Group, int? Tick) ByTick(string line)
        => ("", int.Parse(line.AsSpan(0, line.IndexOf('\t')), CultureInfo.InvariantCulture));

    /// <summary>The doors' changes: one group per door; a closing step carries the door's last opening.</summary>
    private static (string Group, int? Tick) ByDoor(string line)
    {
        var cols = line.Split('\t');
        return (cols[2], cols[0] == "-" ? null : int.Parse(cols[0], CultureInfo.InvariantCulture));
    }

    // ── The runs ────────────────────────────────────────────────────────────────────────────────

    private static CityDay RunCityDay()
    {
        var (maps, data, world, grid, lookup) = Load();
        var vehicles = new VehicleSystem();
        vehicles.Spawn(maps);
        var characters = new CharacterSystem(CharacterSeed) { Pace = 6 };
        characters.Spawn(maps);
        var speech = new PedestrianSpeech(new Random(SpeechSeed));
        speech.CharacterView = characters.ViewOf;
        speech.FaceCharacter = characters.Face;
        var doors = new DoorSystem();
        var occupancy = new OccupancySystem();
        var env = Night();
        var cond = PedestrianSpeech.Conditions(env, WeatherType.Clear);

        var haunts = new StringBuilder();
        var doorKinds = new StringBuilder();
        var alex = new StringBuilder();
        var weather = new StringBuilder();
        var says = new StringBuilder();
        HauntsHeader(haunts);
        DoorsHeader(doorKinds);
        AlexHeader(alex);
        DoorEventsHeader(weather);
        SpeechHeader(says);

        bool captured = false;
        bool alexFound = false;
        Entity alexBody = Entity.Null;
        var lastDoors = new Dictionary<int, (float Target, float Openness, int Hand)>();
        var closing = new Dictionary<int, int>();
        var doorEvents = new List<(int Id, string Line)>();
        var doorNames = new Dictionary<int, string>();
        var speakers = new Dictionary<int, string>();
        var clock = Stopwatch.StartNew();

        for (int tick = 0; tick < HourSeconds / Dt; tick++)
        {
            double now = tick * Dt;
            vehicles.Update(Map, world, Dt);
            characters.Update(Map, world, grid, Dt, env, WeatherType.Clear);
            speech.Update(Map, world, now, cond, (id, label, sound) =>
                Say(says, tick, now, id, label, sound, world, speakers));
            doors.Update(world, Dt, _ => { }, (_, _, _) => { });
            ParentSystem.Update(world, lookup);
            occupancy.Update(world, lookup);

            WatchDoors(world, tick, now, lastDoors, doorNames, closing, doorEvents);

            if (!alexFound && now >= 3.0 && characters.Find("Alex") is { } him)
            {
                alexFound = true;
                alexBody = him.Body;
            }

            if (!captured && now >= CaptureAt)
            {
                captured = true;
                var found = FindHaunts(world, grid, data);
                foreach (var h in found) HauntLine(haunts, h);
                DoorLines(world, doorKinds);
            }

            if (tick % 3 == 0)
            {
                var view = alexFound ? characters.ViewOf(Map, alexBody.Id) : null;
                var body = alexFound && world.IsAlive(alexBody) ? (Transform?)world.Get<Transform>(alexBody) : null;
                AlexLine(alex, tick, now, view, body);
            }
        }
        foreach (var e in doorEvents.OrderBy(e => e.Id)) weather.Append(e.Line);
        clock.Stop();
        return new CityDay(haunts.ToString(), doorKinds.ToString(), alex.ToString(), weather.ToString(),
                           says.ToString(), clock.Elapsed.TotalSeconds);
    }

    private static CityDay RunSquare()
    {
        var (maps, data, world, grid, lookup) = Load();
        data.Characters![0].Places = new List<string> { "Market Square" };
        var vehicles = new VehicleSystem();
        vehicles.Spawn(maps);
        var characters = new CharacterSystem(CharacterSeed) { Pace = 6 };
        characters.Spawn(maps);
        var speech = new PedestrianSpeech(new Random(SpeechSeed));
        speech.CharacterView = characters.ViewOf;
        speech.FaceCharacter = characters.Face;
        var doors = new DoorSystem();
        var occupancy = new OccupancySystem();
        var env = Night();
        var cond = PedestrianSpeech.Conditions(env, WeatherType.Clear);

        var says = new StringBuilder();
        SpeechHeader(says);
        var speakers = new Dictionary<int, string>();
        bool player = false;
        var clock = Stopwatch.StartNew();

        for (int tick = 0; tick < SquareSeconds / Dt; tick++)
        {
            double now = tick * Dt;
            vehicles.Update(Map, world, Dt);
            characters.Update(Map, world, grid, Dt, env, WeatherType.Clear);
            if (!player && now >= CaptureAt)
            {
                var square = FindHaunts(world, grid, data).FirstOrDefault(h => h.Name == "Market Square");
                Assert.NotNull(square);
                world.Create(new Transform { Position = square!.Stand + new Vector3(1.5f, 0f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
                             new Velocity(), new PlayerComponent { Username = "listener" });
                player = true;
            }
            speech.Update(Map, world, now, cond, (id, label, sound) =>
                Say(says, tick, now, id, label, sound, world, speakers));
            doors.Update(world, Dt, _ => { }, (_, _, _) => { });
            ParentSystem.Update(world, lookup);
            occupancy.Update(world, lookup);
        }
        clock.Stop();
        return new CityDay("", "", "", "", says.ToString(), clock.Elapsed.TotalSeconds);
    }

    // ── The world ──────────────────────────────────────────────────────────────────────────────

    private static WorldEnvironmentComponent Night() => new() { GameTime = 23f, Temperature = 3f, DayOfYear = 40 };

    private static (MapManager Maps, MapData Data, World World, SpatialGrid<Entity> Grid, Dictionary<int, Entity> Lookup) Load()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMapData(Map, out var data));
        data.StreetLife = new StreetLifeData { ParkEverySeconds = 20 };
        Assert.True(maps.TryGetMap(Map, out var world, out _, out var grid, out var lookup));
        return (maps, data, world, grid, lookup);
    }

    /// <summary>The pedestrians' voice counters are static: a replay starts them where the first one did, whatever
    /// else in this test process has spawned people before it.</summary>
    private static void ResetVoices()
    {
        foreach (string name in new[] { "_nextVoice", "_pairVoices" })
        {
            var field = typeof(PedestrianSpeech).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is System.Collections.IDictionary counter) counter.Clear();
        }
    }

    // ── What is stored ─────────────────────────────────────────────────────────────────────────

    private static void HauntsHeader(StringBuilder sb)
        => sb.AppendLine("# name\tkind\tstand x,y,z\tfacing deg\tdoor\taccess x,z\tedge");

    private static List<Haunt> FindHaunts(World world, SpatialGrid<Entity> grid, MapData data)
    {
        var view = ServerPeopleView.Of(world, grid);
        var net = Pavements.Build(Pavements.StripsOf(view));
        var solids = Pavements.SolidsOf(view);
        return HauntFinder.Find(view, data, net, solids);
    }

    private static void HauntLine(StringBuilder sb, Haunt h)
        => sb.Append(Flat(h.Name)).Append('\t').Append(h.Kind).Append('\t').Append(Pos(h.Stand)).Append('\t')
             .Append(Degrees(h.Facing)).Append('\t').Append(h.Door.ToString(CultureInfo.InvariantCulture))
             .Append('\t').Append(Flat(h.Access.X)).Append(',').Append(Flat(h.Access.Y)).Append('\t')
             .Append(h.AccessEdge.ToString(CultureInfo.InvariantCulture)).AppendLine();

    private static void DoorsHeader(StringBuilder sb)
        => sb.AppendLine("# door\tname\tmanner\tgoing in, -Z to +Z\tgoing in, +Z to -Z");

    private static void DoorLines(World world, StringBuilder sb)
    {
        var list = new List<(int Id, string Name, DoorRules.Kind Kind, bool Forward, bool Back)>();
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e) =>
        {
            DoorSystem.Doorway(world, e, out var centre, out var rotation);
            var through = Vector3.Transform(Vector3.UnitZ, rotation); through.Y = 0f;
            if (through.LengthSquared() > 1e-6f) through = Vector3.Normalize(through);
            var from = centre - through * 2f;
            var to = centre + through * 2f;
            list.Add((e.Id, NameOf(world, e), DoorManners.KindOf(world, e),
                      DoorManners.GoingIn(world, from, to), DoorManners.GoingIn(world, to, from)));
        });
        foreach (var d in list.OrderBy(d => d.Id))
            sb.Append(d.Id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Flat(d.Name)).Append('\t')
              .Append(d.Kind).Append('\t').Append(d.Forward ? "in" : "out").Append('\t').Append(d.Back ? "in" : "out").AppendLine();
    }

    private static void AlexHeader(StringBuilder sb)
        => sb.AppendLine("# tick\tt\tname\tvoice\tdoing\tkind\tplace\tindoors\tuntil\tx\ty\tz\tyaw deg");

    private static void AlexLine(StringBuilder sb, int tick, double now, CharacterSystem.View? view, Transform? body)
    {
        sb.Append(tick.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Flat(now)).Append('\t');
        if (view is { } v)
            sb.Append(Flat(v.Name)).Append('\t').Append(Flat(v.Voice)).Append('\t').Append(v.Doing).Append('\t')
              .Append(v.Kind?.ToString() ?? "-").Append('\t').Append(Flat(v.Place)).Append('\t')
              .Append(v.Indoors ? "in" : "out").Append('\t').Append(Flat(v.Until)).Append('\t');
        else sb.Append("none\t-\t-\t-\t-\t-\t-\t");
        if (body is { } t)
            sb.Append(Pos(t.Position)).Append('\t').Append(YawDegrees(t.Rotation)).AppendLine();
        else sb.Append("-\t-\t-\t-").AppendLine();
    }

    private static void DoorEventsHeader(StringBuilder sb)
        => sb.AppendLine("# tick\tstep\tdoor\tname\ttarget\topenness\thand");

    /// <summary>
    /// Every door whose target, openness or hand changed. Openings and hand changes carry their tick. A closing
    /// animation carries no tick, only its step: DoorManners.Through makes each person's pause from
    /// HashCode.Combine, whose seed .NET picks per process, so the tick a close starts on is not reproducible
    /// across processes; the states and the animation's length are. A move that changes door law still changes
    /// which doors move and how, and the next opening, which is timed.
    /// </summary>
    private static void WatchDoors(World world, int tick, double now, Dictionary<int, (float Target, float Openness, int Hand)> last,
                                   Dictionary<int, string> names, Dictionary<int, int> closing, List<(int Id, string Line)> events)
    {
        var seen = new HashSet<int>();
        var changed = new List<(int Id, string Name, float Target, float Openness, int Hand)>();
        world.Query(new QueryDescription().WithAll<DoorComponent>(), (Entity e, ref DoorComponent d) =>
        {
            seen.Add(e.Id);
            var was = last.GetValueOrDefault(e.Id, (float.NaN, float.NaN, int.MinValue));
            if (!float.IsNaN(was.Item1) && was == (d.Target, d.Openness, d.HandId)) return;
            last[e.Id] = (d.Target, d.Openness, d.HandId);
            if (!names.TryGetValue(e.Id, out string? name)) names[e.Id] = name = NameOf(world, e);
            changed.Add((e.Id, name, d.Target, d.Openness, d.HandId));
        });
        foreach (int gone in last.Keys.Where(id => !seen.Contains(id)).ToList()) { last.Remove(gone); closing.Remove(gone); }
        foreach (var c in changed.OrderBy(c => c.Id))
        {
            bool shut = c.Target < 0.5f && (c.Openness > 0f || closing.ContainsKey(c.Id));
            int step = 0;
            if (shut)
            {
                step = closing.GetValueOrDefault(c.Id) + 1;
                closing[c.Id] = step;
            }
            else closing.Remove(c.Id);
            events.Add((c.Id, (shut ? "-" : tick.ToString(CultureInfo.InvariantCulture)) + '\t'
                + step.ToString(CultureInfo.InvariantCulture) + '\t'
                + c.Id.ToString(CultureInfo.InvariantCulture) + '\t' + Flat(c.Name) + '\t'
                + Flat(c.Target) + '\t' + Flat(c.Openness) + '\t'
                + c.Hand.ToString(CultureInfo.InvariantCulture) + '\n'));
        }
    }

    private static void SpeechHeader(StringBuilder sb)
        => sb.AppendLine("# tick\tt\tspeaker\tlabel\tsynth\tx\ty\tz\tlevel\tdelay");

    private static void Say(StringBuilder sb, int tick, double now, int id, string label, TransientSound sound,
                            World world, Dictionary<int, string> speakers)
    {
        sb.Append(tick.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Flat(now)).Append('\t')
          .Append(id.ToString(CultureInfo.InvariantCulture)).Append(':').Append(Flat(SpeakerOf(world, id, speakers))).Append('\t')
          .Append(Flat(label)).Append('\t').Append(Flat(sound.SynthKey)).Append('\t')
          .Append(Pos(sound.Position)).Append('\t').Append(Flat(sound.LevelDb)).Append('\t').Append(Flat(sound.DelaySeconds)).AppendLine();
    }

    private static string SpeakerOf(World world, int id, Dictionary<int, string> speakers)
    {
        if (speakers.TryGetValue(id, out string? known)) return known;
        string name = "?";
        world.Query(new QueryDescription().WithAll<Pedestrian>(), (Entity e, ref Pedestrian p) =>
        {
            if (e.Id == id) name = string.IsNullOrEmpty(p.Character) ? p.Voice : p.Character;
        });
        if (name == "?")
            world.Query(new QueryDescription().WithAll<PlayerComponent>(), (Entity e, ref PlayerComponent p) =>
            {
                if (e.Id == id) name = p.Username;
            });
        speakers[id] = name;
        return name;
    }

    // ── Small things ────────────────────────────────────────────────────────────────────────────

    private static string NameOf(World world, Entity e)
        => world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) ? world.Get<NameComponent>(e).Name ?? "" : "";

    private static string Flat(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ');

    private static string Flat(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Flat(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Centimetres: two places of a metre.</summary>
    private static string Pos(Vector3 p)
        => string.Join(',', p.X.ToString("0.00", CultureInfo.InvariantCulture),
                              p.Y.ToString("0.00", CultureInfo.InvariantCulture),
                              p.Z.ToString("0.00", CultureInfo.InvariantCulture));

    private static string Degrees(float radians)
        => ((int)MathF.Round(radians * (180f / (float)Math.PI))).ToString(CultureInfo.InvariantCulture);

    private static string YawDegrees(Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        forward.Y = 0f;
        return forward.LengthSquared() < 1e-6f ? "0" : Degrees(MathF.Atan2(forward.X, forward.Z));
    }
}
