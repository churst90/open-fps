using System.Globalization;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>The replays hold the aids' switches, which are the whole process's, for as long as they run.</summary>
[CollectionDefinition(nameof(SpokenLinesReplays), DisableParallelization = true)]
public class SpokenLinesReplays { }

/// <summary>
/// The spoken-lines replay (docs/ENGINE_PACKAGES.md, stage 0): scripted walks on pieces of the shipped maps,
/// through the client's own session and its aids (the arrival line, zones, stairs, sight and turns, wall
/// bumps, the look-ahead, the tracker, the beacons), compared with a stored list of every line given to the
/// speech output and every cue started, each with its tick. A package that takes an aid out of the session
/// must leave every list as it is.
///
/// An intended change to what is said regenerates the list in the same commit, saying why:
/// OPENFPS_REPLAY_WRITE=1 dotnet test --filter SpokenLinesReplayTests. OPENFPS_REPLAY_DUMP=dir writes each
/// run's list with where the body was at each stage of its script, for reading.
/// </summary>
[Collection(nameof(SpokenLinesReplays))]
public class SpokenLinesReplayTests
{
    private readonly ITestOutputHelper _o;
    public SpokenLinesReplayTests(ITestOutputHelper o) => _o = o;

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    private static string StoredFolder => Path.Combine(RepoRoot(), "OpenFPS.Tests", "EnginePackages", "spoken");
    private static string StoredPath(string scenario) => Path.Combine(StoredFolder, scenario + ".txt");

    public static IEnumerable<object[]> Scenarios() => SpokenReplay.Scenarios.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void WhatIsSaidIsUnchanged(string scenario)
    {
        var replay = SpokenReplay.Run(SpokenReplay.Scenarios[scenario]);
        var lines = replay.Lines;
        _o.WriteLine($"{scenario}: {lines.Count(l => l.Contains(" say"))} lines said, {lines.Count(l => l.Contains(" cue ") || l.Contains(" ui"))} cues, {replay.Tick} ticks");
        if (Environment.GetEnvironmentVariable("OPENFPS_REPLAY_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, scenario + ".spoken.txt"), string.Join("\n", replay.LinesWithNotes) + "\n");
        }
        string path = StoredPath(scenario);
        if (Environment.GetEnvironmentVariable("OPENFPS_REPLAY_WRITE") == "1")
        {
            Directory.CreateDirectory(StoredFolder);
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            return;
        }
        Assert.True(File.Exists(path), $"{scenario} has no stored list: run with OPENFPS_REPLAY_WRITE=1 and commit it.");
        var stored = File.ReadAllLines(path);
        int first = Enumerable.Range(0, Math.Min(stored.Length, lines.Count)).FirstOrDefault(i => stored[i] != lines[i], -1);
        if (first < 0 && stored.Length == lines.Count) return;
        if (first < 0) first = Math.Min(stored.Length, lines.Count);
        string At(IReadOnlyList<string> l, int i) => i < l.Count ? l[i] : "(nothing: the list ends)";
        Assert.Fail($"{scenario}: what is said differs from the stored list at line {first + 1} of {stored.Length} (now {lines.Count}).\n"
            + $"  stored: {At(stored, first)}\n  now:    {At(lines, first)}\n"
            + (first > 0 ? $"  after:  {stored[first - 1]}\n" : "")
            + "If the change is meant, regenerate with OPENFPS_REPLAY_WRITE=1 and say why in the commit.");
    }

    [Fact]
    public void EveryScenarioIsStoredAndNothingElse()
    {
        if (Environment.GetEnvironmentVariable("OPENFPS_REPLAY_WRITE") == "1") return;
        var stored = Directory.Exists(StoredFolder)
            ? Directory.GetFiles(StoredFolder, "*.txt").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : new List<string>();
        Assert.Equal(SpokenReplay.Scenarios.Keys.OrderBy(n => n, StringComparer.Ordinal), stored);
    }

    /// <summary>
    /// A run says the same thing on the same tick every time: nothing in it waits on the wall clock or on a
    /// worker. The first seconds of a walk, twice over; the stored lists hold the whole walks.
    /// </summary>
    [Fact]
    public void TheSameRunSaysTheSame()
    {
        static List<string> Once() => SpokenReplay.Run(r =>
        {
            SpokenReplay.ArriveAtSelbyStairs(r);
            r.Wait(1.5);
            r.Tap(GameKey.F);
            r.Hold(GameKey.W, 3.0);
            r.Tap(GameKey.P);
            r.Wait(1.0);
        }).Lines;
        var a = Once();
        var b = Once();
        Assert.Equal(a, b);
        // And it is a run: the arrival, the stair cue, a compass line, a zone, a beacon.
        Assert.Contains(a, l => l.EndsWith(" say! You're in city, at Selby House landing, floor 0.", StringComparison.Ordinal));
        Assert.Contains(a, l => l.Contains(" say Stairs up, 19 steps, to floor 1", StringComparison.Ordinal));
        Assert.Contains(a, l => l.Contains(" say! North", StringComparison.Ordinal));
        Assert.Contains(a, l => l.Contains(" say Selby House landing, floor 1", StringComparison.Ordinal));
        Assert.Contains(a, l => l.Contains(" cue Beacon ", StringComparison.Ordinal));
        Assert.DoesNotContain(a, l => l.Contains(" # ", StringComparison.Ordinal));
    }
}

/// <summary>
/// One run of the client's session on a piece of a shipped map, with a script at the keys: every line given to
/// the speech output and every cue started, each with the tick it came on. The session is the game's own
/// (its aids, its bindings, its prediction), on a clock the replay steps; the world is the map's own
/// definitions as the server sends them; nothing the server would say after the spawn is said.
/// </summary>
internal sealed class SpokenReplay
{
    /// <summary>The game's tick, and two audio frames to each, as the client's loop runs them.</summary>
    public const float Dt = PhysicsConstants.FixedDeltaTime;
    private const int FramesPerTick = 2;

    /// <summary>The player's own entity: above every id a shipped map gives out.</summary>
    public const int OwnId = 900001;

    private readonly List<(string Line, bool Note)> _lines = new();

    /// <summary>What was said and which cues were started, in order: "tick say text" (said after what is being
    /// said), "tick say! text" (said at once, over it), "tick interrupt", "tick ui id", "tick cue kind sound".</summary>
    public List<string> Lines => _lines.Where(l => !l.Note).Select(l => l.Line).ToList();

    /// <summary>The same with the script's own notes of where the body was ("tick # what at x,y,z").</summary>
    public List<string> LinesWithNotes => _lines.Select(l => l.Line).ToList();

    public readonly ClientGameSession Session;
    public int Tick { get; private set; }
    public double Now { get; private set; } = 1.0;

    private readonly StreamMixer _mixer = new();
    private readonly AudioEngineFacade _facade;
    private readonly Dictionary<int, VoiceKind> _kinds = new();
    private int _mixerRead;

    /// <summary>The kinds of voice that are cues: what an aid starts to tell the player something. A knock on
    /// a wall walked into is a render, played when its render is done on a worker, and is not in the list; the
    /// wall's name, said with it, is.</summary>
    private static readonly HashSet<VoiceKind> Cues = new() { VoiceKind.Beacon, VoiceKind.Aid };

    private SpokenReplay()
    {
        _facade = new AudioEngineFacade(_mixer, new SteppedAudioClock(() => Now));
        // A still day: the weather's wind is read on the wall clock.
        _facade.Weather.Wind = WindWeather.Steady(0f, 0f, 0f);
        _facade.InitializeForTest();
        var network = new ClientNetworkService { Sending = _ => { } };
        Session = new ClientGameSession(network, new Speech(this), new Shell(), _facade,
            new NullMicrophoneCapture("no microphone in a replay"), enableAudio: false, replayClock: () => Now);
        // Built where they are asked for, not on a worker: the same world on every tick of every run.
        Session.World.Geometry.Runner = work => work();
        Session.World.RefreshRunner = work => work();
        Session.AudioSystemForTest.Voices.OnGiven = (id, key) => _kinds[id] = key.Kind;
    }

    /// <summary>A script run with the aids' switches at the game's defaults whatever ran before, and put back
    /// after.</summary>
    public static SpokenReplay Run(Action<SpokenReplay> script)
    {
        AcousticRegistry.Initialize();
        var saved = (NavigationAids.TurnNarration, NavigationAids.WallBumps, NavigationAids.AimAssist, NavigationAids.Track,
                     EditorKeys.Enabled,
                     (DrivingCues.Enabled, DrivingCues.Guide, DrivingCues.LineSensors, DrivingCues.TurnClicks,
                      DrivingCues.BrakeCue, DrivingCues.SpeedWarning));
        (NavigationAids.TurnNarration, NavigationAids.WallBumps, NavigationAids.AimAssist, NavigationAids.Track) =
            (true, true, true, TrackCategory.Doors);
        EditorKeys.Enabled = false;
        (DrivingCues.Enabled, DrivingCues.Guide, DrivingCues.LineSensors, DrivingCues.TurnClicks,
         DrivingCues.BrakeCue, DrivingCues.SpeedWarning) = (true, true, true, true, true, true);
        try
        {
            var r = new SpokenReplay();
            script(r);
            r.Drain();
            return r;
        }
        finally
        {
            (NavigationAids.TurnNarration, NavigationAids.WallBumps, NavigationAids.AimAssist, NavigationAids.Track,
             EditorKeys.Enabled,
             (DrivingCues.Enabled, DrivingCues.Guide, DrivingCues.LineSensors, DrivingCues.TurnClicks,
              DrivingCues.BrakeCue, DrivingCues.SpeedWarning)) = saved;
        }
    }

    private sealed class Speech : ISpeechOutput
    {
        private readonly SpokenReplay _r;
        public Speech(SpokenReplay r) => _r = r;
        public string BackendName => "replay";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) { _r.Drain(); _r.Add($"{(interrupt ? "say!" : "say")} {text}"); }
        public void Interrupt() { _r.Drain(); _r.Add("interrupt"); }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public bool IsGameInputActive { get; set; } = true;
        public bool? NumLockOn => true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }

    private void Add(string what, bool note = false) => _lines.Add(($"{Tick} {what}", note));

    /// <summary>What the mixer was told since the last look, kept where it is a cue: an interface sound, or a
    /// voice of a cue's kind starting. In the order it was told, among the lines said.</summary>
    private void Drain()
    {
        for (; _mixerRead < _mixer.Lines.Count; _mixerRead++)
        {
            var parts = _mixer.Lines[_mixerRead].Split(' ');
            if (parts.Length < 3) continue;
            switch (parts[1])
            {
                case "Ui": Add($"ui {parts[2].Trim('"')}"); break;
                case "UiLoop": Add($"ui-loop {parts[2].Trim('"')} {(parts.Length > 3 ? parts[3].Trim('"') : "")}"); break;
                case "StopUiLoop": Add($"ui-loop-stop {parts[2].Trim('"')}"); break;
                case "Play":
                    if (int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                        && _kinds.TryGetValue(id, out var kind) && Cues.Contains(kind))
                        Add($"cue {kind} {parts.FirstOrDefault(p => p.StartsWith("SoundId=", StringComparison.Ordinal))?["SoundId=".Length..].Trim('"')}");
                    break;
            }
        }
    }

    // ── The world ────────────────────────────────────────────────────────────────

    /// <summary>The shipped maps, loaded once for every replay of a run.</summary>
    private static readonly Lazy<MapManager> Maps = new(() =>
    {
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        return maps;
    });

    /// <summary>
    /// Arrives on <paramref name="map"/> as a client does, holding every definition within
    /// <paramref name="metres"/> on the ground of <paramref name="at"/>: the manifest, the definitions in
    /// batches, the rooms worked out, then the spawn at <paramref name="at"/>. A body arrives facing north.
    /// </summary>
    public void Arrive(string map, Vector3 at, float metres)
    {
        if (!Maps.Value.TryGetMap(map, out var world, out var size, out _, out _) || !Maps.Value.TryGetMapData(map, out var data))
            throw new InvalidOperationException($"no map {map}");
        var defs = EntityDefinitionFactory.StaticDefinitions(world).Where(d => Near(d, at, metres)).OrderBy(d => d.EntityId).ToList();
        var spawn = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One };
        Session.HandleMessage(new MapManifest
        {
            MapName = map, WorldSize = size, ExpectedEntityCount = defs.Count, SpawnPoint = spawn,
            VoxelResolution = data.VoxelResolution, OcclusionFloor = data.OcclusionFloor, MinimumY = data.MinimumY,
            MapMin = data.MinBound, MapMax = data.MaxBound, PlayMin = data.WalkMin, PlayMax = data.WalkMax, Gravity = data.Gravity,
            BeaconPolicy = data.BeaconPolicy == null ? Array.Empty<string>() : data.BeaconPolicy.Select(kv => $"{kv.Key}={kv.Value}").ToArray(),
            Temperature = data.Temperature, Humidity = data.Humidity, AirPressure = data.AirPressure,
            AirAbsorptionMultiplier = data.AirAbsorptionMultiplier,
        });
        for (int i = 0; i < defs.Count; i += 200)
            Session.HandleMessage(new EntityDefinitionBatch { Definitions = defs.Skip(i).Take(200).ToList() });
        // The rooms, as the session works them out when the map has loaded, here and now and not on a worker.
        var snapshot = Session.World.GetSnapshot();
        Session.World.SetAcousticMap(ClientWorldState.BuildAcousticMap(snapshot.Entities.Values.Select(e => e.Definition),
            Session.World.CurrentMapSize, data.MinBound, data.VoxelResolution, data.OcclusionFloor, streamed: false, report: false,
            geometry: OpenFPS.Common.Geometry.TriangleGeometry.Enabled ? Session.World.Geometry.Built : null));
        Session.HandleMessage(new PlayerSpawned { EntityId = OwnId, SpawnTransform = spawn });
    }

    private static bool Near(EntityDefinition d, Vector3 at, float metres)
    {
        var p = d.Transform.Position;
        var half = d.Collider.Size * 0.5f;
        // A floor or a road under the place is near it however far off its middle is.
        float dx = MathF.Max(0f, MathF.Abs(p.X - at.X) - half.X), dz = MathF.Max(0f, MathF.Abs(p.Z - at.Z) - half.Z);
        return dx * dx + dz * dz <= metres * metres;
    }

    // ── The keys ─────────────────────────────────────────────────────────────────

    /// <summary>One tick of the game: the step, then the audio frames with what they say.</summary>
    public void Step()
    {
        Tick++;
        _mixer.Frame = Tick;
        Session.SimStep(Dt);
        for (int f = 0; f < FramesPerTick; f++)
        {
            Now += Dt / FramesPerTick;
            Session.ContinuousUpdate();
            Session.AudioSystemForTest.AcousticWorker.StepForTest();
            _facade.PumpForTest();
            _facade.PumpForTest();
        }
        Drain();
    }

    public void Wait(double seconds) { for (int i = 0, n = (int)Math.Round(seconds / Dt); i < n; i++) Step(); }

    /// <summary>Holds <paramref name="key"/> down for <paramref name="seconds"/>, then lets it go.</summary>
    public void Hold(GameKey key, double seconds)
    {
        Session.Input.SetKey(key, true);
        Wait(seconds);
        Session.Input.SetKey(key, false);
    }

    /// <summary>A tap: down for one tick, up for the next.</summary>
    public void Tap(GameKey key)
    {
        Session.Input.SetKey(key, true);
        Step();
        Session.Input.SetKey(key, false);
        Step();
    }

    /// <summary>A tap with Shift held.</summary>
    public void ShiftTap(GameKey key)
    {
        Session.Input.SetKey(GameKey.ShiftLeft, true);
        Step();
        Tap(key);
        Session.Input.SetKey(GameKey.ShiftLeft, false);
        Step();
    }

    /// <summary>Turns a quarter of a circle to the right (L twice) or the left (J twice), a tap for each
    /// eighth, and waits for what is ahead to be said.</summary>
    public void QuarterTurn(bool right)
    {
        for (int i = 0; i < 2; i++) { Tap(right ? GameKey.L : GameKey.J); Wait(0.8); }
    }

    /// <summary>Where the body is, for somebody reading a dump; never in the stored list.</summary>
    public void Note(string what)
    {
        Drain();
        var p = Session.PlayerState.Position;
        Add(string.Create(CultureInfo.InvariantCulture, $"# {what} at {p.X:F1},{p.Y:F1},{p.Z:F1}"), note: true);
    }

    // ── The scenarios ────────────────────────────────────────────────────────────

    /// <summary>Every scenario: a name and the script that arrives somewhere and works the keys.</summary>
    public static readonly Dictionary<string, Action<SpokenReplay>> Scenarios = new()
    {
        ["selby_stairs"] = SelbyStairs,
        ["main_street"] = MainStreet,
    };

    /// <summary>On the ground-floor landing of Selby House's stairwell on the city, a step short of the foot of
    /// its first flight, which climbs north.</summary>
    public static void ArriveAtSelbyStairs(SpokenReplay r) => r.Arrive("city", new Vector3(10.6f, 0.3f, 148.9f), 35f);

    /// <summary>
    /// Up two flights of Selby House: the arrival on a named landing, the compass, the first flight's cue at
    /// its foot and what comes in front on the way up, the landing at its top by name, two quarter turns to
    /// the next flight with what is ahead after each, that flight's cue, the wall at its top walked into, the
    /// look-ahead, the tracker's nearest door and the zone by its key. The door and stair beacons sound all
    /// the way.
    /// </summary>
    private static void SelbyStairs(SpokenReplay r)
    {
        ArriveAtSelbyStairs(r);
        r.Wait(2.0);
        r.Note("arrived");
        r.Tap(GameKey.F);
        r.Hold(GameKey.W, 3.0);
        r.Note("up the first flight");
        r.Wait(1.0);
        r.QuarterTurn(right: true);
        r.Note("facing east");
        r.Hold(GameKey.W, 0.55);
        r.Note("across the landing");
        r.QuarterTurn(right: true);
        r.Note("facing south");
        r.Hold(GameKey.W, 3.0);
        r.Note("up the second flight, at the wall");
        r.Wait(1.0);
        r.Tap(GameKey.P);
        r.Wait(1.0);
        r.Tap(GameKey.Period);
        r.Wait(1.5);
        r.Tap(GameKey.Z);
        r.Wait(1.0);
        r.Note("end");
    }

    /// <summary>
    /// Along Main Street's sidewalk outside Selby House and off it: the arrival in a zone, north along the
    /// pavement with what comes in front, a quarter turn to the front entrance and into it, the tracker
    /// stepped and its kind changed, where you are in numbers, the narration turned off and on, then west off
    /// the kerb into the street's own zone.
    /// </summary>
    private static void MainStreet(SpokenReplay r)
    {
        r.Arrive("city", new Vector3(7.8f, 0.4f, 150.0f), 35f);
        r.Wait(2.0);
        r.Note("arrived");
        r.Hold(GameKey.W, 1.6);
        r.Note("north along the sidewalk");
        r.QuarterTurn(right: true);
        r.Note("facing the entrance");
        r.Hold(GameKey.W, 1.0);
        r.Note("at the door");
        r.Wait(0.5);
        r.Tap(GameKey.Period);
        r.Wait(1.5);
        r.ShiftTap(GameKey.Period);
        r.Wait(1.5);
        r.Tap(GameKey.Period);
        r.Wait(1.5);
        r.Tap(GameKey.C);
        r.Wait(1.0);
        r.Tap(GameKey.N);
        r.Wait(1.0);
        r.QuarterTurn(right: false);
        r.Tap(GameKey.N);
        r.Wait(1.0);
        r.QuarterTurn(right: false);
        r.Note("facing west");
        r.Hold(GameKey.W, 1.5);
        r.Note("in the street");
        r.Wait(1.5);
        r.Tap(GameKey.Z);
        r.Wait(1.0);
        r.Note("end");
    }
}
