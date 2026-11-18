using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Materials;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A tap and its plug, two controls (todo.md, "A tap and its plug", answered 2026-11-17): E turns the tap
/// alone; Shift+E at a basin you face works its plug (the knock's key, at a basin rather than a door). The
/// plug is the world's state, carried in the definition, so the synth keeps its water and its voice when it
/// changes.
/// </summary>
[Collection("Runoff")]
public class TapAndPlugTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _o;

    public TapAndPlugTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    /// <summary>Runs a synth with this flow for this long, returning its output.</summary>
    private static float[] Run(RunningWaterSynth s, float flow, float seconds)
    {
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0) { s.Flow = flow; s.Control(256f / Rate); }
            x[i] = s.Next();
        }
        return x;
    }

    // ── The water ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_basin_filled_with_the_plug_in_and_the_tap_shut_keeps_its_water_until_it_is_pulled()
    {
        var spec = RunningWaterSpec.KitchenSink;
        var s = new RunningWaterSynth(spec, Rate, 3) { Flow = 0f };
        Assert.True(s.PlugIn, "a sink is placed with its plug in");
        Run(s, spec.Tap!.OpenLitresPerSecond, 60f);
        float full = s.Level;
        _o.WriteLine($"  after a minute running: {full * 1000:F1} mm");

        Run(s, 0f, 60f);
        _o.WriteLine($"  a minute after shutting: {s.Level * 1000:F1} mm, out {s.InletFlow:F4} L/s");
        // The tap's flow glides out over a quarter second, so the last of it adds a fraction of a millimetre;
        // nothing leaves, and a minute more stands at the same height.
        Assert.InRange(s.Level, full, full + 0.0005f);
        Assert.Equal(0f, s.InletFlow);
        float shut = s.Level;
        Run(s, 0f, 60f);
        Assert.Equal(shut, s.Level, 6);

        // The plug pulled, it starts to go at once and is empty in a minute and a half.
        float before = s.Level;
        s.PlugIn = false;
        s.Control(256f / Rate);
        Assert.True(s.Level <= before && s.Level > 0.99f * before, "pulling the plug reset the water instead of letting it out");
        Run(s, 0f, 90f);
        _o.WriteLine($"  a minute and a half after pulling: {s.Level * 1000:F3} mm");
        Assert.True(s.Level < 0.0005f, $"still {s.Level * 1000:F1} mm");
    }

    [Fact]
    public void A_tap_running_with_the_plug_out_never_fills_past_what_the_inlet_passes()
    {
        var spec = RunningWaterSpec.KitchenSink with { Basin = RunningWaterSpec.KitchenSink.Basin! with { PlugIn = false } };
        var s = new RunningWaterSynth(spec, Rate, 3) { Flow = 0f };
        Assert.False(s.PlugIn);
        Run(s, spec.Tap!.OpenLitresPerSecond, 60f);
        float first = s.Level;
        Run(s, spec.Tap.OpenLitresPerSecond, 60f);
        _o.WriteLine($"  plug out, tap running: {first * 1000:F2} mm then {s.Level * 1000:F2} mm, out {s.InletFlow:F3} L/s");
        Assert.Equal(first, s.Level, 4);
        Assert.InRange(s.Level * 1000f, 1f, 5f);
        Assert.InRange(s.InletFlow, 0.11f, 0.13f);
        Assert.True(s.Level < spec.Basin!.OverflowMetres);
    }

    // ── The keys ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shift_E_at_a_basin_you_face_works_the_plug()
    {
        using var f = new Fixture();
        var player = f.Player(Vector3.Zero);
        var tap = f.Tap(new Vector3(0f, 1f, 1f));
        Assert.True(f.World.Get<SoundEmitterComponent>(tap).PlugIn, "the sink's plug starts in");

        f.Command(player, "knock");
        Assert.Equal("plug out", f.LastReply);
        Assert.False(f.World.Get<SoundEmitterComponent>(tap).PlugIn);

        f.Command(player, "knock");
        Assert.Equal("plug in", f.LastReply);
        Assert.True(f.World.Get<SoundEmitterComponent>(tap).PlugIn);
    }

    [Fact]
    public void Shift_E_at_a_door_you_face_still_knocks_beside_a_basin_you_do_not()
    {
        using var f = new Fixture();
        var player = f.Player(Vector3.Zero);
        var tap = f.Tap(new Vector3(1.1f, 1f, 0f));           // within TapReach, off to the side
        f.Door(new Vector3(0f, 1.05f, 2f));                   // faced, within the door's reach
        bool plug = f.World.Get<SoundEmitterComponent>(tap).PlugIn;

        f.Command(player, "knock");
        Assert.Equal("", f.LastReply);                         // the knock is its own answer
        Assert.Equal(plug, f.World.Get<SoundEmitterComponent>(tap).PlugIn);
    }

    [Fact]
    public void E_turns_the_tap_and_leaves_the_plug_alone()
    {
        using var f = new Fixture();
        var player = f.Player(Vector3.Zero);
        var tap = f.Tap(new Vector3(0f, 1f, 1f));
        var before = f.World.Get<SoundEmitterComponent>(tap);
        Assert.False(before.SynthRunning);

        var said = new List<string>();
        Assert.True(f.Server.ToggleTapInReach(f.World, Vector3.Zero, said.Add));
        var after = f.World.Get<SoundEmitterComponent>(tap);
        _o.WriteLine($"  said: {string.Join(" ", said)}");
        Assert.True(after.SynthRunning);
        Assert.Equal(before.PlugIn, after.PlugIn);
        Assert.Contains("turn the kitchen sink on", string.Join(" ", said));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), $"openfps-tap-plug-{Guid.NewGuid():N}");
        public readonly MapManager Maps;
        public readonly GameServer Server;
        public readonly CommandHandler Commands;
        public readonly World World;
        public readonly string MapId = "default";

        private readonly PrefabRepository _prefabs;
        private readonly SessionManager _sessions = new();
        private readonly List<string> _replies = new();

        public string LastReply = "";

        public Fixture()
        {
            string mapDir = Path.Combine(Dir, "maps");
            Directory.CreateDirectory(mapDir);
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "maps"), "*.json"))
                File.Copy(file, Path.Combine(mapDir, Path.GetFileName(file)), overwrite: true);

            _prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), _prefabs);
            Maps.Initialize();
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out _, out _));
            Server = new GameServer(new NoUsers());
            Server.Attach(Maps, _sessions);
            Commands = new CommandHandler(_sessions, Maps, Server);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
        }

        public UserSession Player(Vector3 at)
        {
            var entity = World.Create(
                new PlayerComponent { ConnectionId = 7, Username = "tester", Role = UserRole.Dev },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" });
            Maps.IndexEntity(MapId, entity);
            var session = new UserSession { ConnectionId = 7, Username = "tester", Role = UserRole.Dev, Entity = entity, CurrentMapId = MapId };
            _sessions.AddSession(7, session);
            return session;
        }

        public Entity Tap(Vector3 at)
            => Maps.SpawnEntity(MapId, w => _prefabs.Spawn(w, "kitchen_sink_water", at, Quaternion.Identity, Vector3.One));

        public Entity Door(Vector3 at)
            => Maps.SpawnEntity(MapId, w => _prefabs.Spawn(w, "door", at, Quaternion.Identity, Vector3.One));

        public void Command(UserSession session, string command, params string[] args)
        {
            _replies.Clear();
            Commands.HandleTextCommand(session.ConnectionId,
                new TextCommand { Command = command, Args = args },
                m => { if (m is TextEvent t) _replies.Add(t.Text); });
            Server.DrainCommandBuffer();
            LastReply = string.Join(" ", _replies);
        }
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => true;
        public bool VerifyPassword(string username, string password) => false;
    }
}
