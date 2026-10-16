using System.Numerics;
using Arch.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// The spoken-lines replay's drive (docs/ENGINE_PACKAGES.md, stage 0): out of the city's parking garage in
/// the hatchback parked on level 0, right onto Main Street, right again at Central Street with the indicator
/// on, and out on Central Street, so the driving cues (the road's name and limit, the junction ahead and its
/// exits, the turn, the guide, the sensors, the turn clicks, the brake cue, the relay) are held before package
/// stage 7 moves them.
/// </summary>
internal static class SpokenDrive
{
    /// <summary>The city with its parked cars placed, as the server has it after PlaceRecorded: the shipped
    /// maps the walks share have none, and placing them there would change what the walks are sent.</summary>
    private static readonly Lazy<MapManager> Maps = new(() =>
    {
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        var composites = new CompositeService(maps, prefabs,
            new CompositeRepository(Path.Combine(Path.GetTempPath(), "openfps-no-composites-" + Guid.NewGuid())));
        composites.PlaceRecorded(maps);
        return maps;
    });

    /// <summary>The hatchback on level 0 of the garage, facing east toward Main Street (city.json's first
    /// composite), and where a player stands beside its driver's door ("/tp -16 31.5 0.3").</summary>
    private static readonly Vector3 CarAt = new(-16f, 0.25f, 29f), Beside = new(-16f, 0.3f, 31.5f);

    /// <summary>
    /// Beside the car, in by E, the engine started by T, out of the garage east and right onto Main Street's
    /// southbound kerb lane, the road read out by Z, the right indicator by L before the junction with Central
    /// Street, slowing for it and right through it, west along Central Street, stopping, the engine off by
    /// Shift+T and out by E.
    /// </summary>
    public static void GarageToCentralStreet(SpokenReplay r)
    {
        var server = new DriveServer(r, Maps.Value, "city", CarAt, Route());
        r.Server = server.Tick;
        r.Arrive("city", Beside, 50f, Maps.Value, roads: true);
        r.Wait(2.0);
        r.Note("beside the car");
        r.Tap(GameKey.E);
        r.Wait(1.5);
        r.Note("in the driver's seat");
        r.Tap(GameKey.T);
        r.Wait(1.5);
        r.Hold(GameKey.W, 2.5);
        r.Note("out of the garage");
        r.Wait(1.5);
        r.Note("on Main Street");
        r.Tap(GameKey.Z);
        r.Tap(GameKey.L);
        r.Wait(1.0);
        r.Hold(GameKey.S, 0.4);
        r.Wait(1.5);
        r.Note("in the junction");
        r.Hold(GameKey.W, 2.0);
        r.Note("on Central Street");
        r.Wait(1.0);
        r.Hold(GameKey.S, 1.8);
        r.Note("stopped");
        r.Wait(0.5);
        r.ShiftTap(GameKey.T);
        r.Wait(1.0);
        r.Tap(GameKey.E);
        r.Wait(2.0);
        r.Note("end");
    }

    /// <summary>
    /// The way the car goes, whatever the keys: east out of the garage between its pillars, a right turn of
    /// 5 m onto Main Street's southbound kerb lane (1.5 m in from the kerb at x = -6), south to the junction
    /// box, a right turn of 6 m through it onto Central Street's westbound kerb lane, and west.
    ///
    /// Every heading is 0.6 degrees past the road's, the car standing that much off the garage's line from
    /// its first state: a heading on a multiple of 15 degrees is on one of the turn clicks' marks, where the
    /// last bit of an arctangent decides whether a click sounds, and a straight due south clicked twice in
    /// one run.
    /// </summary>
    private static DrivePath Route()
        => new DrivePath(CarAt.X, CarAt.Z, Math.PI / 2 + OffTheMarks)
            .Straight(6.5)
            .Arc(5.0, Math.PI / 2)
            .Straight(13.5)
            .Arc(6.0, Math.PI / 2)
            .Straight(40.0);

    private const double OffTheMarks = 0.6 * Math.PI / 180.0;
}

/// <summary>
/// A line on the ground made of straights and right-hand arcs, as a pose at each distance along it: x east,
/// z north, and the heading the server gives a vehicle (Atan2(x, z): 0 north, increasing toward east). In
/// doubles and to the millimetre the wire carries, so the poses are the same on every machine.
/// </summary>
internal sealed class DrivePath
{
    private readonly List<(double Length, Func<double, (double X, double Z, double H)> At)> _parts = new();
    private double _x, _z, _h;

    public DrivePath(double x, double z, double heading) { _x = x; _z = z; _h = heading; }

    public double Length => _parts.Sum(p => p.Length);

    public DrivePath Straight(double metres)
    {
        double x = _x, z = _z, h = _h;
        _parts.Add((metres, d => (x + Math.Sin(h) * d, z + Math.Cos(h) * d, h)));
        (_x, _z) = (x + Math.Sin(h) * metres, z + Math.Cos(h) * metres);
        return this;
    }

    /// <summary>A turn to the right of <paramref name="radius"/> through <paramref name="radians"/>.</summary>
    public DrivePath Arc(double radius, double radians)
    {
        double h = _h;
        // The right of a heading is (cos h, -sin h); the centre is that far to the right.
        double cx = _x + Math.Cos(h) * radius, cz = _z - Math.Sin(h) * radius;
        (double, double, double) At(double d)
        {
            double a = h + d / radius;
            return (cx - Math.Cos(a) * radius, cz + Math.Sin(a) * radius, a);
        }
        _parts.Add((radius * radians, At));
        (_x, _z, _h) = At(radius * radians);
        return this;
    }

    /// <summary>Where the car is <paramref name="along"/> metres on: its position on the ground (y is the
    /// caller's) and its rotation.</summary>
    public (Vector3 At, Quaternion Rotation, Vector3 Forward) Pose(double along)
    {
        foreach (var (length, at) in _parts)
        {
            if (along > length) { along -= length; continue; }
            return Of(at(along));
        }
        var (lastLength, lastAt) = _parts[^1];
        return Of(lastAt(lastLength));
    }

    private static (Vector3, Quaternion, Vector3) Of((double X, double Z, double H) p)
    {
        static float Mm(double v) => (float)(Math.Round(v * 1000.0) / 1000.0);
        var rotation = new Quaternion(0f, (float)Math.Sin(p.H / 2), 0f, (float)Math.Cos(p.H / 2));
        return (new Vector3(Mm(p.X), 0f, Mm(p.Z)), rotation, new Vector3((float)Math.Sin(p.H), 0f, (float)Math.Cos(p.H)));
    }
}

/// <summary>
/// The server's half of a drive, scripted by hand as the emitter-stream replay scripts its cars: a parked car
/// as the server sends it once it is in reach (its definition and its parts', from the map with its cars
/// placed), the answers to E, T and Shift+T in the words the server uses (OccupancyService.Enter and Exit,
/// DrivingSystem.SetIgnition), and every tick a ServerStateUpdate with the car and its parts where they are
/// and, seated, the driver at the seat. The car keeps to its <see cref="DrivePath"/>; the keys only set its
/// speed (W pulls at 2.5 m/s², S brakes at 6, nothing rolls it down at 0.4), so steering and lane assist are
/// sent and not followed. No car door is sent (a render, as the knock is) and no wheels (the cues read the road
/// as dry without them).
/// </summary>
internal sealed class DriveServer
{
    private const float Pull = 2.5f, Brake = 6f, Roll = 0.4f, Top = 15f;
    /// <summary>Above this a car will not let anybody in or out (OccupancyService.Moving).</summary>
    private const float Moving = 2.0f;

    private readonly SpokenReplay _r;
    private readonly World _world;
    private readonly DrivePath _path;
    private readonly Entity _root;
    private readonly string _name;
    private readonly Seat _seat;
    private readonly float _groundY;
    private readonly List<(int Id, Vector3 Local, Quaternion Rotation)> _parts = new();
    private readonly List<Entity> _members;

    private int _read;
    private bool _introduced, _seated, _engine, _resend, _leaving;
    private long _lastSequence;
    private double _along;
    private float _speed;

    public DriveServer(SpokenReplay r, MapManager maps, string map, Vector3 carAt, DrivePath path)
    {
        _r = r;
        _path = path;
        if (!maps.TryGetMap(map, out _world, out _, out _, out _)) throw new InvalidOperationException($"no map {map}");
        Entity found = Entity.Null;
        _world.Query(new QueryDescription().WithAll<Transform, DriveComponent>(), (Entity e, ref Transform t) =>
        {
            if (Vector3.Distance(t.Position, carAt) < 0.1f) found = e;
        });
        if (found == Entity.Null) throw new InvalidOperationException($"no car parked at {carAt}");
        _root = found;
        _name = _world.Get<CompositeComponent>(_root).Name;
        _seat = _world.Get<OccupancyComponent>(_root).Seats.First(s => s.Controls);
        _groundY = carAt.Y;
        _members = CompositeService.MembersOf(_world, _root.Id).OrderBy(e => e.Id).ToList();
        foreach (var m in _members)
        {
            var p = _world.Get<ParentComponent>(m);
            _parts.Add((m.Id, p.LocalPosition, p.LocalRotation));
        }
    }

    /// <summary>One tick of the server: what the client sent since the last, then what it is sent.</summary>
    public void Tick()
    {
        float throttle = 0f;
        for (; _read < _r.Sent.Count; _read++)
        {
            switch (_r.Sent[_read])
            {
                case ClientInputUpdate input:
                    _lastSequence = input.SequenceId;
                    throttle = input.MoveDirection.Z;
                    break;
                case InteractRequest:
                    Interact();
                    break;
                case TextCommand { Command: "ignition" } key when _seated:
                    bool on = key.Args.Length == 0 || key.Args[0] != "off";
                    if (on == _engine) Say(on ? "The engine is already running." : "The engine is already off.");
                    else
                    {
                        _engine = on;
                        _resend = true;
                        Say(on ? "You turn the key. The engine starts." : "You switch the engine off.");
                    }
                    break;
            }
        }

        const float dt = SpokenReplay.Dt;
        if (!_seated || !_engine) throttle = MathF.Min(throttle, 0f);
        float a = throttle > 0f ? Pull * throttle : throttle < 0f ? Brake * throttle : -Roll;
        _speed = Math.Clamp(_speed + a * dt, 0f, Top);
        _along += _speed * dt;
        if (_along >= _path.Length) { _along = _path.Length; _speed = 0f; }

        var (at, rotation, forward) = _path.Pose(_along);
        at.Y = GroundAt(at.X);
        var velocity = forward * _speed;
        var root = new Transform { Position = at, Rotation = rotation, Scale = _world.Get<Transform>(_root).Scale };

        // In reach on the first tick: the definitions before the first state, as the broadcast sends them.
        if (!_introduced)
        {
            _r.Session.HandleMessage(Definition(_root, root));
            foreach (var m in _members) _r.Session.HandleMessage(Definition(m, Place(root, m.Id)));
            _introduced = true;
        }
        else if (_resend) _r.Session.HandleMessage(Definition(_root, root));
        _resend = false;

        var update = new ServerStateUpdate
        {
            Tick = _r.Tick,
            LastProcessedSequenceId = _lastSequence,
            RidingEntityId = _seated ? _root.Id : -1,
            RidingControls = _seated,
        };
        update.States.Add(State(_root.Id, root, velocity, VehicleSignalBits.Encode(manual: true, false, false, SirenMode.Wail)));
        foreach (var (id, _, _) in _parts) update.States.Add(State(id, Place(root, id), velocity, 0));
        if (_seated)
        {
            // The seat owns where the driver is (OccupancySystem), facing the way the car does.
            var seat = new Transform
            {
                Position = root.Position + Vector3.Transform(_seat.LocalPosition, root.Rotation),
                Rotation = root.Rotation * Quaternion.CreateFromYawPitchRoll(_seat.LocalYaw, 0f, 0f),
                Scale = Vector3.One,
            };
            update.States.Add(State(SpokenReplay.OwnId, seat, velocity, 0));
        }
        else if (_leaving)
        {
            // Out on the first spot FindStandingRoom tries: 1.6 m to the right of the seat, on the road.
            var seatAt = root.Position + Vector3.Transform(_seat.LocalPosition, root.Rotation);
            var right = Vector3.Transform(Vector3.UnitX, root.Rotation);
            var spot = seatAt + right * 1.6f;
            spot.Y = at.Y;
            update.States.Add(State(SpokenReplay.OwnId, new Transform { Position = spot, Rotation = root.Rotation, Scale = Vector3.One },
                                    Vector3.Zero, 0));
            _leaving = false;
        }
        _r.Session.HandleMessage(update);
    }

    /// <summary>E: in by the nearest seat when on foot, out when in it, as Program's interact handler does
    /// with nothing else in reach.</summary>
    private void Interact()
    {
        if (_seated)
        {
            if (_speed > Moving) { Say($"{_name} is still moving. Stop first."); return; }
            _seated = false;
            _leaving = true;
            Say($"You get out of {_name}.{(_engine ? " You left the engine running." : "")}");
            return;
        }
        if (_speed > Moving) { Say($"{_name} is moving. Wait for it to stop."); return; }
        _seated = true;
        string engine = _engine ? " The engine is running." : " The engine is off; T starts it.";
        Say($"You are in the {_seat.Name} seat of {_name}.{engine} W and S to drive, A and D to steer, shift T switches it off.");
    }

    private void Say(string text) => _r.Session.HandleMessage(new TextEvent { Text = text });

    /// <summary>The garage's level 0 is a step up from the street: the car comes down it between the
    /// garage's edge (x = -11.5) and the sidewalk's (x = -9.5).</summary>
    private float GroundAt(float x)
    {
        const float garageEdge = -11.5f, sidewalk = -9.5f, road = 0.05f;
        if (x <= garageEdge) return _groundY;
        if (x >= sidewalk) return road;
        return _groundY + (road - _groundY) * (x - garageEdge) / (sidewalk - garageEdge);
    }

    /// <summary>A part where ParentSystem puts it, from the root's pose and its own.</summary>
    private Transform Place(Transform root, int id)
    {
        var (_, local, rotation) = _parts.First(p => p.Id == id);
        return new Transform
        {
            Position = root.Position + Vector3.Transform(local, root.Rotation),
            Rotation = root.Rotation * rotation,
            Scale = _world.Get<Transform>(_members.First(m => m.Id == id)).Scale,
        };
    }

    private EntityDefinition Definition(Entity e, Transform at)
    {
        var def = EntityDefinitionFactory.From(_world, e);
        def.Transform = at;
        if (e == _root) def.SoundEmitter.SynthRunning = _engine;
        return def;
    }

    private static EntityState State(int id, Transform t, Vector3 velocity, byte signals) => new()
    {
        EntityId = id,
        Transform = QuantizedTransform.FromTransform(t),
        LinearVelocity = velocity,
        Signals = signals,
    };
}
