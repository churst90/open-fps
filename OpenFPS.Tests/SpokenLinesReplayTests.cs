using System.Globalization;
using System.Numerics;
using OpenFPS.Audio.Core;
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
using OpenFPS.Sound;
using OpenFPS.Materials;
using OpenFPS.Physics;

namespace OpenFPS.Tests;

/// <summary>The replays hold the aids' switches, which are the whole process's, for as long as they run.</summary>
[CollectionDefinition(nameof(SpokenLinesReplays), DisableParallelization = true)]
public class SpokenLinesReplays { }

/// <summary>
/// The spoken-lines replay (docs/ENGINE_PACKAGES.md, stage 0): scripted walks and a drive on pieces of the
/// shipped maps, through the client's own session and its aids (the arrival line, zones, stairs, sight and
/// turns, wall bumps, the look-ahead, the tracker, the beacons, the driving cues), compared with a stored list
/// of every line given to the speech output and every cue started, each with its tick. A package that takes an
/// aid out of the session must leave every list as it is.
///
/// An intended change to what is said regenerates the list in the same commit, saying why:
/// OPENFPS_REPLAY_WRITE=1 dotnet test --filter SpokenLinesReplayTests. OPENFPS_REPLAY_DUMP=dir writes each
/// run's list with where the body was at each stage of its script, and everything the mixer was told, for
/// reading.
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
            File.WriteAllText(Path.Combine(dump, scenario + ".mixer.txt"), string.Join("\n", replay.MixerLines) + "\n");
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

    /// <summary>The drive, twice over: the scripted server answers on the same ticks, and the cues that run on
    /// the car's motion (the guide, the sensors, the clicks, the brake cue, the relay) come on the same
    /// ticks.</summary>
    [Fact]
    public void TheSameDriveSaysTheSame()
    {
        var a = SpokenReplay.Run(SpokenDrive.GarageToCentralStreet).Lines;
        var b = SpokenReplay.Run(SpokenDrive.GarageToCentralStreet).Lines;
        Assert.Equal(a, b);
        // And it is a drive: in, the road and the junction ahead, the indicator, the next road, out.
        Assert.Contains(a, l => l.Contains(" say You are in the driver seat of ", StringComparison.Ordinal));
        Assert.Contains(a, l => l.EndsWith(" say Main Street, heading south, limit 50.", StringComparison.Ordinal));
        Assert.Contains(a, l => l.Contains(" say Junction in ", StringComparison.Ordinal));
        Assert.Contains(a, l => l.EndsWith(" say! Right indicator.", StringComparison.Ordinal));
        Assert.Contains(a, l => l.EndsWith(" say Central Street, heading west, limit 50.", StringComparison.Ordinal));
        Assert.Contains(a, l => l.Contains(" say You get out of ", StringComparison.Ordinal));
        foreach (var cue in new[] { "drive_guide", "drive_kerb", "drive_turn", "drive_relay_on", "drive_relay_off" })
            Assert.Contains(a, l => l.EndsWith(" cue Aid SYNTH/" + cue, StringComparison.Ordinal));
    }

    /// <summary>
    /// Walking into a wall knocks on it: up Selby House's stairs into the brick wall at the top of the second
    /// flight, the wall's name is said and a strike of the wall's own material starts (StruckThings, rendered
    /// on a worker). The stored lists leave the knock out, since a worker's render lands on no fixed tick;
    /// here the replay waits for each render before the next tick, so the knock must start within a few
    /// ticks of the bump.
    /// </summary>
    [Fact]
    public void WalkingIntoAWallKnocks()
    {
        const string wall = "Selby House south wall";
        var r = SpokenReplay.Run(r =>
        {
            r.AwaitRenders = true;
            SpokenReplay.SelbyToTheWall(r);
            r.Wait(1.0);
        });
        // The bump says the wall's name over everything, and only the bump says it bare.
        var bump = r.Lines.Where(l => l.EndsWith(" say! " + wall, StringComparison.Ordinal)).ToList();
        Assert.True(bump.Count == 1, "the walk did not bump the wall once:\n" + string.Join("\n", r.Lines));
        int bumpTick = int.Parse(bump[0].Split(' ')[0], CultureInfo.InvariantCulture);

        var knocks = r.MixerLines
            .Select(l => (Line: l, Id: System.Text.RegularExpressions.Regex.Match(l, "SoundId=\"synth:(strike:[^\"]*)\"")))
            .Where(k => k.Id.Success && k.Line.Split(' ')[1] == "Play")
            .Select(k => (Tick: int.Parse(k.Line.Split(' ')[0], CultureInfo.InvariantCulture), Key: k.Id.Groups[1].Value))
            .ToList();
        _o.WriteLine($"bump at tick {bumpTick}; {knocks.Count} voices of the knock: "
                     + string.Join("; ", knocks.Select(k => $"{k.Tick} {k.Key}").Distinct()));
        // The knock and the room's answer to it (its reflections, "~wash" the diffuse ones): one strike.
        Assert.NotEmpty(knocks);
        Assert.InRange(knocks[0].Tick, bumpTick, bumpTick + 3);
        var key = Assert.Single(knocks.Select(k => k.Key.Split('~')[0]).Distinct());

        // Of the wall's own stuff: what the map says the wall is made of.
        var made = r.Session.World.GetSnapshot().Entities.Values
            .Where(e => e.Definition.Identity.Name == wall).Select(e => e.Definition.Material.Material).Distinct().ToList();
        Assert.Equal(new[] { "Brick" }, made);
        Assert.True(StruckThings.TryParseKey(key, out var strike), key);
        Assert.Equal("Brick", strike.Thing.Material);
        Assert.Contains(strike.Blows, b => b.Striker.Name == "body");
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

    /// <summary>Everything the session sent the server, in order. A scripted server reads it (DriveServer).</summary>
    public readonly List<IMessage> Sent = new();

    /// <summary>The server's half of the run, called at the start of every tick before the session steps: what
    /// it answers and the states it sends. Null for a walk, where nothing is said after the spawn.</summary>
    public Action? Server;

    /// <summary>Waits after each tick for every render a worker is making, so a sound that waits on one starts
    /// on the next tick (the knock). Never in a stored list's run: the game does not wait.</summary>
    public bool AwaitRenders;

    /// <summary>Every line the mixer was told, each starting with its tick.</summary>
    public IReadOnlyList<string> MixerLines => _mixer.Lines;
    public int Tick { get; private set; }
    public double Now { get; private set; } = 1.0;

    private readonly StreamMixer _mixer = new();
    private readonly AudioEngineFacade _facade;
    private readonly Dictionary<int, VoiceKind> _kinds = new();
    private int _mixerRead;

    /// <summary>The kinds of voice that are cues: what an aid starts to tell the player something. A knock on
    /// a wall walked into is a render, played when its render is done on a worker, and is not in the list; the
    /// wall's name, said with it, is. <see cref="SpokenLinesReplayTests.WalkingIntoAWallKnocks"/> holds the
    /// knock.</summary>
    private static readonly HashSet<VoiceKind> Cues = new() { VoiceKind.Beacon, VoiceKind.Aid };

    private SpokenReplay()
    {
        _facade = new AudioEngineFacade(_mixer, new SteppedAudioClock(() => Now));
        // A still day: the weather's wind is read on the wall clock.
        _facade.Weather.Wind = WindWeather.Steady(0f, 0f, 0f);
        _facade.InitializeForTest();
        var network = new ClientNetworkService { Sending = Sent.Add };
        Session = new ClientGameSession(network, new Speech(this), new Shell(), _facade,
            new NullMicrophoneCapture("no microphone in a replay"), enableAudio: false, replayClock: () => Now);
        // Built where they are asked for, not on a worker: the same world on every tick of every run.
        Session.World.Geometry.Runner = work => work();
        Session.World.RefreshRunner = work => work();
        Session.AudioSystemForTest.World.Voices.OnGiven = (id, key) => _kinds[id] = key.Kind;
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
                // A start, or a cue voice given another sound while the mixer still holds it: the indicator's
                // relay keeps one voice, and this mixer never lets a one-shot end, where the game's starts
                // each tick and tock afresh.
                case "Play":
                case "Place":
                    if (int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                        && _kinds.TryGetValue(id, out var kind) && Cues.Contains(kind)
                        && parts.FirstOrDefault(p => p.StartsWith("SoundId=", StringComparison.Ordinal)) is { } sound)
                        Add($"cue {kind} {sound["SoundId=".Length..].Trim('"')}");
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
    /// <paramref name="maps"/> is the shipped maps unless a scenario brings its own (the drive's, with the
    /// parked cars placed); with <paramref name="roads"/> the map's roads come before the spawn, as the server
    /// sends them at the end of a join.
    /// </summary>
    public void Arrive(string map, Vector3 at, float metres, MapManager? maps = null, bool roads = false)
    {
        maps ??= Maps.Value;
        if (!maps.TryGetMap(map, out var world, out var size, out _, out _) || !maps.TryGetMapData(map, out var data))
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
            geometry: OpenFPS.Geometry.TriangleGeometry.Enabled ? Session.World.Geometry.Built : null));
        // Without the level crossings, which the server's CrossingSystem lays from the rails: none is near
        // the drive (RoadsFor gives the tests' rig the same).
        if (roads)
        {
            var roadData = MapRoadsBuilder.Build(data, Array.Empty<CrossingRails>());
            Session.HandleMessage(new MapRoads { MapName = map, Json = roadData.IsEmpty ? "" : roadData.ToJson() });
        }
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
        Server?.Invoke();
        Session.SimStep(Dt);
        for (int f = 0; f < FramesPerTick; f++)
        {
            Now += Dt / FramesPerTick;
            Session.ContinuousUpdate();
            Session.AudioSystemForTest.World.AcousticWorker.StepForTest();
            _facade.PumpForTest();
            _facade.PumpForTest();
        }
        Drain();
        if (AwaitRenders) WaitForRenders();
    }

    private static readonly System.Reflection.FieldInfo RenderedField = typeof(OneOffSounds)
        .GetField("_rendered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    /// <summary>Until every render the world's sounds asked a worker for is back, or a minute has gone.</summary>
    private void WaitForRenders()
    {
        var player = Session.AudioSystemForTest.WorldAudio.Sounds;
        var rendered = (System.Collections.ICollection)RenderedField.GetValue(player)!;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (rendered.Count < player.RendersOutstanding && waited.Elapsed < TimeSpan.FromMinutes(1))
            Thread.Sleep(2);
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
        ["garage_drive"] = SpokenDrive.GarageToCentralStreet,
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
        SelbyToTheWall(r);
        r.Wait(1.0);
        r.Tap(GameKey.P);
        r.Wait(1.0);
        r.Tap(GameKey.Period);
        r.Wait(1.5);
        r.Tap(GameKey.Z);
        r.Wait(1.0);
        r.Note("end");
    }

    /// <summary>Selby House's walk as far as the brick wall at the top of the second flight, walked into.</summary>
    public static void SelbyToTheWall(SpokenReplay r)
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
