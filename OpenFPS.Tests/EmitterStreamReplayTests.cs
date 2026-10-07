using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The emitter-stream replay (docs/SOUND_LIBRARY_BOUNDARY.md, stage 0). A whole ClientAudioSystem is driven
/// through scripted worlds (a walk past cars and walls, a drive with a horn, traffic in the rain) and every
/// call it makes on the mixer is written down and compared with the stream stored in
/// OpenFPS.Tests/LibraryBoundary/streams: a refactor that changes nothing changes no line of it.
///
/// Everything left to threads, the clock or chance is pinned (the acoustic worker answers on the test's
/// thread, without Steam Audio; the geometry and rain survey are built in place; birds, near drops and
/// footsteps are seeded), and each scenario must agree with itself twice before the comparison. Where the
/// maths library matches the machine the streams were made on (RenderFingerprintTests.MathsProbe) the stream
/// must match to the character; elsewhere the same calls, every number within a part in a thousand.
///
/// An intended change regenerates the stream in the same commit, saying why: OPENFPS_REPLAY_WRITE=1 dotnet
/// test --filter EmitterStreamReplayTests. OPENFPS_REPLAY_DUMP=dir writes each run's stream as text.
/// </summary>
public class EmitterStreamReplayTests
{
    private readonly ITestOutputHelper _o;
    public EmitterStreamReplayTests(ITestOutputHelper o) => _o = o;

    public static IEnumerable<object[]> Scenarios() => Replay.Scenarios.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void StreamIsUnchanged(string scenario)
    {
        var first = Replay.Run(scenario);
        var second = Replay.Run(scenario);
        Dump(scenario, first);
        int apart = FirstDifference(first, second);
        Assert.True(apart < 0, $"{scenario}: two runs in one process part at line {apart + 1}:\n  {Line(first, apart)}\n  {Line(second, apart)}");
        _o.WriteLine($"{scenario}: {first.Count} lines, {first.Sum(l => l.Length + 1) / 1024} KiB");

        string maths = RenderFingerprintTests.MathsProbe();
        string path = StreamPath(scenario);
        if (Environment.GetEnvironmentVariable("OPENFPS_REPLAY_WRITE") == "1")
        {
            Write(path, maths, first);
            _o.WriteLine($"{scenario}: written");
            return;
        }

        Assert.True(File.Exists(path), $"{scenario} has no stored stream: run with OPENFPS_REPLAY_WRITE=1 and commit it.");
        var (storedMaths, stored) = Read(path);
        if (storedMaths == maths)
        {
            int at = FirstDifference(stored, first);
            Assert.True(at < 0, $"{scenario}: the stream parts from the stored one at line {at + 1} of {stored.Count}:\n"
                + $"  stored: {Line(stored, at)}\n  now:    {Line(first, at)}\n"
                + "If the change is intended, regenerate in the same commit with the reason (docs/SOUND_LIBRARY_BOUNDARY.md, stage 0).");
            return;
        }

        // Compared whole (every field of each voice): a value that rounds the same between two frames on one
        // machine may not on another, and the stream writes only what changed.
        _o.WriteLine($"{scenario}: the maths differ from the machine the stream was made on; numbers compared within 1e-3");
        Assert.Equal(stored.Count, first.Count);
        var a = Expand(stored);
        var b = Expand(first);
        for (int i = 0; i < a.Count; i++)
            Assert.True(Near(a[i], b[i]), $"{scenario}: line {i + 1} differs beyond rounding:\n  stored: {a[i]}\n  now:    {b[i]}");
    }

    /// <summary>The stream with every Play, Place and Path line written whole: each voice's fields as
    /// they stand after that line, by name.</summary>
    internal static List<string> Expand(List<string> lines)
    {
        var state = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        var result = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var parts = SplitTop(line);
            if (parts.Count >= 3 && parts[1] is "Play" or "Place" or "Path")
            {
                string key = (parts[1] == "Path" ? "P" : "E") + parts[2];
                if (parts[1] == "Play" || !state.TryGetValue(key, out var fields))
                    state[key] = fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
                for (int i = 3; i < parts.Count; i++)
                {
                    int eq = parts[i].IndexOf('=');
                    fields[parts[i][..eq]] = parts[i][(eq + 1)..];
                }
                result.Add($"{parts[0]} {parts[1]} {parts[2]} " + string.Join(" ", fields.Select(f => f.Key + "=" + f.Value)));
                continue;
            }
            if (parts.Count >= 3 && parts[1] == "Stop") { state.Remove("E" + parts[2]); state.Remove("P" + parts[2]); }
            result.Add(line);
        }
        return result;
    }

    /// <summary>Splits a line at the spaces outside brackets and quotes.</summary>
    private static List<string> SplitTop(string line)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '(' or '[' or '{') depth++;
            else if (!quoted && c is ')' or ']' or '}') depth--;
            else if (!quoted && depth == 0 && c == ' ')
            {
                parts.Add(line[start..i]);
                start = i + 1;
            }
        }
        parts.Add(line[start..]);
        return parts;
    }

    private static string Line(List<string> lines, int i) => i >= 0 && i < lines.Count ? lines[i] : "(end of stream)";

    private static int FirstDifference(List<string> a, List<string> b)
    {
        int n = Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Count == b.Count ? -1 : n;
    }

    /// <summary>The same text with every number within a part in a thousand (or 1e-4 absolute).</summary>
    internal static bool Near(string a, string b)
    {
        if (a == b) return true;
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count != tb.Count) return false;
        for (int i = 0; i < ta.Count; i++)
        {
            if (ta[i] == tb[i]) continue;
            if (!double.TryParse(ta[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(tb[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return false;
            if (Math.Abs(x - y) > Math.Max(1e-4, 1e-3 * Math.Max(Math.Abs(x), Math.Abs(y)))) return false;
        }
        return true;
    }

    private static List<string> Tokens(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '-' or '+' or '_')
                sb.Append(c);
            else
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                list.Add(c.ToString());
            }
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private static string StreamPath(string scenario, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "LibraryBoundary", "streams", scenario + ".txt.gz");

    private static void Write(string path, string maths, List<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        // No name or time in the gzip header: the same stream makes the same file.
        using var gz = new GZipStream(file, CompressionLevel.SmallestSize);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine("# maths " + maths);
        foreach (var l in lines) w.WriteLine(l);
    }

    private static (string Maths, List<string> Lines) Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        string maths = (r.ReadLine() ?? "").Replace("# maths ", "");
        var lines = new List<string>();
        for (string? l; (l = r.ReadLine()) != null;) lines.Add(l);
        return (maths, lines);
    }

    private static void Dump(string scenario, List<string> lines)
    {
        string? dir = Environment.GetEnvironmentVariable("OPENFPS_REPLAY_DUMP");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, scenario + ".txt"), lines);
    }

    [Fact]
    public void EveryScenarioIsStoredAndNothingElse()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_REPLAY_WRITE") == "1") return;
        string dir = Path.GetDirectoryName(StreamPath("x"))!;
        var stored = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.txt.gz").Select(f => Path.GetFileName(f)[..^".txt.gz".Length]).OrderBy(n => n, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        Assert.Equal(Replay.Scenarios.Keys.OrderBy(k => k, StringComparer.Ordinal), stored);
    }

    [Fact]
    public void TheStreamWritesEveryField()
    {
        var e = new SpatialEmitter { EntityId = 7, SoundId = "engine:x", Position = new Vector3(1f, 2f, 3f), Volume = 0.1f };
        string s = StreamMixer.Format(e);
        Assert.Contains("EntityId=7", s);
        Assert.Contains("SoundId=\"engine:x\"", s);
        Assert.Contains("Position=(1,2,3)", s);
        Assert.Contains("Volume=0.1", s);
        Assert.True(Near("a=1.0001 b=(2,3)", "a=1.0002 b=(2,3)"));
        Assert.False(Near("a=1.01 b=(2,3)", "a=1.02 b=(2,3)"));

        // A re-placing writes what changed; expanded, every line says everything.
        var expanded = Expand(new List<string>
        {
            "1 Play 5 A=1 B={X=1 Y=2} C=\"a b\"",
            "2 Place 5 B={X=1 Y=3}",
            "2 Place 5",
            "3 Stop 5",
            "4 Place 5 A=2",
        });
        Assert.Equal("1 Play 5 A=1 B={X=1 Y=2} C=\"a b\"", expanded[0]);
        Assert.Equal("2 Place 5 A=1 B={X=1 Y=3} C=\"a b\"", expanded[1]);
        Assert.Equal("2 Place 5 A=1 B={X=1 Y=3} C=\"a b\"", expanded[2]);
        Assert.Equal("4 Place 5 A=2", expanded[4]);
    }
}

/// <summary>The scripted worlds and the system that hears them.</summary>
internal sealed class Replay
{
    private const int Seed = 7;
    private const double Dt = 1.0 / ClientAudioSystem.UpdateHz;

    public readonly StreamMixer Mixer = new();
    public readonly AudioEngineFacade Facade;
    public readonly ClientWorldState World = new();
    public readonly LocalPlayerState Player = new();
    public readonly ClientAudioSystem Audio;
    public double Now { get; private set; } = 1.0;
    public int Frame { get; private set; }

    private Replay()
    {
        World.Geometry.Runner = work => work();
        World.RefreshRunner = work => work();
        World.Clear(new Vector3(4000, 400, 4000));
        Facade = new AudioEngineFacade(Mixer);
        Facade.InitializeForTest();
        Audio = new ClientAudioSystem(Facade, new SoundMappingService(Player), Player, () => Now, manualAcoustics: true, seed: Seed);
    }

    /// <summary>Every scenario: a name and the script that sets the world up and moves it each frame.</summary>
    public static readonly Dictionary<string, Action<Replay>> Scenarios = new()
    {
        ["walk"] = Walk,
        ["drive"] = Drive,
        ["traffic_rain"] = TrafficInRain,
    };

    public static List<string> Run(string scenario)
    {
        AcousticRegistry.Initialize();
        // The statics the audio system reads, at the game's defaults whatever ran before, and put back after
        // (docs/SOUND_LIBRARY_BOUNDARY.md, 4.1).
        var saved = (Loudness.DynamicRangeCompression, OpenFPS.Common.Hearing.EarModel.Enabled,
                     OpenFPS.Common.Hearing.EarModel.ListeningLevelDb, AudioPhysics.CurrentSpeedOfSound,
                     AudioPhysics.CurrentAirCelsius, MixerQuality.MixerRate,
                     OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Enabled,
                     OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Enabled,
                     OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise, RainField.RoofRidesWithHead, Runoff.Held);
        Loudness.DynamicRangeCompression = Loudness.DefaultCompression;
        OpenFPS.Common.Hearing.EarModel.Enabled = true;
        OpenFPS.Common.Hearing.EarModel.ListeningLevelDb = OpenFPS.Common.Hearing.EarModel.DefaultListeningLevelDb;
        AudioPhysics.CurrentSpeedOfSound = AudioPhysics.SpeedOfSound;
        AudioPhysics.CurrentAirCelsius = 20f;
        MixerQuality.MixerRate = MixerQuality.DefaultRate;
        OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Enabled = true;
        OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Enabled = true;
        OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise = true;
        RainField.RoofRidesWithHead = true;
        Runoff.Held = false;
        Runoff.Reset();
        try
        {
            var r = new Replay();
            // A still day: the weather's wind is read on the wall clock (WindField.Now).
            using (WindField.Hold(WindWeather.Steady(0f, 0f, 0f)))
            using (AudioClock.UseForTest(() => r.Now))
                Scenarios[scenario](r);
            return r.Mixer.Lines;
        }
        finally
        {
            (Loudness.DynamicRangeCompression, OpenFPS.Common.Hearing.EarModel.Enabled,
             OpenFPS.Common.Hearing.EarModel.ListeningLevelDb, AudioPhysics.CurrentSpeedOfSound,
             AudioPhysics.CurrentAirCelsius, MixerQuality.MixerRate,
             OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Enabled,
             OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Enabled,
             OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise, RainField.RoofRidesWithHead, Runoff.Held) = saved;
            Runoff.Reset();
        }
    }

    /// <summary>One audio frame: the update, the acoustic worker's answers, then the facade's audio thread
    /// pumped by hand twice (a voice the budget plays is queued, then handed over).</summary>
    private void Tick()
    {
        Frame++;
        Now += Dt;
        Mixer.Frame = Frame;
        Audio.Update(World.GetSnapshot());
        Audio.AcousticWorker.StepForTest();
        Facade.PumpForTest();
        Facade.PumpForTest();
    }

    // ── The world ───────────────────────────────────────────────────────────────────────────────

    private void Wall(int id, Vector3 centre, Vector3 size, string material)
        => World.RegisterDefinition(new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
            Material = new MaterialComponent { Material = material },
        });

    /// <summary>A car as the server puts one on the map: an engine emitter on a moving entity.</summary>
    private void Car(int id, string preset, Vector3 at, Vector3 velocity)
    {
        var profile = MachineRegistry.VehicleFor(preset);
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.NPC,
            Moves = true,
            Transform = new Transform { Position = at, Rotation = Facing(velocity) },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "engine:" + preset;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = Loudness.AudibleRange(profile.SourceLevelDb);
        def.SoundEmitter.MinDistance = 3f;
        World.RegisterDefinition(def);
        Move(id, at, velocity);
    }

    private void Move(int id, Vector3 at, Vector3 velocity)
        => World.SyncState(new[] { new EntityState
        {
            EntityId = id,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = Facing(velocity) }),
            LinearVelocity = velocity,
        } });

    /// <summary>Turned to face along its velocity (forward is -Z), or north when it stands.</summary>
    private static Quaternion Facing(Vector3 velocity)
        => velocity.LengthSquared() < 1e-6f ? Quaternion.Identity
         : Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(-velocity.X, -velocity.Z));

    private void Listener(Vector3 feet, Vector3 velocity, float yawDegrees)
    {
        Player.Position = feet;
        Player.Velocity = velocity;
        Player.Yaw = yawDegrees;
        Player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yawDegrees * MathF.PI / 180f);
    }

    // ── The scenarios ───────────────────────────────────────────────────────────────────────────

    /// <summary>Eight seconds on foot up a street: a brick building on the right, a concrete wall on the
    /// left, a car idling at the kerb, and another driving past.</summary>
    private static void Walk(Replay r)
    {
        r.Wall(9001, new Vector3(8f, 3f, 0f), new Vector3(0.3f, 6f, 24f), "Brick");
        r.Wall(9002, new Vector3(14f, 3f, -12f), new Vector3(12f, 6f, 0.3f), "Brick");
        r.Wall(9003, new Vector3(-7f, 1.5f, 6f), new Vector3(0.25f, 3f, 14f), "Concrete");
        r.Car(101, "v8_muscle", new Vector3(5f, 0.5f, 4f), Vector3.Zero);
        var passing = new Vector3(3f, 0.5f, -70f);
        var speed = new Vector3(0f, 0f, 14f);
        r.Car(102, "v8_muscle", passing, speed);
        var feet = new Vector3(0f, 0f, -6f);
        var walking = new Vector3(0f, 0f, 1.4f);
        for (int f = 0; f < 480; f++)
        {
            r.Listener(feet, walking, 0f);
            r.Move(102, passing, speed);
            r.Tick();
            feet += walking * (float)Dt;
            passing += speed * (float)Dt;
        }
    }

    /// <summary>Six seconds riding east in a car along a wall, another coming the other way that sounds
    /// its horn as it nears.</summary>
    private static void Drive(Replay r)
    {
        r.Wall(9101, new Vector3(0f, 2.5f, -8f), new Vector3(120f, 5f, 0.3f), "Concrete");
        var mine = new Vector3(-40f, 0.5f, 0f);
        var east = new Vector3(12f, 0f, 0f);
        var theirs = new Vector3(60f, 0.5f, 4f);
        var west = new Vector3(-12f, 0f, 0f);
        r.Car(201, "v8_muscle", mine, east);
        r.Car(202, "v8_muscle", theirs, west);
        r.Player.RidingEntityId = 201;
        for (int f = 0; f < 360; f++)
        {
            r.Listener(mine, east, 90f);
            r.Move(201, mine, east);
            r.Move(202, theirs, west);
            if (f == 150) r.Audio.WorldAudio.HornReceived!(202, "electric:disc_pair", new[] { 0.6f, 0.2f, 0.4f });
            r.Tick();
            mine += east * (float)Dt;
            theirs += west * (float)Dt;
        }
    }

    /// <summary>Six seconds standing by a wall at a crossing in 8 mm/h of rain, five cars on two lanes.</summary>
    private static void TrafficInRain(Replay r)
    {
        r.Wall(9201, new Vector3(-4f, 3f, 0f), new Vector3(0.3f, 6f, 16f), "Brick");
        r.Wall(9202, new Vector3(-10f, 6f, 0f), new Vector3(12f, 0.3f, 16f), "Concrete");   // a roof over a porch
        r.World.UpdateAtmosphere(new WorldStateUpdate
        {
            Temperature = 12f, Humidity = 0.9f, AirPressure = 1008f, AirAbsorptionMultiplier = 1f,
            PrecipitationIntensity = 0.5f, RainRateMmPerHour = 8f, PrecipitationKind = (int)PrecipitationKind.Rain,
            RainMedianDropMm = 1.4f,
        });
        var cars = new (int Id, Vector3 At, Vector3 V)[]
        {
            (301, new Vector3(4f, 0.5f, -50f), new Vector3(0f, 0f, 11f)),
            (302, new Vector3(4f, 0.5f, -90f), new Vector3(0f, 0f, 13f)),
            (303, new Vector3(8f, 0.5f, 60f), new Vector3(0f, 0f, -12f)),
            (304, new Vector3(8f, 0.5f, 20f), new Vector3(0f, 0f, -9f)),
            (305, new Vector3(-40f, 0.5f, 14f), new Vector3(10f, 0f, 0f)),
        };
        foreach (var c in cars) r.Car(c.Id, "v8_muscle", c.At, c.V);
        var feet = new Vector3(0f, 0f, 2f);
        for (int f = 0; f < 360; f++)
        {
            r.Listener(feet, Vector3.Zero, 90f);
            for (int i = 0; i < cars.Length; i++)
            {
                r.Move(cars[i].Id, cars[i].At, cars[i].V);
                cars[i].At += cars[i].V * (float)Dt;
            }
            r.Tick();
        }
    }
}

/// <summary>
/// A mixer that writes down every call made on it, in order, one line each with the frame it came in,
/// and plays nothing. Its answers (what is playing, where) come from what it was told, as a mixer's do.
/// </summary>
internal sealed class StreamMixer : IAudioProvider
{
    public readonly List<string> Lines = new();
    public int Frame;
    private readonly Dictionary<int, SpatialEmitter> _latest = new();
    private readonly HashSet<int> _live = new();
    /// <summary>What each voice was last told, field by field, so a re-placing writes only what changed.</summary>
    private readonly Dictionary<int, string[]> _emitterFields = new(), _pathFields = new();

    /// <summary>A voice's emitter or path: whole when it starts, then only the fields that changed.</summary>
    private void LogDelta(string call, int id, object value, Dictionary<int, string[]> last, bool whole)
    {
        var fields = Fields(value.GetType());
        var now = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            var sb = new StringBuilder();
            Append(sb, fields[i].GetValue(value), 1);
            now[i] = sb.ToString();
        }
        var line = new StringBuilder().Append(Frame).Append(' ').Append(call).Append(' ').Append(id);
        last.TryGetValue(id, out var before);
        for (int i = 0; i < fields.Length; i++)
            if (whole || before == null || before[i] != now[i])
                line.Append(' ').Append(Name(fields[i])).Append('=').Append(now[i]);
        last[id] = now;
        Lines.Add(line.ToString());
    }

    private static string Name(FieldInfo f) => f.Name.TrimStart('<').Replace(">k__BackingField", "");

    private void Log(string call, params object?[] args)
    {
        var sb = new StringBuilder();
        sb.Append(Frame).Append(' ').Append(call);
        foreach (var a in args) { sb.Append(' '); Append(sb, a, 0); }
        Lines.Add(sb.ToString());
    }

    internal static string Format(object? value)
    {
        var sb = new StringBuilder();
        Append(sb, value, 0);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, object? v, int depth)
    {
        switch (v)
        {
            case null: sb.Append("null"); return;
            case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
            case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
            case string s: sb.Append('"').Append(s).Append('"'); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case Enum e: sb.Append(e.ToString()); return;
            case IFormattable n when v.GetType().IsPrimitive: sb.Append(n.ToString(null, CultureInfo.InvariantCulture)); return;
            case Vector3 p: sb.Append('(').Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                              .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                              .Append(p.Z.ToString("R", CultureInfo.InvariantCulture)).Append(')'); return;
            case Quaternion q: sb.Append('(').Append(q.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                                 .Append(q.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                                 .Append(q.Z.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                                 .Append(q.W.ToString("R", CultureInfo.InvariantCulture)).Append(')'); return;
            case Delegate: sb.Append("fn"); return;
            case IEnumerable list:
                sb.Append('[');
                bool first = true;
                foreach (var item in list) { if (!first) sb.Append('|'); first = false; Append(sb, item, depth + 1); }
                sb.Append(']');
                return;
        }
        var type = v.GetType();
        if (depth > 3 || !type.IsValueType) { sb.Append(type.Name); return; }
        sb.Append('{');
        bool firstField = true;
        foreach (var field in Fields(type))
        {
            if (!firstField) sb.Append(' ');
            firstField = false;
            sb.Append(Name(field)).Append('=');
            Append(sb, field.GetValue(v), depth + 1);
        }
        sb.Append('}');
    }

    private static readonly Dictionary<Type, FieldInfo[]> _fields = new();

    /// <summary>Every instance field, in declaration order: the struct is the record, all of it.</summary>
    private static FieldInfo[] Fields(Type t)
    {
        if (!_fields.TryGetValue(t, out var f))
            _fields[t] = f = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              .OrderBy(x => x.MetadataToken).ToArray();
        return f;
    }

    public bool Initialize() { Log("Initialize"); return true; }
    public void Update() { }
    public void UpdateListener(Vector3 p, Quaternion r, Vector3 v, int region) => Log("Listener", p, r, v, region);
    public void UpdateShelter(float f) => Log("Shelter", f);
    public void SetListenerEnclosure(float lowDb, float midDb, float highDb) => Log("Enclosure", lowDb, midDb, highDb);
    public void SetEarWind(EarWindListener? listener) => Log("EarWind", listener);
    public void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes) => Log("Boundaries", probes.ToArray());
    public bool PlayAmbientBed(string id, AmbisonicLayout l, float v, bool loop = true) { Log("PlayBed", id, l, v, loop); return true; }
    public void StopAmbientBed(string id) => Log("StopBed", id);
    public void SetAcousticMap(AcousticMap map) => Log("AcousticMap");
    public void PlaySpatialSound(SpatialEmitter e) { LogDelta("Play", e.EntityId, e, _emitterFields, whole: true); _latest[e.EntityId] = e; _live.Add(e.EntityId); }
    public void UpdateSpatialAttributes(SpatialEmitter e) { LogDelta("Place", e.EntityId, e, _emitterFields, whole: false); _latest[e.EntityId] = e; }
    public void SetAcousticPath(int id, AcousticPathData p) => LogDelta("Path", id, p, _pathFields, whole: false);
    public void SetSimulatedReverbDecay(float ms, float enclosure, float hf, float lf) => Log("Reverb", ms, enclosure, hf, lf);
    public void SetAirTemperature(float c) => Log("Air", c);
    public float MixerLoad => 0f;
    public int SpatialVoicesFree => 256;
    public void ReviveEngine(int id) => Log("ReviveEngine", id);
    public bool FadeOutEngine(int id) { Log("FadeEngine", id); return true; }
    public bool FadeOutVoice(int id) { Log("FadeVoice", id); return true; }
    public void CancelVoiceFade(int id) => Log("CancelFade", id);
    public void SignalTrain(string train, float[] warning, float bellSeconds, double secondsAgo) => Log("SignalTrain", train, warning, bellSeconds, secondsAgo);
    public void StopSound(int id) { Log("Stop", id); _live.Remove(id); _emitterFields.Remove(id); _pathFields.Remove(id); }
    public bool IsPlaying(int id) => _live.Contains(id);
    public Vector3 GetSoundPosition(int id) => _latest.TryGetValue(id, out var e) ? e.Position : Vector3.Zero;
    public IEnumerable<int> GetActiveSpatialSoundIds() => _live.OrderBy(i => i).ToList();
    public void Preload(string id) => Log("Preload", id);
    public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate)
    {
        Log("Register", soundId, pcm16Mono.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pcm16Mono), 0, 8), sampleRate);
        return true;
    }
    public bool ReleaseSynthesisedSound(string soundId) { Log("Release", soundId); return true; }
    public void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume) => Log("Ui", id, sampleRate, volume);
    public void SetUiLoop(string slot, string id, Func<float[]> render, int sampleRate, float volume, float pitch) => Log("UiLoop", slot, id, sampleRate, volume, pitch);
    public void StopUiLoop(string slot) => Log("StopUiLoop", slot);
    public void SetWorldFade(float gain) => Log("WorldFade", gain);
    public IReadOnlyList<VoiceLevel> LoudestVoices(int count) => Array.Empty<VoiceLevel>();
    public IReadOnlyList<string> OutputDevices() => new[] { "Test output" };
    public IReadOnlyList<string> InputDevices() => new[] { "Test input" };
    public bool SetOutputDevice(string name) => true;
    public void StartDiagnosticSound() { }
    public void SetDiagnosticPosition(Vector3 p) { }
    public void StopDiagnosticSound() { }
    public void Dispose() { }
}
