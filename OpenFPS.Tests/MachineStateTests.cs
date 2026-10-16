using System.Globalization;
using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Machine state from the server (docs/SPECIAL_CASES_AUDIT.md, S16 and P7): a train's notch, an aeroplane's power
/// lever and a patrol car's siren are worked out by the server that drives them and sent, and the client plays
/// what it is sent. The client guessed all three from motion: a notch from a speed differenced between two
/// network states, a lever from the climb angle and an acceleration, a siren from a controller of its own
/// started when it first saw the car.
/// </summary>
public class MachineStateTests
{
    private readonly ITestOutputHelper _o;
    public MachineStateTests(ITestOutputHelper o) => _o = o;

    // ── The laws ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_trains_notch_is_what_its_driver_wants_of_the_speed()
    {
        // Standing at a platform: none, whatever it is about to do.
        Assert.Equal(0f, MachinePower.TrainNotch(dwelling: true, 0f, 20f));
        // Wanting more speed than it has: full, from rest and at speed.
        Assert.Equal(MachinePower.TrainPulling, MachinePower.TrainNotch(false, 0f, 12f));
        Assert.Equal(MachinePower.TrainPulling, MachinePower.TrainNotch(false, 11.9f, 12f));
        // Holding the speed the line allows: a little.
        Assert.Equal(MachinePower.TrainHolding, MachinePower.TrainNotch(false, 12f, 12f));
        // Wanting less: none. Down a grade at a steady speed with the brakes on, the client read this as holding.
        Assert.Equal(0f, MachinePower.TrainNotch(false, 12f, 11.9f));
        // Stood on the line with nowhere to go: none.
        Assert.Equal(0f, MachinePower.TrainNotch(false, 0f, 0f));
    }

    [Fact]
    public void An_aeroplanes_lever_in_the_air_is_its_flight_path()
    {
        float Lever(float degrees, float speed = 70f)
            => MachinePower.FlightLever(new Vector3(0f, speed * MathF.Sin(degrees * MathF.PI / 180f), speed * MathF.Cos(degrees * MathF.PI / 180f)));
        Assert.Equal(0.62f, Lever(0f), 3);                      // level: cruise
        Assert.Equal(1f, Lever(8f));                            // climbing: everything
        Assert.InRange(Lever(3f), 0.75f, 0.9f);
        Assert.Equal(MachinePower.PistonIdle, Lever(-6f));      // coming down: idle
        Assert.InRange(Lever(-2f), 0.3f, 0.4f);
        Assert.True(Lever(-1f) < Lever(0f) && Lever(0f) < Lever(1f));
        Assert.Equal(MachinePower.GroundStanding, MachinePower.FlightLever(Vector3.Zero));
    }

    [Fact]
    public void An_aeroplanes_lever_on_its_wheels_is_its_ground_run()
    {
        // A jet: reversers from speed on the roll-out, then the brakes; a little to taxi; idle to hold; all of it to go.
        Assert.Equal(MachinePower.GroundReversing, MachinePower.GroundLever(AircraftGroundRun.Phase.Rollout, 60f, AircraftPower.Turbofan));
        Assert.Equal(MachinePower.GroundTaxi, MachinePower.GroundLever(AircraftGroundRun.Phase.Rollout, 20f, AircraftPower.Turbofan));
        Assert.Equal(MachinePower.GroundTaxi, MachinePower.GroundLever(AircraftGroundRun.Phase.Taxi, 8f, AircraftPower.Turbofan));
        Assert.Equal(MachinePower.GroundStanding, MachinePower.GroundLever(AircraftGroundRun.Phase.Hold, 0f, AircraftPower.Turbofan));
        Assert.Equal(1f, MachinePower.GroundLever(AircraftGroundRun.Phase.TakeoffRoll, 5f, AircraftPower.Turbofan));
        // A piston aeroplane has no reverser: idle through all of it but the take-off roll.
        foreach (var phase in new[] { AircraftGroundRun.Phase.Rollout, AircraftGroundRun.Phase.Taxi, AircraftGroundRun.Phase.Hold })
            Assert.Equal(MachinePower.PistonIdle, MachinePower.GroundLever(phase, 40f, AircraftPower.Piston));
        Assert.Equal(1f, MachinePower.GroundLever(AircraftGroundRun.Phase.TakeoffRoll, 5f, AircraftPower.Piston));
    }

    // ── The wire ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The laws' own values cross the wire exactly (steps of a two-hundredth), anything else to the
    /// nearest step, and the lever is the last thing in an entity's state: appended, as the wire needs.</summary>
    [Fact]
    public void A_lever_crosses_the_wire()
    {
        foreach (float lever in new[] { 0f, MachinePower.PistonIdle, MachinePower.GroundStanding, MachinePower.GroundTaxi,
                                        MachinePower.GroundReversing, MachinePower.TrainPulling, 1f })
            Assert.Equal(lever, MachinePower.Decode(MachinePower.Encode(lever)));
        Assert.InRange(MachinePower.Decode(MachinePower.Encode(0.777f)), 0.774f, 0.78f);
        Assert.Equal(1f, MachinePower.Decode(MachinePower.Encode(3f)));
        Assert.Equal(0f, MachinePower.Decode(MachinePower.Encode(-1f)));
        Assert.Equal(1f, MachinePower.Decode(255));

        var update = new ServerStateUpdate
        {
            States = new List<EntityState> { new EntityState { EntityId = 7, LinearVelocity = new Vector3(1f, 2f, 3f), Signals = 6,
                                                 SpeedRate = -40, TurnY = 9, Lever = MachinePower.Encode(MachinePower.TrainPulling) } },
        };
        var back = (ServerStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(update))!;
        var state = Assert.Single(back.States);
        Assert.Equal(MachinePower.TrainPulling, state.LeverFraction);
        Assert.Equal(6, state.Signals);
        Assert.Equal(-40, state.SpeedRate);
        Assert.Equal(9, state.TurnY);

        // The last member declared, so nothing after it is renumbered and an entity that says nothing reads as 0.
        var fields = typeof(EntityState).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                                        .OrderBy(f => f.MetadataToken).Select(f => f.Name).ToList();
        Assert.Equal(nameof(EntityState.Lever), fields[^1]);
        Assert.Equal(0f, new EntityState().LeverFraction);
    }

    /// <summary>The client keeps a lever it is sent and puts it in every snapshot.</summary>
    [Fact]
    public void The_world_keeps_the_lever_it_is_sent()
    {
        var world = new ClientWorldState();
        world.RegisterDefinition(new EntityDefinition { EntityId = 5, Type = EntityType.NPC, Moves = true });
        world.SyncState(new[] { new EntityState { EntityId = 5, Lever = MachinePower.Encode(MachinePower.GroundReversing) } });
        Assert.Equal(MachinePower.GroundReversing, world.GetSnapshot().Entities[5].Lever);
        world.SyncState(new[] { new EntityState { EntityId = 5 } });
        Assert.Equal(0f, world.GetSnapshot().Entities[5].Lever);
    }

    // ── What the client plays ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The value of a field in the last line of a voice that carries it.</summary>
    private static float LastValue(IEnumerable<string> lines, string field)
    {
        float value = float.NaN;
        foreach (var line in lines)
        {
            int at = line.IndexOf(" " + field + "=", StringComparison.Ordinal);
            if (at < 0) continue;
            at += field.Length + 2;
            int end = line.IndexOf(' ', at);
            value = float.Parse(line[at..(end < 0 ? line.Length : end)], CultureInfo.InvariantCulture);
        }
        return value;
    }

    /// <summary>
    /// A train's voices play the notch the server sends for it, whatever its speed does. Running at a steady
    /// 12 m/s the client's guess was "holding" (0.3) always: sent "pulling away", its voices are at 0.9; sent
    /// "braking", at 0.
    /// </summary>
    [Theory]
    [InlineData(MachinePower.TrainPulling)]
    [InlineData(MachinePower.TrainHolding)]
    [InlineData(0f)]
    public void A_trains_voices_play_the_notch_the_server_sends(float notch)
    {
        var run = Replay.RunScript(r =>
        {
            var layout = TrainLayout.Sources(TrainProfile.ByName("light_rail"), ModelLibrary.Presets);
            const float speed = 12f;
            float head = -10f;
            (Vector3 At, Vector3 V) Source(int i) => (new Vector3(head - layout[i].AlongMetres, layout[i].HeightMetres, -25f), new Vector3(speed, 0f, 0f));
            for (int i = 0; i < layout.Count; i++)
            {
                var (at, v) = Source(i);
                r.RailSource(601 + i, $"rail:light_rail/t1/{i}", layout[i].LevelDb, at, v, notch);
            }
            for (int f = 0; f < 60; f++)
            {
                for (int i = 0; i < layout.Count; i++)
                {
                    var (at, v) = Source(i);
                    r.Move(601 + i, at, v, lever: notch);
                }
                r.Listener(Vector3.Zero, Vector3.Zero, 180f);
                r.Tick();
                head += speed / (float)ClientAudioSystem.UpdateHz;
            }
        });
        var trains = EmitterStreamReplayTests.Expand(run.Mixer.Lines).Where(l => l.Contains("SoundId=\"rail\"")).ToList();
        Assert.NotEmpty(trains);
        foreach (var line in trains.TakeLast(6))
            Assert.Equal(notch, LastValue(new[] { line }, "PowerLever"));
    }

    private static void AddAeroplane(ClientAudioHarness h, int id, Vector3 at, Vector3 velocity, float lever)
    {
        var def = new EntityDefinition
        {
            EntityId = id, Type = EntityType.NPC, Moves = true,
            Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        def.SoundEmitter.IsSynth = true;
        def.SoundEmitter.SoundId = "aircraft:piston_single";
        def.SoundEmitter.Mode = PlaybackMode.LoopOne;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.MinDistance = 1f;
        def.SoundEmitter.Range = 50f;
        h.World.RegisterDefinition(def);
        h.World.SyncState(new[] { new EntityState
        {
            EntityId = id, Transform = QuantizedTransform.FromTransform(def.Transform), LinearVelocity = velocity,
            Lever = MachinePower.Encode(lever),
        } });
    }

    /// <summary>
    /// An aeroplane's voice plays the lever the server sends, whatever its flight path says: in level flight the
    /// client's guess was cruise power (0.62); sent idle, it idles, and sent everything, it has everything.
    /// </summary>
    [Theory]
    [InlineData(MachinePower.PistonIdle)]
    [InlineData(1f)]
    public void An_aeroplanes_voice_plays_the_lever_the_server_sends(float lever)
    {
        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        const int Id = 77;
        AddAeroplane(h, Id, new Vector3(0f, 120f, 200f), new Vector3(55f, 0f, 0f), lever);
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(Id), 60), "the aeroplane was never voiced");
        h.Tick(2);
        Assert.Equal(lever, h.Mixer.Latest[Id].PowerLever);
    }

    /// <summary>
    /// A patrol car in traffic is silent until the server says its siren is on, and plays the mode the server
    /// says: the client decides nothing. This car's own controller, as each client ran one, would have had it
    /// on a call within two seconds.
    /// </summary>
    [Fact]
    public void A_patrol_cars_siren_is_the_servers_to_switch()
    {
        int car = 0;
        for (int id = 1; id < 20000 && car == 0; id++)
        {
            var own = new SirenController(id);
            for (int k = 0; k < 400; k++)
                if (own.Update(17f, 1f / 240f) != SirenMode.Off) { car = id; break; }
        }
        Assert.True(car > 0, "no car id starts a call promptly");

        var h = new ClientAudioHarness();
        h.StandAt(Vector3.Zero);
        var at = new Vector3(0f, 0f, 30f);
        var velocity = new Vector3(17f, 0f, 0f);
        h.AddCar(car, "police_interceptor", at, velocity);
        int siren = h.Voice(VoiceKind.Siren, car);
        // Ten seconds driving with nothing said: no siren.
        for (int f = 0; f < 600; f++) { h.Wait(1.0 / 60.0); h.Tick(); }
        Assert.Empty(h.Mixer.StartedAs(VoiceKind.Siren));

        // The server says yelp, with nobody's hand on the switch: it yelps.
        byte yelp = VehicleSignalBits.Encode(manual: false, horn: false, sirenOn: true, SirenMode.Yelp);
        Assert.False(VehicleSignalBits.IsManual(yelp));
        h.World.SyncState(new[] { new EntityState
        {
            EntityId = car, Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = Quaternion.Identity }),
            LinearVelocity = velocity, Signals = yelp,
        } });
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(siren), 120), "the siren never sounded");
        _o.WriteLine($"the siren's key: {h.Mixer.Latest[siren].PhysicalKey}; mode sent {SirenMode.Yelp}");
        Assert.Contains(siren, h.Mixer.Live);
    }

    // ── What the server sends ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A lever is part of what the wire carries, so a change of it goes the tick it happens and the tick
    /// after, as a horn does (RestingStates): for a train standing at its platform, which is sent once a second
    /// while it rests, and for an aeroplane far off closing its throttle in the flare, whose motion is still
    /// what the client predicted and so would have waited for its next interval.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]      // standing, near
    [InlineData(300f, 60f)]   // moving steadily, far
    public void A_lever_that_changes_is_sent_that_tick_and_the_next(float distance, float speed)
    {
        const float dt = 1f / 30f;
        const int id = 7;
        var sent = new Dictionary<int, SentState>();
        var ticks = new List<long>();
        for (long tick = 1; tick <= 59; tick++)
        {
            var s = new EntityState
            {
                EntityId = id,
                Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(0f, 50f, speed * tick * dt), Rotation = Quaternion.Identity }),
                LinearVelocity = new Vector3(0f, 0f, speed),
                Lever = MachinePower.Encode(tick < 40 ? MachinePower.TrainPulling : MachinePower.PistonIdle),
            };
            if (RestingStates.ShouldSend(sent, ref s, tick, force: false, distance)) ticks.Add(tick);
        }
        _o.WriteLine($"{distance} m away at {speed} m/s: sent on ticks {string.Join(",", ticks)}");
        Assert.Contains(40L, ticks);
        Assert.Contains(41L, ticks);
        // Not a tick that goes anyway: the keep-alive falls on tick 23 and 53 for this id, and a far thing's
        // interval is not every tick.
        Assert.DoesNotContain(39L, ticks);
        Assert.DoesNotContain(43L, ticks);
    }

    /// <summary>
    /// A far aeroplane whose lever creeps with a curving flight path (a hundredth a tick) does not go every
    /// tick for it: it goes when the lever has moved by more than the tolerance, and the tick after.
    /// </summary>
    [Fact]
    public void A_far_lever_that_creeps_goes_by_its_tolerance()
    {
        const float dt = 1f / 30f;
        List<long> Sent(Func<long, byte> lever)
        {
            var sent = new Dictionary<int, SentState>();
            var ticks = new List<long>();
            for (long tick = 1; tick <= 60; tick++)
            {
                var s = new EntityState
                {
                    EntityId = 7,
                    Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(0f, 50f, 60f * tick * dt), Rotation = Quaternion.Identity }),
                    LinearVelocity = new Vector3(0f, 0f, 60f),
                    Lever = lever(tick),
                };
                if (RestingStates.ShouldSend(sent, ref s, tick, force: false, 300f)) ticks.Add(tick);
            }
            return ticks;
        }
        var steady = Sent(_ => 100);
        var creeping = Sent(tick => (byte)(40 + 2 * tick));
        _o.WriteLine($"steady: {string.Join(",", steady)}");
        _o.WriteLine($"creeping: {string.Join(",", creeping)}");
        // A hundredth a tick passes the tolerance on the fifth tick after it was last sent: that tick and the next,
        // where a steady one goes every sixth; never every tick.
        Assert.True(creeping.Count > steady.Count, "a lever that moved 0.6 was never sent for it");
        Assert.InRange(creeping.Count, steady.Count + 1, 30);
    }

    /// <summary>
    /// The broadcast itself, on the city: what a client is sent for a train's sources is the notch the rail
    /// system has it in, for an aeroplane the lever the vehicle system flies it at, and for a patrol car in
    /// traffic the siren its controller has it at, with nobody's hand on the switch; nothing else carries a
    /// lever, and a car with no siren carries no signal.
    /// </summary>
    [Fact]
    public void The_broadcast_carries_what_the_servers_drivers_are_doing()
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out var lookup));
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions, new OccupancyService(maps), new HandsService(maps));

        // The city's own trains, and on a stretch of their own an aeroplane, a patrol car and a car with no siren.
        server.Rail.Spawn(maps);
        var rail = new HashSet<int>();
        world.Query(new QueryDescription().WithAll<Transform, SoundEmitterComponent>(), (Entity e, ref Transform t, ref SoundEmitterComponent em) =>
        {
            if (em.SoundId != null && em.SoundId.StartsWith("rail:")) rail.Add(e.Id);
        });
        Assert.NotEmpty(rail);
        var air = AircraftProfile.ByName("piston_single");
        var strip = (From: new Vector3(380f, 90f, -1700f), To: new Vector3(380f, 0.1f, -250f));
        var aeroplane = server.Vehicles.SpawnOne(maps, null, "city", new VehicleData
        {
            Name = "Test single", Preset = "piston_single", RoadStart = strip.From, RoadEnd = strip.To,
            SpeedsKmh = new[] { air.ApproachSpeedMps * 3.6f, 160f }, AccelerationMps2 = 1.5f, BrakingMps2 = 2f, WaitSeconds = 6f,
        });
        Entity Car(string name, string preset, float x) => server.Vehicles.SpawnOne(maps, null, "city", new VehicleData
        {
            Name = name, Preset = preset, RoadStart = new Vector3(x, 0.1f, -1700f), RoadEnd = new Vector3(x, 0.1f, -1300f),
            SpeedsKmh = new[] { 60f }, AccelerationMps2 = 2f, BrakingMps2 = 3f, WaitSeconds = 2f,
        });
        var patrol = Car("Test patrol car", "police_interceptor", 400f);
        var saloon = Car("Test car", "v8_muscle", 408f);
        Assert.NotEqual(Entity.Null, aeroplane);
        Assert.NotEqual(Entity.Null, patrol);
        Assert.NotEqual(Entity.Null, saloon);
        maps.RefreshEarshotRanges();

        // One player beside each, told of nothing yet.
        UserSession Join(int connection, Vector3 at)
        {
            var body = world.Create(
                new PlayerComponent { ConnectionId = connection, Username = "p" + connection }, EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity }, new Velocity(),
                new MaterialComponent { Material = "Generic" }, new NameComponent { Name = "p" + connection },
                new HealthComponent { Current = 100, Max = 100 });
            maps.IndexEntity("city", body);
            var session = new UserSession { ConnectionId = connection, Username = "p" + connection, Entity = body, CurrentMapId = "city", Welcomed = true };
            sessions.AddSession(connection, session);
            return session;
        }
        int train = rail.Min();
        var byTheLine = Join(1, world.Get<Transform>(lookup[train]).Position);
        Join(2, new Vector3(400f, 0.1f, -1500f));
        Join(3, strip.To);

        int notches = 0, pulling = 0, levers = 0, fullPower = 0, idle = 0, sirens = 0, quiet = 0, others = 0;
        server.Broadcasted = (to, message) =>
        {
            if (message is not ServerStateUpdate update) return;
            foreach (var s in update.States)
            {
                if (rail.Contains(s.EntityId))
                {
                    Assert.True(server.Rail.TryGetLever("city", s.EntityId, out float notch));
                    Assert.Equal(MachinePower.Encode(notch), s.Lever);
                    notches++;
                    if (notch == MachinePower.TrainPulling) pulling++;
                }
                else if (s.EntityId == aeroplane.Id)
                {
                    Assert.True(server.Vehicles.TryGetLever("city", s.EntityId, out float lever));
                    Assert.Equal(MachinePower.Encode(lever), s.Lever);
                    levers++;
                    if (lever == 1f) fullPower++;
                    if (lever == MachinePower.PistonIdle) idle++;
                }
                else
                {
                    Assert.Equal(0, s.Lever);
                    others++;
                }
                if (s.EntityId == patrol.Id)
                {
                    Assert.True(server.Vehicles.TryGetSiren("city", s.EntityId, out var mode));
                    if (mode == SirenMode.Off) { Assert.Equal(0, s.Signals); quiet++; }
                    else
                    {
                        Assert.False(VehicleSignalBits.IsManual(s.Signals));
                        Assert.Equal(mode, VehicleSignalBits.Siren(s.Signals));
                        sirens++;
                    }
                }
                else if (s.EntityId == saloon.Id) Assert.Equal(0, s.Signals);
            }
        };

        // The player by the line rides along with the train's first source, so it stays in earshot.
        float dt = PhysicsConstants.FixedDeltaTime;
        long tick = 1;
        for (; tick <= 600 * PhysicsConstants.TickRate; tick++)
        {
            server.Vehicles.Update("city", world, dt);
            server.Rail.Update("city", world, dt);
            // Every tick for the first two seconds, then every fifth: the vehicles are driven every tick.
            if (tick > 60 && tick % 5 != 0) continue;
            world.Get<Transform>(byTheLine.Entity).Position = world.Get<Transform>(lookup[train]).Position;
            server.BroadcastForTest(tick);
            if (pulling > 0 && fullPower > 0 && idle > 0 && sirens > 0 && quiet > 0) break;
        }
        _o.WriteLine($"after {tick / (float)PhysicsConstants.TickRate:F0} s: {notches} train states ({pulling} pulling away), {levers} of the aeroplane "
                   + $"({fullPower} at full power, {idle} idling), the patrol car {sirens} on a call and {quiet} off, {others} of everything else");
        Assert.True(pulling > 0, "no train was sent pulling away");
        Assert.True(fullPower > 0, "the aeroplane was never sent at full power");
        Assert.True(idle > 0, "the aeroplane was never sent idling");
        Assert.True(sirens > 0, "the patrol car was never sent on a call");
        Assert.True(quiet > 0, "the patrol car was never sent off a call");
        Assert.True(others > 0);
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }
}
