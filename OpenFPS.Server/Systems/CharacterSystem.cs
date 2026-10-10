using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// People with names, who live on a map rather than walk a line of it. One so far: Alex, a homeless
/// man on the city (Cody, 2026-10-06: "he walks the streets, rides the bus, hangs out at bus stops and
/// in front of stores and the lobby in apartment buildings because he's homeless").
///
/// His day is a string of places found from the map (<see cref="Haunt"/>: bus stops, front entrances,
/// lobbies, squares), chosen by the hour and the weather and reached on foot or by bus. A lobby's door
/// is locked from the street: he waits at it until somebody opens it or lets him in.
///
/// Every choice is drawn from a sequence seeded by the map, his name and the game day, so the same day
/// goes the same way and a restart puts him where his day has him. What he says is
/// <see cref="PedestrianSpeech"/>'s; this moves him and says what he is doing (<see cref="ViewOf"/>).
/// </summary>
public sealed class CharacterSystem
{
    public enum Doing { Lingering, Walking, AtDoor, WaitingForBus, Boarding, Riding }

    /// <summary>What a character is doing, for what they say and for /where.</summary>
    public readonly record struct View(string Name, string Voice, Doing Doing, HauntKind? Kind, string Place,
                                       bool Indoors, double Until);

    private sealed class Character
    {
        public required string MapId;
        public required CharacterData Data;
        public Entity Entity = Entity.Null;
        public List<Haunt> Haunts = new();
        public Pavements Paths = Pavements.Build(Array.Empty<Pavements.Strip>());
        public List<Pavements.Solid> Solids = new();
        public bool Ready;

        public Doing Now = Doing.Lingering;
        public Haunt? At;
        public Haunt? Target;
        public double Until;
        public List<Vector3> Route = new();
        public int Leg;
        /// <summary>Going out of a lobby: the door is opened from inside before the walk goes on.</summary>
        public bool OpeningDoor;
        /// <summary>Walking in through a lobby's door, which is open: the walk ends inside.</summary>
        public bool EnteringLobby;
        public double DoorSince;
        /// <summary>His way through a door, and the leg of the route that has him through it.</summary>
        public DoorManners.Passage? Passage;
        public int ThroughAtLeg = -1;
        public Haunt? RideTo;
        public Entity Bus = Entity.Null;
        public double BusSince;
        public double OffAt = double.NegativeInfinity;
        public int Decisions;
        public int Day;
        public double GoneUntil = double.NaN;
        public Vector3 LastSeen;
        public float FacingYaw;
        public double FaceUntil;
        public float FaceYaw;
    }

    private readonly List<Character> _all = new();
    private readonly DoorManners _manners = new();
    private readonly Dictionary<string, double> _clocks = new();
    private readonly int _seed;
    private MapManager? _maps;

    /// <summary>Walking pace, m/s: a shuffle, slower than the crowd's.</summary>
    public const float WalkSpeed = 1.05f;
    /// <summary>How long after the world starts the places are looked for, seconds: a building's door is
    /// one of its parts and has no world position until the parts have been placed.</summary>
    public const double FindAfterSeconds = 3;
    /// <summary>How long he waits at a locked front door before somebody inside lets him in, seconds.</summary>
    public const double DoorWaitSeconds = 45;
    /// <summary>The longest he waits for a bus before walking instead, seconds.</summary>
    public const double BusWaitSeconds = 12 * 60;
    /// <summary>Off at the next stop after this long on a bus whose stop he never reached, seconds.</summary>
    public const double LongestRideSeconds = 15 * 60;
    /// <summary>How long a character is gone after being killed before they are back, seconds.</summary>
    public const double BackAfterSeconds = 4 * 60;
    /// <summary>A bus standing this near a stop is at it, metres.</summary>
    public const float AtStopMetres = 15f;

    /// <summary>
    /// How much faster his day goes than real time: his lingering, his waits and his time away divided by
    /// it. For walking a day through in a test, OPENFPS_CHARACTER_PACE=10; unset, 1.
    /// </summary>
    public double Pace { get; set; } = ReadPace();

    private static double ReadPace()
        => double.TryParse(Environment.GetEnvironmentVariable("OPENFPS_CHARACTER_PACE"), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out double p) && p > 0 ? p : 1.0;

    public CharacterSystem(int seed = 20261006) { _seed = seed; }

    /// <summary>The characters every map declares. Their bodies are made once the map's places are known.</summary>
    public void Spawn(MapManager maps)
    {
        _maps = maps;
        foreach (var entry in maps.GetAllMaps())
            foreach (var cd in entry.Value.data.Characters ?? new List<CharacterData>())
            {
                if (string.IsNullOrWhiteSpace(cd.Name)) continue;
                if (Speech.LinesOf(cd.Voice, "greet").Count == 0 && !Speech.Voices.Contains(cd.Voice))
                    Log.Warning("Map {Map}: character {Name} speaks in voice '{Voice}', which is not in the catalogue; they will be silent.",
                                entry.Key, cd.Name, cd.Voice);
                _all.Add(new Character { MapId = entry.Key, Data = cd });
                Log.Information("Map {Map}: {Name} ({Kind}) lives here.", entry.Key, cd.Name, cd.Kind);
            }
    }

    /// <summary>For tests: a character on a map, with the places given.</summary>
    internal void Add(string mapId, CharacterData data, List<Haunt> haunts, Pavements paths, MapManager maps)
    {
        _maps = maps;
        _all.Add(new Character { MapId = mapId, Data = data, Haunts = haunts, Paths = paths, Ready = true });
    }

    public int Count => _all.Count;

    /// <summary>A character put on a running map (the world editor): their body is made once the map's
    /// places are found, as at start.</summary>
    public void AddLive(MapManager maps, string mapId, CharacterData data)
    {
        _maps = maps;
        _all.RemoveAll(c => c.MapId == mapId && c.Data.Name.Equals(data.Name, StringComparison.OrdinalIgnoreCase) && c.Entity == Entity.Null);
        _all.Add(new Character { MapId = mapId, Data = data });
        Log.Information("Map {Map}: {Name} ({Kind}) lives here now.", mapId, data.Name, data.Kind);
    }

    /// <summary>A character taken off a running map: their body's runtime id, to tell the clients it has
    /// gone, or null if they had none.</summary>
    public int? RemoveLive(MapManager maps, string mapId, string name)
    {
        var c = _all.FirstOrDefault(x => x.MapId == mapId && x.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (c == null) return null;
        _all.Remove(c);
        if (c.Entity == Entity.Null || !maps.TryGetMap(mapId, out var world, out _, out _, out _) || !world.IsAlive(c.Entity)) return null;
        int id = c.Entity.Id;
        maps.DestroyEntity(mapId, c.Entity);
        return id;
    }

    /// <summary>A character's voice or places changed: their places are found again, and the next place
    /// they go to is one of them; where they are now they finish.</summary>
    public void Change(string mapId, CharacterData data)
    {
        var c = _all.FirstOrDefault(x => x.MapId == mapId && x.Data.Name.Equals(data.Name, StringComparison.OrdinalIgnoreCase));
        if (c == null) return;
        c.Data = data;
        if (c.Entity != Entity.Null && _maps != null && _maps.TryGetMap(mapId, out var world, out _, out _, out _)
            && world.IsAlive(c.Entity) && world.Has<Pedestrian>(c.Entity))
            world.Get<Pedestrian>(c.Entity).Voice = data.Voice;
        if (c.Ready) c.Ready = false;
    }

    /// <summary>The places a character's day keeps to: those named, if any of them is on the map.</summary>
    private static List<Haunt> KeptTo(Character c, List<Haunt> found)
    {
        if (c.Data.Places is not { Count: > 0 } names) return found;
        var kept = found.Where(h => names.Contains(h.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (kept.Count > 0) return kept;
        Log.Warning("CHARACTER {Name} on {Map}: none of the places named ({Places}) is on the map; they go anywhere.", c.Data.Name, c.MapId, string.Join("; ", names));
        return found;
    }

    // ── Each tick ───────────────────────────────────────────────────────────────────────────────

    public void Update(string mapId, World world, SpatialGrid<Entity> grid, float dt, WorldEnvironmentComponent env, WeatherType weather)
    {
        double now = _clocks[mapId] = _clocks.GetValueOrDefault(mapId) + dt;
        var cond = SpeechConditions.From(env, weather);
        foreach (var c in _all)
        {
            if (c.MapId != mapId) continue;
            c.Day = env.DayOfYear;
            if (!c.Ready)
            {
                if (now < FindAfterSeconds || _maps == null || !_maps.TryGetMapData(mapId, out var data)) continue;
                Prepare(c, world, grid, data);
                if (c.Haunts.Count == 0) continue;
            }
            if (c.Haunts.Count == 0) continue;

            // Gone (killed and taken away): back after a while, somewhere else.
            if (c.Entity == Entity.Null || !world.IsAlive(c.Entity) || !world.Has<Pedestrian>(c.Entity)
                || world.Get<Pedestrian>(c.Entity).Character != c.Data.Name)
            {
                if (c.Entity != Entity.Null && double.IsNaN(c.GoneUntil))
                {
                    c.GoneUntil = now + BackAfterSeconds / Pace;
                    Log.Information("CHARACTER {Name}: gone; back in {Seconds:F0} s.", c.Data.Name, BackAfterSeconds / Pace);
                }
                if (c.Entity == Entity.Null || now >= c.GoneUntil)
                {
                    Begin(c, now, cond, awayFrom: c.Entity == Entity.Null ? null : c.LastSeen);
                    c.GoneUntil = double.NaN;
                }
                continue;
            }
            // Dead where he fell, until he is taken away: nothing moves him.
            if (world.Has<DeadComponent>(c.Entity) || world.Has<FrozenComponent>(c.Entity))
            {
                if (world.Has<Velocity>(c.Entity)) world.Get<Velocity>(c.Entity).Linear = Vector3.Zero;
                continue;
            }
            Step(c, world, grid, dt, now, cond);
            if (world.IsAlive(c.Entity)) c.LastSeen = world.Get<Transform>(c.Entity).Position;
        }
        _manners.Update(mapId, world, dt);
    }

    private void Prepare(Character c, World world, SpatialGrid<Entity> grid, MapData data)
    {
        c.Ready = true;
        c.Paths = Pavements.Build(Pavements.StripsOf(world));
        c.Solids = Pavements.SolidsOf(world);
        c.Haunts = KeptTo(c, HauntFinder.Find(world, grid, data, c.Paths, c.Solids));
        Log.Information("CHARACTER {Name} on {Map}: {Count} places ({Kinds}) on {Nodes} pavement corners: {Names}",
                        c.Data.Name, c.MapId, c.Haunts.Count,
                        string.Join(", ", c.Haunts.GroupBy(h => h.Kind).Select(g => $"{g.Count()} {g.Key}")),
                        c.Paths.NodeCount, string.Join("; ", c.Haunts.Select(h => h.Name)));
        if (c.Haunts.Count == 0)
            Log.Warning("CHARACTER {Name} on {Map}: the map has no bus stops, front entrances or squares; he stays away.", c.Data.Name, c.MapId);
    }

    /// <summary>Starts (or restarts) a character's day at the place it has them at this hour.</summary>
    private void Begin(Character c, double now, SpeechConditions cond, Vector3? awayFrom)
    {
        var rng = Draw(c);
        var choices = awayFrom is { } dead
            ? c.Haunts.Where(h => Vector3.Distance(h.Stand, dead) > 100f).ToList()
            : c.Haunts;
        if (choices.Count == 0) choices = c.Haunts;
        var at = Choose(choices, null, null, cond, rng);
        c.At = at; c.Target = null; c.RideTo = null;
        c.Now = Doing.Lingering;
        c.Passage = null;
        // Part way through a stay, as somebody already there would be.
        c.Until = now + LingerSeconds(at.Kind, cond, rng) * (0.3 + 0.7 * rng.NextDouble()) / Pace;
        c.FacingYaw = at.Facing;
        var maps = _maps!;
        c.Entity = maps.SpawnEntity(c.MapId, w => w.Create(
            EntityType.NPC,
            new Transform { Position = at.Stand, Rotation = Quaternion.CreateFromYawPitchRoll(at.Facing, 0f, 0f), IsDirty = true },
            new Velocity { Linear = Vector3.Zero },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false },
            new NameComponent { Name = c.Data.Name },
            new IdentityComponent
            {
                Name = c.Data.Name,
                Description = string.IsNullOrWhiteSpace(c.Data.Description) ? "somebody with a name" : c.Data.Description,
                Named = true,
            },
            new MaterialComponent { Material = PhysicsConstants.PersonMaterial },
            new Pedestrian { Voice = c.Data.Voice, Pair = "", Character = c.Data.Name },
            new HealthComponent { Current = 100, Max = 100 }));
        c.LastSeen = at.Stand;
        Log.Information("CHARACTER {Name} (entity {Id}) is at {Place}, {Doing} until {Until}.",
                        c.Data.Name, c.Entity.Id, at.Name, "staying", Clock(cond, c.Until - now));
    }

    // ── What he does ────────────────────────────────────────────────────────────────────────────

    private void Step(Character c, World world, SpatialGrid<Entity> grid, float dt, double now, SpeechConditions cond)
    {
        ref var t = ref world.Get<Transform>(c.Entity);
        ref var vel = ref world.Get<Velocity>(c.Entity);
        switch (c.Now)
        {
            case Doing.Lingering:
                Stand(c, ref t, ref vel, now);
                if (now >= c.Until) Leave(c, world, now, cond);
                break;

            case Doing.Walking:
                if (c.OpeningDoor)
                {
                    // Out through a lobby's door: opened from inside with the bar, and through once it is open.
                    Stand(c, ref t, ref vel, now);
                    if (!DoorSystem.OpenEnough(world, c.At?.Door ?? Entity.Null) && now - c.DoorSince < 6) break;
                    c.OpeningDoor = false;
                }
                bool arrived = Walk(c, ref t, ref vel, dt);
                if (c.Passage != null && c.Leg > c.ThroughAtLeg)
                {
                    _manners.Through(c.MapId, world, c.Passage, c.Entity);
                    c.Passage = null;
                }
                if (arrived) Arrive(c, world, now, cond);
                break;

            case Doing.AtDoor:
            {
                var door = c.Target!.Door;
                Stand(c, ref t, ref vel, now);
                if (!world.IsAlive(door) || DoorSystem.OpenEnough(world, door))
                {
                    c.Route = new List<Vector3> { t.Position, c.Target.Outside, c.Target.Inside, c.Target.Stand };
                    c.Leg = 1;
                    c.ThroughAtLeg = 2;
                    c.Now = Doing.Walking;
                    c.OpeningDoor = false;
                    c.EnteringLobby = true;
                    Log.Information("CHARACTER {Name}: in through {Door}.", c.Data.Name, c.Target.Name);
                }
                else if (now - c.DoorSince >= DoorWaitSeconds / Pace)
                {
                    // Somebody on their way out holds it for him: pushed by its bar, from inside.
                    DoorSystem.Set(world, door, true, by: c.Target.Inside);
                    // Asked again in 15 s if it has shut on him.
                    c.DoorSince = now - DoorWaitSeconds / Pace + 15;
                    Log.Information("CHARACTER {Name}: somebody inside lets him into {Place}.", c.Data.Name, c.Target.Name);
                }
                break;
            }

            case Doing.WaitingForBus:
            {
                // Back to the stop from wherever a missed bus left him.
                if (Flat(t.Position, c.At!.Stand) > 0.3f)
                {
                    c.Route = new List<Vector3> { t.Position, c.At.Stand };
                    c.Leg = 1;
                    Walk(c, ref t, ref vel, dt);
                }
                else Stand(c, ref t, ref vel, now);
                var bus = BusAt(world, c.At!.StopAt);
                // Not the bus he has just got off, still standing at the stop.
                if (bus == c.Bus && now - c.OffAt < 90) bus = Entity.Null;
                if (bus != Entity.Null && FreeSeat(world, bus, t.Position) >= 0)
                {
                    c.Bus = bus;
                    c.Now = Doing.Boarding;
                    c.Route = new List<Vector3> { t.Position, DoorOf(world, bus) };
                    c.Leg = 1;
                    Log.Information("CHARACTER {Name}: the bus (entity {Bus}) is in at {Place}; getting on.", c.Data.Name, bus.Id, c.At.Name);
                }
                else if (now - c.BusSince >= BusWaitSeconds)          // the bus keeps real time, whatever his pace
                {
                    Log.Information("CHARACTER {Name}: no bus at {Place}; walking to {To} instead.", c.Data.Name, c.At.Name, c.RideTo!.Name);
                    WalkTo(c, world, t.Position, c.RideTo!, now);
                    c.RideTo = null;
                }
                break;
            }

            case Doing.Boarding:
            {
                bool still = world.IsAlive(c.Bus) && Standing(world, c.Bus);
                if (!still)
                {
                    // It went without him: back to the stop to wait for the next.
                    c.Now = Doing.WaitingForBus;
                    c.Bus = Entity.Null;
                    Log.Information("CHARACTER {Name}: the bus left without him.", c.Data.Name);
                    break;
                }
                c.Route[^1] = DoorOf(world, c.Bus);
                if (!Walk(c, ref t, ref vel, dt)) break;
                int seat = FreeSeat(world, c.Bus, t.Position);
                if (seat < 0) { c.Now = Doing.WaitingForBus; break; }
                world.Add(c.Entity, new OccupantComponent
                {
                    RootEntityId = c.Bus.Id, SeatIndex = seat, Controls = false, BoardedFrom = t.Position,
                });
                vel.Linear = Vector3.Zero;
                c.Now = Doing.Riding;
                c.BusSince = now;
                Log.Information("CHARACTER {Name}: on the bus (entity {Bus}), seat {Seat}, for {To}.", c.Data.Name, c.Bus.Id, seat, c.RideTo!.Name);
                break;
            }

            case Doing.Riding:
            {
                // The seat carries him (OccupancySystem). He gets off at his stop.
                if (!world.IsAlive(c.Bus))
                {
                    CompositeService.Disembark(world, c.Entity);
                    WalkTo(c, world, t.Position with { Y = Ground(world, grid, t.Position) }, c.RideTo!, now);
                    break;
                }
                bool atStop = world.Has<SoundEmitterComponent>(c.Bus) && world.Get<SoundEmitterComponent>(c.Bus).ServingStop;
                var busAt = world.Get<Transform>(c.Bus).Position;
                bool mine = Flat(busAt, c.RideTo!.StopAt) < AtStopMetres;
                bool tooLong = now - c.BusSince > LongestRideSeconds;
                if (atStop && now - c.BusSince > 10 && (mine || tooLong))
                {
                    CompositeService.Disembark(world, c.Entity);
                    c.OffAt = now;
                    var off = DoorOf(world, c.Bus);
                    off.Y = Ground(world, grid, off);
                    t.Position = off;
                    t.IsDirty = true;
                    var to = mine ? c.RideTo! : Nearest(c.Haunts, off, HauntKind.BusStop) ?? c.RideTo!;
                    Log.Information("CHARACTER {Name}: off the bus at {Place}.", c.Data.Name, to.Name);
                    c.Route = new List<Vector3> { off, to.Stand };
                    c.Leg = 1;
                    c.Target = to;
                    c.Now = Doing.Walking;
                    c.RideTo = null;
                }
                break;
            }
        }
    }

    /// <summary>Time to go: choose where, and set off.</summary>
    private void Leave(Character c, World world, double now, SpeechConditions cond)
    {
        var rng = Draw(c);
        var at = c.At!;
        var here = world.Get<Transform>(c.Entity).Position;
        var others = c.Haunts.Where(h => h.Kind == HauntKind.BusStop && h != at).ToList();
        if (at.Kind == HauntKind.BusStop && others.Count > 0 && rng.NextDouble() < RideChance(cond))
        {
            c.RideTo = others[rng.Next(others.Count)];
            c.Now = Doing.WaitingForBus;
            c.BusSince = now;
            Log.Information("CHARACTER {Name}: waiting at {Place} for a bus to {To}.", c.Data.Name, at.Name, c.RideTo.Name);
            return;
        }
        var next = Choose(c.Haunts, at, new Vector2(here.X, here.Z), cond, rng);
        WalkTo(c, world, here, next, now);
    }

    /// <summary>Sets off on foot for a place: out of a lobby by its door if he is in one, along the pavements,
    /// and to its door if it is a lobby.</summary>
    private void WalkTo(Character c, World world, Vector3 from, Haunt to, double now)
    {
        var start = new List<Vector2> { new(from.X, from.Z) };
        bool leavingLobby = c.Now != Doing.Riding && c.Now != Doing.WaitingForBus && c.At is { Kind: HauntKind.Lobby } lobby
                            && Flat(from, lobby.Stand) < 3f;
        if (leavingLobby)
        {
            start.Add(new Vector2(c.At!.Inside.X, c.At.Inside.Z));
            start.Add(new Vector2(c.At.Outside.X, c.At.Outside.Z));
        }
        var off = start[^1];
        var end = to.Kind == HauntKind.Lobby ? new Vector2(to.Outside.X, to.Outside.Z) : new Vector2(to.Stand.X, to.Stand.Z);
        List<Vector2> path;
        if (c.Paths.IsEmpty) path = new List<Vector2> { off, end };
        else
        {
            var on = c.Paths.Nearest(off, out int onEdge);
            path = to.AccessEdge >= 0
                ? c.Paths.Route(off, on, onEdge, end, to.Access, to.AccessEdge)
                : c.Paths.Route(off, end);
        }
        var flat = start.Take(start.Count - 1).Concat(Pavements.Clear(path, c.Solids)).ToList();
        var maps = _maps!;
        maps.TryGetMap(c.MapId, out _, out _, out var grid, out _);
        c.Route = flat.Select(p => new Vector3(p.X, 0f, p.Y)).ToList();
        for (int i = 0; i < c.Route.Count; i++)
            c.Route[i] = c.Route[i] with { Y = Ground(world, grid, c.Route[i] with { Y = i == 0 ? from.Y + 0.3f : 0.3f }) };
        c.Route[0] = from;
        // Inside a lobby the floor is the lobby's, not whatever the street's probe found.
        if (leavingLobby) { c.Route[1] = c.At!.Inside; c.Route[2] = c.At.Outside; }
        c.Leg = 1;
        c.Target = to;
        c.Now = Doing.Walking;
        c.OpeningDoor = leavingLobby;
        c.EnteringLobby = false;
        c.DoorSince = now;
        // Out through the lobby's door, opened from inside if it is shut; through it at the step outside.
        c.Passage = leavingLobby ? _manners.Reach(world, c.At!.Door, c.At.Inside, c.At.Outside, c.Entity) : null;
        c.ThroughAtLeg = 2;
        Log.Information("CHARACTER {Name}: leaving {From} for {To}, {Metres:F0} m on foot.",
                        c.Data.Name, c.At?.Name ?? "where he was", to.Name, PathLength(c.Route));
    }

    /// <summary>Reached the end of a walk: a lobby's door, or the place itself.</summary>
    private void Arrive(Character c, World world, double now, SpeechConditions cond)
    {
        var to = c.Target!;
        if (to.Kind == HauntKind.Lobby && !c.EnteringLobby)
        {
            // How he finds it. It is locked from the street, so he waits for it to be opened.
            c.Passage = _manners.Reach(world, to.Door, to.Outside, to.Inside, c.Entity, open: false);
            c.Now = Doing.AtDoor;
            c.DoorSince = now;
            c.FacingYaw = to.Facing + MathF.PI;           // facing the door
            Log.Information("CHARACTER {Name}: at the door of {Place}.", c.Data.Name, to.Name);
            return;
        }
        c.EnteringLobby = false;
        c.At = to;
        c.Target = null;
        c.Now = Doing.Lingering;
        c.FacingYaw = to.Facing;
        var rng = Draw(c);
        double stay = LingerSeconds(to.Kind, cond, rng) / Pace;
        c.Until = now + stay;
        Log.Information("CHARACTER {Name}: at {Place}, staying {Minutes:F1} min.", c.Data.Name, to.Name, stay / 60);
    }

    // ── Moving the body ─────────────────────────────────────────────────────────────────────────

    private static void Stand(Character c, ref Transform t, ref Velocity vel, double now)
    {
        vel.Linear = Vector3.Zero;
        float yaw = now < c.FaceUntil ? c.FaceYaw : c.FacingYaw;
        var rot = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
        if (t.Rotation != rot) { t.Rotation = rot; t.IsDirty = true; }
    }

    /// <summary>One tick along the route. True once he is at the end of it.</summary>
    private static bool Walk(Character c, ref Transform t, ref Velocity vel, float dt)
    {
        float left = WalkSpeed * dt;
        while (c.Leg < c.Route.Count)
        {
            var target = c.Route[c.Leg];
            var to = target - t.Position; to.Y = 0f;
            float dist = to.Length();
            if (dist < 0.05f) { t.Position = target; c.Leg++; continue; }
            var dir = to / dist;
            float step = MathF.Min(dist, left);
            float frac = step / dist;
            t.Position = new Vector3(t.Position.X + dir.X * step, t.Position.Y + (target.Y - t.Position.Y) * frac, t.Position.Z + dir.Z * step);
            t.Rotation = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(dir.X, dir.Z), 0f, 0f);
            t.IsDirty = true;
            vel.Linear = dir * WalkSpeed;
            left -= step;
            if (left <= 1e-4f) return false;
        }
        vel.Linear = Vector3.Zero;
        return true;
    }

    // ── The bus ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A bus standing at its stop near <paramref name="stop"/> with its doors open, or Entity.Null.</summary>
    private static Entity BusAt(World world, Vector3 stop)
    {
        Entity found = Entity.Null;
        world.Query(new QueryDescription().WithAll<Transform, OccupancyComponent, VehicleComponent, SoundEmitterComponent>(),
            (Entity e, ref Transform t, ref SoundEmitterComponent em) =>
            {
                if (found == Entity.Null && em.ServingStop && Flat(t.Position, stop) < AtStopMetres) found = e;
            });
        return found;
    }

    private static bool Standing(World world, Entity bus)
        => world.Has<SoundEmitterComponent>(bus) && world.Get<SoundEmitterComponent>(bus).ServingStop
           || !OccupancyService.Moving(world, bus) && world.Has<VehicleComponent>(bus) && MathF.Abs(world.Get<VehicleComponent>(bus).Speed) < 0.1f;

    /// <summary>The free passenger seat nearest a point, or -1.</summary>
    private static int FreeSeat(World world, Entity bus, Vector3 near)
    {
        if (!world.Has<OccupancyComponent>(bus)) return -1;
        var seats = world.Get<OccupancyComponent>(bus).Seats;
        if (seats == null) return -1;
        var rootT = world.Get<Transform>(bus);
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < seats.Count; i++)
        {
            if (seats[i].Controls || OccupancyService.SeatTaken(world, bus.Id, i)) continue;
            float d = Vector3.Distance(OccupancyService.SeatPosition(rootT, seats[i]), near);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Where a passenger steps on and off: the front door, on the kerb side (traffic keeps right).</summary>
    private static Vector3 DoorOf(World world, Entity bus)
    {
        var t = world.Get<Transform>(bus);
        var size = world.Has<ColliderComponent>(bus) ? world.Get<ColliderComponent>(bus).Size : new Vector3(2.5f, 3f, 12f);
        var forward = Vector3.Transform(Vector3.UnitZ, t.Rotation); forward.Y = 0f;
        var right = Vector3.Transform(Vector3.UnitX, t.Rotation); right.Y = 0f;
        if (forward.LengthSquared() < 1e-6f || right.LengthSquared() < 1e-6f) return t.Position;
        forward = Vector3.Normalize(forward); right = Vector3.Normalize(right);
        var p = t.Position + forward * (size.Z * 0.5f - 1.5f) + right * (size.X * 0.5f + 0.5f);
        return p with { Y = MathF.Max(0f, t.Position.Y - size.Y * 0.5f) };
    }

    // ── Choosing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Night, cold or wet: when somebody with nowhere to live looks for a roof.</summary>
    public static bool Sheltering(SpeechConditions c)
        => c.Hour >= 21f || c.Hour < 6f || c.TemperatureC <= 8f
           || (c.Weather is WeatherType.Rain or WeatherType.Storm or WeatherType.Snow && c.Precipitation >= 0.2f);

    /// <summary>How much a kind of place draws him, at this hour and in this weather.</summary>
    public static double KindWeight(HauntKind kind, SpeechConditions c)
    {
        if (Sheltering(c))
            return kind switch { HauntKind.Lobby => 6, HauntKind.BusStop => 2, HauntKind.Doorway => 0.6, _ => 0.15 };
        if (c.Hour >= 17f)
            return kind switch { HauntKind.Lobby => 1.5, HauntKind.BusStop => 3, HauntKind.Doorway => 2.5, _ => 1 };
        return kind switch { HauntKind.Lobby => 0.5, HauntKind.BusStop => 3, HauntKind.Doorway => 3, _ => 2 };
    }

    /// <summary>Of riding the bus from a bus stop rather than walking on: more in the cold and wet.</summary>
    public static double RideChance(SpeechConditions c) => Sheltering(c) ? 0.6 : 0.4;

    /// <summary>
    /// The next place, never the one he is at: weighted by kind for the hour and weather, and by
    /// distance (half as likely 150 m off).
    /// </summary>
    public static Haunt Choose(IReadOnlyList<Haunt> haunts, Haunt? current, Vector2? from, SpeechConditions cond, Random rng)
    {
        var weights = new double[haunts.Count];
        double total = 0;
        for (int i = 0; i < haunts.Count; i++)
        {
            var h = haunts[i];
            if (h == current && haunts.Count > 1) continue;
            double w = KindWeight(h.Kind, cond);
            if (from is { } f) w /= 1.0 + Vector2.Distance(f, new Vector2(h.Stand.X, h.Stand.Z)) / 150.0;
            weights[i] = w;
            total += w;
        }
        double pick = rng.NextDouble() * total;
        for (int i = 0; i < haunts.Count; i++)
        {
            pick -= weights[i];
            if (weights[i] > 0 && pick <= 0) return haunts[i];
        }
        return haunts[^1];
    }

    /// <summary>How long he stays, seconds: minutes, longer in a lobby when it is night or cold.</summary>
    public static double LingerSeconds(HauntKind kind, SpeechConditions c, Random rng)
    {
        bool shelter = Sheltering(c);
        var (lo, hi) = kind switch
        {
            HauntKind.Lobby => shelter ? (8.0, 20.0) : (3.0, 8.0),
            HauntKind.BusStop => (3.0, 9.0),
            HauntKind.Doorway => shelter ? (2.0, 5.0) : (3.0, 8.0),
            _ => shelter ? (2.0, 4.0) : (4.0, 10.0),
        };
        return 60.0 * (lo + (hi - lo) * rng.NextDouble());
    }

    /// <summary>The next draw of a character's day: seeded by the map, their name, the day and how many
    /// choices they have made, so the same day goes the same way.</summary>
    private Random Draw(Character c) => new(Seed(_seed, c.MapId, c.Data.Name, c.Day, c.Decisions++));

    public static int Seed(int seed, string mapId, string name, int day, int decision)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char ch in mapId + "/" + name) { h ^= ch; h *= 16777619; }
            h ^= (uint)seed; h *= 16777619;
            h ^= (uint)day; h *= 16777619;
            h ^= (uint)decision; h *= 16777619;
            return (int)(h & 0x7fffffff);
        }
    }

    // ── Asked from outside ──────────────────────────────────────────────────────────────────────

    /// <summary>What a character is doing, by their body's entity id; null for anybody else.</summary>
    public View? ViewOf(string mapId, int entityId)
    {
        foreach (var c in _all)
            if (c.MapId == mapId && c.Entity.Id == entityId && c.Entity != Entity.Null)
            {
                double now = _clocks.GetValueOrDefault(mapId);
                var place = c.Now is Doing.Lingering or Doing.WaitingForBus ? c.At
                          : c.Now == Doing.AtDoor ? c.Target : null;
                bool indoors = c.Now == Doing.Lingering && c.At?.Kind == HauntKind.Lobby || c.Now == Doing.Riding;
                string where = c.Now switch
                {
                    Doing.Lingering => c.At?.Name ?? "",
                    Doing.WaitingForBus => $"{c.At?.Name}, waiting for a bus to {c.RideTo?.Name}",
                    Doing.Walking => $"walking to {c.Target?.Name}",
                    Doing.AtDoor => $"at the door of {c.Target?.Name}",
                    Doing.Boarding => "getting on the bus",
                    Doing.Riding => $"on the bus, for {c.RideTo?.Name}",
                    _ => "",
                };
                return new View(c.Data.Name, c.Data.Voice, c.Now, place?.Kind, where, indoors, c.Until - now);
            }
        return null;
    }

    /// <summary>A character by name, anywhere: their map and their body, if they are in the world.</summary>
    public (string MapId, Entity Body)? Find(string name)
    {
        foreach (var c in _all)
            if (c.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && c.Entity != Entity.Null)
                return (c.MapId, c.Entity);
        return null;
    }

    /// <summary>Has a character turn to face a point for a while: somebody they are talking to.</summary>
    public void Face(string mapId, int entityId, Vector3 at, double seconds)
    {
        foreach (var c in _all)
            if (c.MapId == mapId && c.Entity.Id == entityId && _maps != null
                && _maps.TryGetMap(mapId, out var world, out _, out _, out _) && world.IsAlive(c.Entity))
            {
                var p = world.Get<Transform>(c.Entity).Position;
                var to = at - p;
                if (to.X * to.X + to.Z * to.Z < 1e-4f) return;
                c.FaceYaw = MathF.Atan2(to.X, to.Z);
                c.FaceUntil = _clocks.GetValueOrDefault(mapId) + seconds;
            }
    }

    // ── Small things ────────────────────────────────────────────────────────────────────────────

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private static Haunt? Nearest(IEnumerable<Haunt> haunts, Vector3 p, HauntKind kind)
        => haunts.Where(h => h.Kind == kind).OrderBy(h => Flat(h.Stand, p)).FirstOrDefault();

    private static float Ground(World world, SpatialGrid<Entity>? grid, Vector3 p)
    {
        if (grid == null) return p.Y;
        float g = PhysicsUtils.GetGroundHeight(world, grid, p, out _);
        return g < -900f ? p.Y : g;
    }

    private static float PathLength(IReadOnlyList<Vector3> route)
    {
        float m = 0f;
        for (int i = 1; i < route.Count; i++) m += Flat(route[i - 1], route[i]);
        return m;
    }

    private static string Clock(SpeechConditions c, double realSeconds)
    {
        // The game clock runs a minute a second by default; this is only for the log.
        return $"{realSeconds / 60:F1} real min from {(int)c.Hour:00}:{(int)(c.Hour % 1 * 60):00}";
    }
}
