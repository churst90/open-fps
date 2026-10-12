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
/// through scripted worlds (a walk past cars and walls, a drive with a horn, traffic in the rain, and five
/// more from part P0 of docs/SPECIAL_CASES_AUDIT.md: sirens and borrowed cars, a train, spread places and
/// woods, a talker and the driving aids, ids at the real-place maps' scale) and every
/// call it makes on the mixer is written down and compared with the stream stored in
/// OpenFPS.Tests/LibraryBoundary/streams: a refactor that changes nothing changes no line of it.
///
/// Everything left to threads, the clock or chance is pinned (the acoustic worker answers on the test's
/// thread, without Steam Audio; the geometry and rain survey are built in place; birds, near drops and
/// footsteps are seeded; voice chat decodes with the managed Opus codec, never a system libopus), and each scenario must agree with itself twice before the comparison. Where the
/// maths library matches the machine the streams were made on (RenderFingerprintTests.MathsProbe) the stream
/// must match to the character; elsewhere the same calls, every number within a part in a thousand, and a
/// sound rendered for the mixer by its name, length and rate (its bits follow the maths, so its hash does).
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
            Assert.True(Near(WithoutRenderHash(a[i]), WithoutRenderHash(b[i])), $"{scenario}: line {i + 1} differs beyond rounding:\n  stored: {a[i]}\n  now:    {b[i]}");
    }

    /// <summary>A Register line without the hash of the sound's samples: on another maths library a render
    /// differs in its last bits, and a hash is then another hash, not a near one. Every other line as it is.</summary>
    internal static string WithoutRenderHash(string line)
    {
        var parts = SplitTop(line);
        if (parts.Count != 6 || parts[1] != "Register") return line;
        parts[4] = "\"\"";
        return string.Join(' ', parts);
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

    /// <summary>
    /// The stream with every id the registry gave put back to the id that voice had before ids were given
    /// out (by its kind, owner and part, VoiceRegistry.SeedOf) is the stream from before, line for line: what
    /// the mixer is told is the same but for the numbers voices are under (docs/SPECIAL_CASES_AUDIT.md, P3c).
    /// OPENFPS_REPLAY_BEFORE names a folder of the streams from before, stored as these are; without it
    /// there is nothing to compare with and the test says so.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void UnderTheIdMapTheStreamIsTheOneBefore(string scenario)
    {
        string? before = Environment.GetEnvironmentVariable("OPENFPS_REPLAY_BEFORE");
        if (string.IsNullOrEmpty(before)) { _o.WriteLine("OPENFPS_REPLAY_BEFORE is not set: nothing to compare with."); return; }
        // Each written whole (Expand): two voices that shared an id before were written as changes from each
        // other, and are two voices now.
        var (_, stored) = Read(Path.Combine(before, scenario + ".txt.gz"));
        var old = Expand(stored);
        var (now, given) = Replay.RunUnderOldIds(scenario);
        Dump(scenario + ".mapped", now);
        var differ = new List<int>();
        for (int i = 0; i < Math.Min(old.Count, now.Count); i++) if (old[i] != now[i]) differ.Add(i);
        _o.WriteLine($"{scenario}: {now.Count} lines ({old.Count} before), {given} ids given, {differ.Count} lines differ under the map");
        foreach (int i in differ.Take(12)) _o.WriteLine($"  line {i + 1}\n    before: {Line(old, i)}\n    now:    {Line(now, i)}");
        Assert.Equal(old.Count, now.Count);
        // Line for line, but for voices the budget re-places in the order of their ids when nothing else tells
        // them apart (VoiceManager): within a frame they may come in another order now, each line the same.
        var frames = differ.Select(i => old[i].Split(' ', 2)[0]).Distinct().ToList();
        foreach (var frame in frames)
        {
            var was = old.Where(l => l.Split(' ', 2)[0] == frame).OrderBy(l => l, StringComparer.Ordinal).ToList();
            var isNow = now.Where(l => l.Split(' ', 2)[0] == frame).OrderBy(l => l, StringComparer.Ordinal).ToList();
            int part = FirstDifference(was, isNow);
            Assert.True(part < 0, $"{scenario}, frame {frame}: under the id map a line is not the one before:\n"
                + $"  before: {Line(was, part)}\n  now:    {Line(isNow, part)}");
        }
        if (differ.Count > 0)
            _o.WriteLine($"{scenario}: the {differ.Count} lines that differ are the same lines in another order within frames {string.Join(", ", frames)} ("
                + string.Join(", ", differ.Select(i => old[i].Split(' ', 4)).Select(p => p.Length > 2 ? $"{p[1]} {p[2]}" : p[^1]).Distinct()) + ")");
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

        // On another maths library a rendered sound is compared by its name, length and rate, not its bits.
        const string here = "1 Register \"SYNTH/drive_brake_3\" 8640 \"8D6C842614213F96\" 48000";
        Assert.False(Near(here, here.Replace("8D6C", "4202")));
        Assert.Equal(WithoutRenderHash(here), WithoutRenderHash(here.Replace("8D6C", "4202")));
        Assert.NotEqual(WithoutRenderHash(here), WithoutRenderHash(here.Replace("8640", "8641")));
        Assert.NotEqual(WithoutRenderHash(here), WithoutRenderHash(here.Replace("drive_brake_3", "drive_brake_4")));
        Assert.Equal("3 Stop 5", WithoutRenderHash("3 Stop 5"));

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
        // A still day: the weather's wind is read on the wall clock (WindField.Now). The run-off is this
        // world's own, so it starts empty.
        Facade.Weather.Wind = WindWeather.Steady(0f, 0f, 0f);
        Facade.InitializeForTest();
        Audio = new ClientAudioSystem(Facade, new SoundMappingService(Player), Player, () => Now, manualAcoustics: true, seed: Seed);
    }

    /// <summary>Every scenario: a name and the script that sets the world up and moves it each frame.</summary>
    public static readonly Dictionary<string, Action<Replay>> Scenarios = new()
    {
        ["walk"] = Walk,
        ["drive"] = Drive,
        ["traffic_rain"] = TrafficInRain,
        // The five P0 added (docs/SPECIAL_CASES_AUDIT.md, P0): recorded from the code as it was, bugs
        // included. Each says which of the audit's findings its lines show.
        ["sirens_and_far_traffic"] = SirensAndFarTraffic,
        ["train_behind_wall"] = TrainBehindWall,
        ["places_and_woods"] = PlacesAndWoods,
        ["talker_and_aids"] = TalkerAndAids,
        ["high_ids"] = HighIds,
    };

    public static List<string> Run(string scenario) => RunScript(Scenarios[scenario]).Mixer.Lines;

    /// <summary>The verbs whose first argument is a voice id.</summary>
    private static readonly HashSet<string> VoiceVerbs = new(StringComparer.Ordinal)
        { "Play", "Place", "Path", "Stop", "FadeEngine", "FadeVoice", "CancelFade", "ReviveEngine" };

    /// <summary>
    /// A scenario's stream, each line written whole (EmitterStreamReplayTests.Expand), with each id the registry
    /// gave written as the id that voice had before ids were given out: its seed, by kind, owner and part
    /// (VoiceRegistry.SeedOf). An id stands for what it was last given for, from the moment it was given; entity
    /// ids, under which an entity's own voice is, stay. And how many ids were given.
    /// </summary>
    internal static (List<string> Lines, int Given) RunUnderOldIds(string scenario)
    {
        const string Mark = "\u0001given ";
        int given = 0;
        var r = RunScript(x =>
        {
            x.Audio.Voices.OnGiven = (id, key) =>
            {
                if (key.Kind == VoiceKind.Probe) return;
                given++;
                x.Mixer.Lines.Add($"{Mark}{id} {VoiceRegistry.SeedOf(key.Kind, key.Owner, key.Part)}");
            };
            Scenarios[scenario](x);
        });
        var oldOf = new Dictionary<int, string>();
        var lines = new List<string>(r.Mixer.Lines.Count);
        foreach (var line in EmitterStreamReplayTests.Expand(r.Mixer.Lines))
        {
            if (line.StartsWith(Mark, StringComparison.Ordinal))
            {
                var p = line[Mark.Length..].Split(' ');
                oldOf[int.Parse(p[0], CultureInfo.InvariantCulture)] = p[1];
                continue;
            }
            var parts = line.Split(' ');
            if (parts.Length >= 3 && VoiceVerbs.Contains(parts[1]) && int.TryParse(parts[2], CultureInfo.InvariantCulture, out int id)
                && oldOf.TryGetValue(id, out var was))
            {
                parts[2] = was;
                for (int i = 3; i < parts.Length; i++)
                    if (parts[i].StartsWith("EntityId=", StringComparison.Ordinal)) { parts[i] = "EntityId=" + was; break; }
                lines.Add(string.Join(' ', parts));
            }
            else lines.Add(line);
        }
        return (lines, given);
    }

    /// <summary>A script of a test's own, run with everything pinned as the stored scenarios are; the
    /// replay is handed back so the test can read what the mixer was told.</summary>
    internal static Replay RunScript(Action<Replay> script)
    {
        AcousticRegistry.Initialize();
        // The statics the audio system reads, at the game's defaults whatever ran before, and put back after
        // (docs/SOUND_LIBRARY_BOUNDARY.md, 4.1).
        var saved = (Loudness.DynamicRangeCompression, OpenFPS.Common.Hearing.EarModel.Enabled,
                     OpenFPS.Common.Hearing.EarModel.ListeningLevelDb, AudioPhysics.CurrentSpeedOfSound,
                     AudioPhysics.CurrentAirCelsius, MixerQuality.MixerRate,
                     OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Enabled,
                     OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Enabled,
                     OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise, RainField.RoofRidesWithHead,
                     (DrivingCues.Enabled, DrivingCues.Guide, DrivingCues.LineSensors, DrivingCues.TurnClicks,
                      DrivingCues.BrakeCue, DrivingCues.SpeedWarning));
        Loudness.DynamicRangeCompression = Loudness.DefaultCompression;
        (DrivingCues.Enabled, DrivingCues.Guide, DrivingCues.LineSensors, DrivingCues.TurnClicks,
         DrivingCues.BrakeCue, DrivingCues.SpeedWarning) = (true, true, true, true, true, true);
        // Voice chat streams are a static the replay's talker scenario fills; none from another test.
        OpenFPS.Client.AudioEngine.Fmod.Talkers.Clear();
        OpenFPS.Common.Hearing.EarModel.Enabled = true;
        OpenFPS.Common.Hearing.EarModel.ListeningLevelDb = OpenFPS.Common.Hearing.EarModel.DefaultListeningLevelDb;
        AudioPhysics.CurrentSpeedOfSound = AudioPhysics.SpeedOfSound;
        AudioPhysics.CurrentAirCelsius = 20f;
        MixerQuality.MixerRate = MixerQuality.DefaultRate;
        OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Enabled = true;
        OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Enabled = true;
        OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise = true;
        RainField.RoofRidesWithHead = true;
        // The talker's voice through Concentus's own C#, not a system libopus: the talker is placed at the
        // level measured off the decoded audio (OwnVoiceRing.SpeechRmsDbfs), and a libopus where there is one
        // (Linux with it installed) decodes a little differently from the managed codec where there is not
        // (the GitHub runners): the talker's Volume and Range came out a part in a thousand apart.
        bool nativeOpus = Concentus.OpusCodecFactory.AttemptToUseNativeLibrary;
        Concentus.OpusCodecFactory.AttemptToUseNativeLibrary = false;
        try
        {
            var r = new Replay();
            using (AudioClock.UseForTest(() => r.Now))
                script(r);
            return r;
        }
        finally
        {
            (Loudness.DynamicRangeCompression, OpenFPS.Common.Hearing.EarModel.Enabled,
             OpenFPS.Common.Hearing.EarModel.ListeningLevelDb, AudioPhysics.CurrentSpeedOfSound,
             AudioPhysics.CurrentAirCelsius, MixerQuality.MixerRate,
             OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Enabled,
             OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Enabled,
             OpenFPS.Client.AudioEngine.Core.Engine.EngineSynth.ValveJetNoise, RainField.RoofRidesWithHead,
             (DrivingCues.Enabled, DrivingCues.Guide, DrivingCues.LineSensors, DrivingCues.TurnClicks,
              DrivingCues.BrakeCue, DrivingCues.SpeedWarning)) = saved;
            OpenFPS.Client.AudioEngine.Fmod.Talkers.Clear();
            Concentus.OpusCodecFactory.AttemptToUseNativeLibrary = nativeOpus;
        }
    }

    /// <summary>One audio frame: the update, the acoustic worker's answers, then the facade's audio thread
    /// pumped by hand twice (a voice the budget plays is queued, then handed over).</summary>
    internal void Tick()
    {
        Frame++;
        Now += Dt;
        Mixer.Frame = Frame;
        Audio.Update(World.GetSnapshot());
        Audio.AcousticWorker.StepForTest();
        Facade.PumpForTest();
        Facade.PumpForTest();
        AfterTick?.Invoke(this);
    }

    /// <summary>Called at the end of every frame, for a test that reads the system between frames.</summary>
    internal Action<Replay>? AfterTick;

    // ── The world ───────────────────────────────────────────────────────────────────────────────

    internal void Wall(int id, Vector3 centre, Vector3 size, string material)
        => World.RegisterDefinition(new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
            Material = new MaterialComponent { Material = material },
        });

    /// <summary>A car as the server puts one on the map: an engine emitter on a moving entity.</summary>
    internal void Car(int id, string preset, Vector3 at, Vector3 velocity)
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

    internal void Move(int id, Vector3 at, Vector3 velocity, byte signals = 0)
        => World.SyncState(new[] { new EntityState
        {
            EntityId = id,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = Facing(velocity) }),
            LinearVelocity = velocity,
            Signals = signals,
        } });

    /// <summary>One source of a train as the server places it (RailSystem): an NPC entity whose SoundId
    /// names the train and the source.</summary>
    internal void RailSource(int id, string soundId, float levelDb, Vector3 at, Vector3 velocity)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.NPC,
            Moves = true,
            Transform = new Transform { Position = at, Rotation = Facing(velocity) },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = soundId;
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = Loudness.AudibleRange(levelDb);
        def.SoundEmitter.MinDistance = 3f;
        World.RegisterDefinition(def);
        Move(id, at, velocity);
    }

    /// <summary>Turned to face along its velocity, or north when it stands. Forward is +Z, as the server
    /// turns its vehicles (heading = Atan2(x, z), Rotation = CreateFromYawPitchRoll(heading, 0, 0): RaceLine,
    /// VehicleSystem); its traffic never pitches, and these roads are flat.</summary>
    private static Quaternion Facing(Vector3 velocity)
        => velocity.LengthSquared() < 1e-6f ? Quaternion.Identity
         : Quaternion.CreateFromYawPitchRoll(MathF.Atan2(velocity.X, velocity.Z), 0f, 0f);

    /// <summary>The listener as the client turns it (ClientPhysicsSystem): yaw in radians, increasing
    /// toward east, Rotation = CreateFromYawPitchRoll(yaw, pitch 0, 0). Riding, that is the vehicle's heading
    /// (ClientGameSession.FollowRide), so it agrees with <see cref="Facing"/>.</summary>
    internal void Listener(Vector3 feet, Vector3 velocity, float yawDegrees)
    {
        Player.Position = feet;
        Player.Velocity = velocity;
        Player.Yaw = yawDegrees * MathF.PI / 180f;
        Player.Pitch = 0f;
        Player.Rotation = Quaternion.CreateFromYawPitchRoll(Player.Yaw, 0f, 0f);
    }

    /// <summary>A carriageway as the map builds one: an asphalt box, named as a road is.</summary>
    private void Road(int id, Vector3 centre, Vector3 size, string? name)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
            Material = new MaterialComponent { Material = "Asphalt" },
        };
        if (name != null) def.Identity.Name = name;
        World.RegisterDefinition(def);
    }

    /// <summary>A sound that stands where it is: a tree, a fountain's tap, a machine, a loudspeaker.</summary>
    private void Fixture(int id, string soundId, bool synth, Vector3 at, float range, float minDistance)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter = new SoundEmitterComponent
        {
            IsSynth = synth, SoundId = soundId, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = range, MinDistance = minDistance,
        };
        World.RegisterDefinition(def);
    }

    /// <summary>Somebody else on the server, standing or walking.</summary>
    private void Person(int id, Vector3 feet)
    {
        World.RegisterDefinition(new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.Player,
            Moves = true,
            Transform = new Transform { Position = feet, Rotation = Quaternion.Identity, Scale = Vector3.One },
        });
        Move(id, feet, Vector3.Zero);
    }

    /// <summary>
    /// The engine budget as a mixer at its ceiling leaves it (ChooseLiveEngines sheds to two): the budget
    /// is 32, and a scenario of 33 cars would be a stream many times the size for the same behaviour. The
    /// replay's mixer reports no load, so nothing gives it back.
    /// </summary>
    internal void ShedEnginesTo(int engines)
        => typeof(ClientAudioSystem).GetField("_adaptiveBudget", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Audio, engines);

    /// <summary>A driver's switches as the wire carries them (EntityState.Signals).</summary>
    internal static byte Driven(bool horn = false, OpenFPS.Common.SirenMode siren = OpenFPS.Common.SirenMode.Off)
        => OpenFPS.Common.VehicleSignalBits.Encode(true, horn, siren != OpenFPS.Common.SirenMode.Off, siren);

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

    // ── P0's scenarios (docs/SPECIAL_CASES_AUDIT.md) ──────────────────────────────────────────────
    //
    // Recorded from the code as it stood at 22423a5b, and again when the replay's facing was fixed
    // (2026-10-11): they guard the behaviour as it is, the audit's bugs with it. Where a scenario's lines show a finding, its summary says which voice ids and fields, so
    // the part that fixes it knows which lines it should change and which it should not.

    /// <summary>
    /// Two seconds standing in front of a building with a road behind it: a patrol car with its siren on
    /// passing behind the building, seven cars more than the engine budget (shed to two, as a mixer at
    /// its ceiling leaves it) so they borrow, and a parked car whose driver holds the horn.
    ///
    /// S3: each borrowed voice (-700502 to -700508) is played and re-placed with Occlusion 0,
    /// EffectiveDistance 0, the global region and ApertureFactor and TransmissionBleed at 1 (the emitter's
    /// defaults), whatever its Path lines say: -700504, behind the building, has Path Occlusion 0.95, bleed
    /// 0.004 and 48.9 m. Its band gains come from the Path lines, which the Place lines do not carry.
    /// S2: the held horn (-2100509) has two apparent positions, the exhaust's in its Path lines and the
    /// grille's in its Place lines; at frame 110 it is stopped and then handed a Path and a Place. S1: the
    /// front outlets (-800501, -800509) have Place lines and no Path.
    /// </summary>
    private static void SirensAndFarTraffic(Replay r)
    {
        r.Wall(9501, new Vector3(0f, 4f, -6f), new Vector3(40f, 8f, 0.3f), "Brick");
        r.ShedEnginesTo(2);
        var patrolAt = new Vector3(-30f, 0.5f, -14f);
        var patrolV = new Vector3(15f, 0f, 0f);
        r.Car(501, "police_interceptor", patrolAt, patrolV);
        var hornAt = new Vector3(6f, 0.5f, 9f);
        r.Car(509, "v8_muscle", hornAt, Vector3.Zero);
        var cars = new (int Id, Vector3 At, Vector3 V)[]
        {
            (502, new Vector3(-30f, 0.5f, 70f), new Vector3(11f, 0f, 0f)),     // in the open, north
            (503, new Vector3(40f, 0.5f, 85f), new Vector3(-12f, 0f, 0f)),
            (504, new Vector3(-20f, 0.5f, -40f), new Vector3(10f, 0f, 0f)),    // behind the building
            (505, new Vector3(25f, 0.5f, -60f), new Vector3(-11f, 0f, 0f)),
            (506, new Vector3(150f, 0.5f, 10f), new Vector3(0f, 0f, 12f)),     // far down the street
            (507, new Vector3(-160f, 0.5f, -5f), new Vector3(0f, 0f, -10f)),
            (508, new Vector3(90f, 0.5f, 120f), new Vector3(0f, 0f, -9f)),
        };
        foreach (var c in cars) r.Car(c.Id, "v8_muscle", c.At, c.V);
        var feet = new Vector3(0f, 0f, 4f);
        for (int f = 0; f < 120; f++)
        {
            r.Listener(feet, Vector3.Zero, 180f);
            r.Move(501, patrolAt, patrolV, Driven(siren: OpenFPS.Common.SirenMode.Wail));
            r.Move(509, hornAt, Vector3.Zero, Driven(horn: f >= 20 && f < 60));
            for (int i = 0; i < cars.Length; i++)
            {
                r.Move(cars[i].Id, cars[i].At, cars[i].V);
                cars[i].At += cars[i].V * (float)Dt;
            }
            r.Tick();
            patrolAt += patrolV * (float)Dt;
        }
    }

    /// <summary>
    /// Two seconds standing in front of a building while a two-car light rail set passes behind it,
    /// sounding for a crossing. Its sources are entities (601 up); its voices are the train's own band
    /// (-2300000 down), placed by PlaceTrainVoice with the path of the source they stand for.
    /// </summary>
    private static void TrainBehindWall(Replay r)
    {
        r.Wall(9601, new Vector3(0f, 5f, -8f), new Vector3(60f, 10f, 0.3f), "Brick");
        var layout = TrainLayout.Sources(TrainProfile.ByName("light_rail"));
        const string train = "light_rail/t1";
        const float speed = 12f;
        float head = -10f;
        (Vector3 At, Vector3 V) Source(int i) => (new Vector3(head - layout[i].AlongMetres, layout[i].HeightMetres, -25f), new Vector3(speed, 0f, 0f));
        for (int i = 0; i < layout.Count; i++)
        {
            var (at, v) = Source(i);
            r.RailSource(601 + i, $"rail:{train}/{i}", layout[i].LevelDb, at, v);
        }
        var feet = new Vector3(0f, 0f, 0f);
        for (int f = 0; f < 120; f++)
        {
            for (int i = 0; i < layout.Count; i++)
            {
                var (at, v) = Source(i);
                r.Move(601 + i, at, v);
            }
            if (f == 20) r.Audio.WorldAudio.TrainSignalReceived!(train, new[] { 2f, 1f, 2f, 1f, 1f, 1f, 3f }, 6f);
            r.Listener(feet, Vector3.Zero, 180f);
            r.Tick();
            head += speed * (float)Dt;
        }
    }

    /// <summary>
    /// Two seconds walking out of a small roofed room through its doorway: outside, a tree near the door
    /// (spread over its places), a fountain's tap, an air conditioner on the wall, and nine pines 170 to
    /// 200 m off, heard as woods (WoodChorus: four of them, the pines falling in four 200 m squares).
    ///
    /// S1: the woods (-20000000 down) and their places (-245000001 down) have Play and Place lines and no
    /// Path line: the path pass skips every id under -5000, so their band gains are what they started with
    /// indoors, unchanged as you step outside. The tree's and the tap's places (-5008413 down, -5008425
    /// down) get Path lines from the path pass's branch for spread sources.
    /// </summary>
    private static void PlacesAndWoods(Replay r)
    {
        // A 6 m room, its doorway in the north wall, a slab over it.
        r.Wall(9701, new Vector3(0f, 1.5f, -3f), new Vector3(6.5f, 3f, 0.25f), "Brick");
        r.Wall(9702, new Vector3(3f, 1.5f, 0f), new Vector3(0.25f, 3f, 6.5f), "Brick");
        r.Wall(9703, new Vector3(-3f, 1.5f, 0f), new Vector3(0.25f, 3f, 6.5f), "Brick");
        r.Wall(9704, new Vector3(-1.925f, 1.5f, 3f), new Vector3(2.65f, 3f, 0.25f), "Brick");
        r.Wall(9705, new Vector3(1.925f, 1.5f, 3f), new Vector3(2.65f, 3f, 0.25f), "Brick");
        r.Wall(9706, new Vector3(0f, 2.6f, 3f), new Vector3(1.2f, 0.8f, 0.25f), "Brick");
        r.Wall(9707, new Vector3(0f, 3.1f, 0f), new Vector3(6.5f, 0.2f, 6.5f), "Concrete");
        r.Fixture(701, "foliage:park_tree", true, new Vector3(3f, 0f, 7f), 90f, 1f);
        r.Fixture(702, "water:park_fountain/elm_park/0", true, new Vector3(-3f, 0.4f, 6.5f), 120f, 1f);
        r.Fixture(703, "machine:ac_condenser", true, new Vector3(4.5f, 0.5f, 4f), 120f, 1f);
        for (int i = 0; i < 9; i++)
            r.Fixture(710 + i, "foliage:pine", true, new Vector3(-15f + 15f * (i % 3), 0f, 170f + 15f * (i / 3)), 90f, 1f);
        r.World.RefreshWoods();
        var feet = new Vector3(0f, 0f, 1.5f);
        var walking = new Vector3(0f, 0f, 1.4f);
        for (int f = 0; f < 120; f++)
        {
            r.Listener(feet, walking, 0f);
            r.Tick();
            feet += walking * (float)Dt;
        }
    }

    /// <summary>
    /// Two seconds driving north up a two-lane street with one side's wheels over the centre line,
    /// speeding up from 4 to 6 m/s, while somebody on voice chat walks out from behind a garden wall ahead
    /// on the right. The driving aids are on.
    ///
    /// S11: the centre rumble (-964003) is submitted to VoiceManager once and then re-placed from the game
    /// thread each frame with the pitch the speed asks for; VoiceManager then re-applies the emitter it
    /// holds, so a later Place line in the same frame puts Pitch and Position back to their first values. The talker (-1400704) carries its own path (CarriesPath) and gets no Path line.
    /// </summary>
    private static void TalkerAndAids(Replay r)
    {
        r.Road(9801, new Vector3(0f, -0.05f, 40f), new Vector3(6f, 0.1f, 120f), "Test Street carriageway");
        r.Wall(9802, new Vector3(8f, 1.5f, 28f), new Vector3(6f, 3f, 0.3f), "Brick");
        var at = new Vector3(0.5f, 0.15f, 0f);
        float speed = 4f;
        r.Car(801, "i4_economy", at, new Vector3(0f, 0f, speed));
        r.Player.RidingEntityId = 801;
        r.Player.RidingControls = true;
        const int talker = 802;
        var mouth = new Vector3(6f, 0f, 31f);
        var walking = new Vector3(-1.4f, 0f, 0f);
        r.Person(talker, mouth);
        var encoder = new OpenFPS.Client.Core.Platform.VoiceFrameEncoder();
        ushort sequence = 1;
        var block = new float[(int)(OpenFPS.Client.Core.Platform.VoiceCodec.Rate / ClientAudioSystem.UpdateHz)];
        long sample = 0;
        for (int f = 0; f < 120; f++)
        {
            var v = new Vector3(0f, 0f, speed);
            r.Listener(at, v, 0f);
            r.Move(801, at, v);
            r.Move(talker, mouth, walking);
            // A steady voice, a frame's worth of samples each frame, each packet in as the network hands it
            // over (a stream is made by its first packet: one made before it is let go as long silent).
            for (int i = 0; i < block.Length; i++, sample++)
                block[i] = 0.3f * MathF.Sin(2f * MathF.PI * 180f * sample / OpenFPS.Client.Core.Platform.VoiceCodec.Rate);
            encoder.Push(block, p => r.Audio.ReceiveVoice(talker, sequence++, p));
            r.Tick();
            at += v * (float)Dt;
            mouth += walking * (float)Dt;
            speed += 1f * (float)Dt;
        }
    }

    /// <summary>
    /// Two seconds driving across a walled yard (its asphalt a junction to the driving aids, so the guide
    /// beeps) with ids at the scale of the real-place maps: the car you drive is 32,598 (all of Magnolia),
    /// a loudspeaker playing a recording is 18,024 (Albany's join), and a parked patrol car with its siren
    /// on is 61,001.
    ///
    /// S6: the loudspeaker's reflection copies are -30000 - 18024·5 - slot (-120120, -120121), inside the
    /// transients' band (-100000 to -499999). The siren's voice is -900000 - 61001 = -961001, the second of
    /// the guide beep's six ids: at frame 38 the beep is a Play of "SYNTH/drive_guide" on the siren's id, and
    /// from then on that voice is re-placed every frame as the siren and as the beep in turn.
    /// </summary>
    private static void HighIds(Replay r)
    {
        r.Road(9901, new Vector3(0f, -0.25f, 0f), new Vector3(60f, 0.5f, 60f), null);
        r.Wall(9902, new Vector3(15.25f, 3f, 0f), new Vector3(0.5f, 6f, 31f), "Concrete");
        r.Wall(9903, new Vector3(-15.25f, 3f, 0f), new Vector3(0.5f, 6f, 31f), "Concrete");
        r.Wall(9904, new Vector3(0f, 3f, 15.25f), new Vector3(31f, 6f, 0.5f), "Concrete");
        r.Wall(9905, new Vector3(0f, 3f, -15.25f), new Vector3(31f, 6f, 0.5f), "Concrete");
        const int mine = 32_598, speaker = 18_024, patrol = 61_001;
        r.Fixture(speaker, "ANNOUNCE/st_louis_welcome", false, new Vector3(3f, 2f, 4f), 400f, 12f);
        var patrolAt = new Vector3(8f, 0.5f, 8f);
        r.Car(patrol, "police_interceptor", patrolAt, Vector3.Zero);
        var at = new Vector3(-10f, 0.15f, -10f);
        var east = new Vector3(3f, 0f, 0f);
        r.Car(mine, "i4_economy", at, east);
        r.Player.RidingEntityId = mine;
        r.Player.RidingControls = true;
        for (int f = 0; f < 120; f++)
        {
            r.Listener(at, east, 90f);
            r.Move(mine, at, east);
            r.Move(patrol, patrolAt, Vector3.Zero, Driven(siren: OpenFPS.Common.SirenMode.Wail));
            r.Tick();
            at += east * (float)Dt;
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
    public void SetAcousticMap(AcousticMap map) => Log("AcousticMap");
    public void PlaySpatialSound(SpatialEmitter e) { LogDelta("Play", e.EntityId, e, _emitterFields, whole: true); _latest[e.EntityId] = e; _live.Add(e.EntityId); }
    public void UpdateSpatialAttributes(SpatialEmitter e) { LogDelta("Place", e.EntityId, e, _emitterFields, whole: false); _latest[e.EntityId] = e; }
    public void SetAcousticPath(int id, AcousticPathData p) => LogDelta("Path", id, p, _pathFields, whole: false);
    public void SetSimulatedReverbDecay(float ms, float enclosure, float hf, float lf) => Log("Reverb", ms, enclosure, hf, lf);
    public void SetAirTemperature(float c) => Log("Air", c);
    /// <summary>What the mixer says its load is; a test of the budgets sets it.</summary>
    public float Load;
    public float MixerLoad => Load;
    public int Free = 256;
    public int SpatialVoicesFree => Free;
    public void ReviveEngine(int id) => Log("ReviveEngine", id);
    public bool FadeOutEngine(int id) { Log("FadeEngine", id); return true; }
    public bool FadeOutVoice(int id) { Log("FadeVoice", id); return true; }
    public void CancelVoiceFade(int id) => Log("CancelFade", id);
    public void SignalTrain(string train, float[] warning, float bellSeconds, double secondsAgo) => Log("SignalTrain", train, warning, bellSeconds, secondsAgo);
    public void PlanTrainSlot(string preset, string train, int slot, TrainSlotPlan plan) => Log("PlanTrain", preset + "/" + train, slot, plan.Sources, plan.Weights);
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
