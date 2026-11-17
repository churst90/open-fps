using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Arch.Core;
using OpenFPS.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using OpenFPS.Physics;
using OpenFPS.Server.Core;
using OpenFPS.Sound;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// The server's body worlds (docs/PHYSICS_BOUNDARY.md 18.2 step 7, 18.11): one per loaded map, with the map's own
/// gravity, its tiles and door leaves as Jolt meshes through <see cref="PieceBodies"/>, stepped once a server tick,
/// and its contacts delivered into the map's <see cref="ContactStream"/>. Only things that are moving live in it: a
/// dropped or thrown item is a dynamic body while it flies and bounces, and when the solver puts it to sleep the
/// body leaves the world and the item lies where it stopped, the Transform everything else reads unchanged.
///
/// <para>Heavy work is off the tick. A tile's or leaf's shapes are built on one worker thread per map
/// (<see cref="PieceBodies.Prepare"/>), and the tick only registers shapes and adds bodies four at a time
/// (<see cref="PieceBodies.Pump"/>), rebuilding the broad phase once per tile; that is the 18.7 way the 18 to 72 ms
/// first-add spikes are kept out of a tick. A map is not ready for a body until every tile it has is in and the
/// queue is empty, so a drop during the first seconds of a map is the old scripted drop, never a fall through a
/// floor that is still being made.</para>
///
/// <para>One thread owns each map's world (the tick that steps it). <see cref="TryLaunch"/> may be called from that
/// thread between ticks, as a command is handled at the top of one.</para>
/// </summary>
public static class BodyWorldSystem
{
    /// <summary>Bodies a map may add in one tick, so a tile arrives over a few ticks (18.7: 514 city leaves cost
    /// 2 ms at four a call).</summary>
    public const int BodiesPerTick = 4;

    private static readonly ConditionalWeakTable<World, MapBodies> _states = new();

    /// <summary>How many item bodies are moving on this map.</summary>
    public static int MovingCount(World world) => _states.TryGetValue(world, out var state) ? state.MovingCount : 0;

    /// <summary>Whether this item is one of them, a body in the world now.</summary>
    public static bool IsMoving(World world, Entity item)
        => _states.TryGetValue(world, out var state) && state.IsMoving(item);

    /// <summary>Whether the map's body world is up, its ground and leaves all in: a drop can become a body.</summary>
    public static bool IsReady(World world) => _states.TryGetValue(world, out var state) && state.Ready;

    /// <summary>
    /// The item leaves the hand as a dynamic body at its own pose with this velocity. False (with the reason) when
    /// the map has no body world yet, its ground is not all in, or the thing is not an item: the caller keeps the
    /// old scripted drop for that one.
    /// </summary>
    public static bool TryLaunch(World world, Entity item, string name, Vector3 velocity, out string why)
    {
        why = "";
        if (!_states.TryGetValue(world, out var state)) { why = "the map has no body world"; return false; }
        if (!state.Ready) { why = "the map's ground is still being made"; return false; }
        return state.Launch(world, item, name, velocity, out why);
    }

    /// <summary>
    /// One tick of a map's bodies, after the geometry has been synchronised and before the contact stream closes:
    /// the world follows the tiles and leaves, takes a few queued bodies, steps its four steps, hears every contact
    /// that begins, and follows every flying item's transform, letting go of the ones that have come to rest.
    /// </summary>
    public static void Update(string mapId, World world, ServerGeometry? geometry, ContactStream contacts,
                              Action<int, string, IReadOnlyList<TransientSound>>? heard = null)
    {
        if (geometry == null) return;
        var state = _states.GetValue(world, w => new MapBodies(mapId, w, geometry.Builder.TileMetres));
        state.Update(world, geometry, contacts, heard);
    }

    /// <summary>Frees a map's body world (a test rig, or the server's shutdown). Safe when there is none.</summary>
    public static void Forget(World world)
    {
        if (_states.TryGetValue(world, out var state) && _states.Remove(world)) state.Dispose();
    }

    private sealed class Flight
    {
        public Entity Item;
        public int Body;
        public int Shape;
        public string Name = "";
        public ItemShape ItemShape;
    }

    private sealed class MapBodies : IDisposable
    {
        private readonly string _mapId;
        private readonly BodyWorld _world;
        private readonly PieceBodies _pieces;
        private readonly Dictionary<int, Flight> _flights = new();

        // Prepared pieces, by tile (key and signature) and by a leaf build's signature, waiting for the tick that
        // hands them to the feeder. Shapes are made on the worker (PieceBodies.Prepare) and never on the tick.
        private readonly object _gate = new();
        private readonly Dictionary<(TileKey Key, ulong Signature), PreparedPiece> _tilesReady = new();
        private readonly Dictionary<ulong, PreparedPiece> _leavesReady = new();
        private readonly HashSet<(TileKey Key, ulong Signature)> _tilesComing = new();
        private readonly HashSet<ulong> _leavesComing = new();
        // What the world holds this tick: a piece is a leaf or a tile by whether an instance owns it, and the
        // prepared callback only receives the piece (PieceBodies.Follow's shape).
        private readonly HashSet<(TileKey Key, ulong Signature)> _tilesWanted = new();
        private readonly HashSet<ulong> _leavesWanted = new();
        private readonly ConcurrentQueue<PendingPiece> _work = new();
        private readonly SemaphoreSlim _wake = new(0);
        private readonly Thread _worker;
        private readonly float _minimumY;
        private int _wantedTiles;
        private volatile bool _disposed;

        private readonly record struct PendingPiece(bool Leaf, TileKey Key, ulong Signature, GeometryPiece Piece);

        public MapBodies(string mapId, World world, float tileMetres)
        {
            _mapId = mapId;
            _world = new BodyWorld(PhysicsConstants.GravityOf(world)) { ReportContacts = true };
            _pieces = new PieceBodies(_world);
            float lowest = float.MinValue;
            world.Query(new QueryDescription().WithAll<ZoneComponent>(), (ref ZoneComponent zone) => lowest = MathF.Max(lowest, zone.MinimumY));
            _minimumY = lowest;
            // One worker a map, below normal priority: building a tile's tree must not take the tick's time or a
            // player's keystroke. It ends when the map is forgotten.
            _worker = new Thread(Work) { IsBackground = true, Name = $"bodies {mapId}", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
            Log.Information("Bodies on {Map}: a world at {Gravity:F3} m/s^2.", mapId, _world.Gravity);
        }

        public int MovingCount => _flights.Count;

        public bool Ready => !_disposed && _wantedTiles > 0 && _pieces.TileCount >= _wantedTiles && _pieces.PendingBodies == 0;

        public bool IsMoving(Entity item)
        {
            foreach (var flight in _flights.Values) if (flight.Item == item) return true;
            return false;
        }

        public void Update(World world, ServerGeometry geometry, ContactStream contacts,
                           Action<int, string, IReadOnlyList<TransientSound>>? heard)
        {
            if (_disposed) return;
            _tilesWanted.Clear();
            _leavesWanted.Clear();
            int wanted = 0;
            foreach (ref readonly var inst in geometry.World.Instances)
            {
                if (inst.Piece.IsEmpty) continue;
                if (inst.Owner >= 0) _leavesWanted.Add(inst.Piece.Signature);
                else { _tilesWanted.Add((inst.Piece.Key, inst.Piece.Signature)); wanted++; }
            }
            _wantedTiles = wanted;

            // Nothing here is heavy: the difference to the last world, and a few bodies of the queue.
            _pieces.Follow(geometry.World, Prepared);
            _pieces.Pump(BodiesPerTick);

            _world.Tick(contacts);
            Sounded(world, contacts, heard);
            FollowFlights(world);
        }

        // ═══ Bodies for items ═════════════════════════════════════════════════════════════════════

        public bool Launch(World world, Entity item, string name, Vector3 velocity, out string why)
        {
            why = "";
            if (!DroppedThings.TryBody(world, item, out ItemShape itemShape, out float mass, out string material))
            { why = "not an item"; return false; }
            var t = world.Get<Transform>(item);
            var rotation = t.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : t.Rotation;
            int shape = _world.AddBox(itemShape.Size * 0.5f);
            int body;
            try
            {
                body = _world.Add(shape, BodyMotion.Dynamic, t.Position, rotation, material, mass);
            }
            catch
            {
                _world.RemoveShape(shape);
                throw;
            }
            _world.SetVelocity(body, velocity);
            _flights[body] = new Flight { Item = item, Body = body, Shape = shape, Name = name, ItemShape = itemShape };
            return true;
        }

        /// <summary>Follows every flying item's transform, and lets go of the body when the solver says it rests.</summary>
        private void FollowFlights(World world)
        {
            foreach (var flight in _flights.Values.ToList())
            {
                var item = flight.Item;
                if (!world.IsAlive(item) || world.Has<HeldComponent>(item)) { Remove(flight); continue; }

                var state = _world.State(flight.Body);
                var t = world.Get<Transform>(item);
                t.Position = state.Position;
                t.Rotation = state.Orientation;
                t.IsDirty = true;
                world.Set(item, t);
                if (world.Has<Velocity>(item))
                {
                    var v = world.Get<Velocity>(item);
                    v.Linear = state.Velocity;
                    world.Set(item, v);
                }

                // A body below the map's void has fallen through ground that was taken out from under it (a world
                // tile unloaded): it is gone, and the item is left where it was last seen.
                if (_minimumY > float.MinValue && state.Position.Y < _minimumY - 1f) { Remove(flight); continue; }
                // Asleep is at rest: Jolt's own half second under its sleep speed (measured in 18.9). The transform
                // above is its last pose, where the item now lies for everything else that reads it.
                if (state.Sleeping) Remove(flight);
            }
        }

        private void Remove(Flight flight)
        {
            _flights.Remove(flight.Body);
            _world.Remove(flight.Body);
            _world.RemoveShape(flight.Shape);
        }

        // ═══ What a contact sounds like ═══════════════════════════════════════════════════════════

        private void Sounded(World world, ContactStream contacts, Action<int, string, IReadOnlyList<TransientSound>>? heard)
        {
            if (heard == null) return;
            foreach (var contact in contacts.Events)
            {
                if (contact.Phase != ContactPhase.Began) continue;
                if (!BodyWorld.IsContactId(contact.Body)) continue;
                if (!_flights.TryGetValue(BodyWorld.BodyOfContactId(contact.Body), out var flight)) continue;

                // The other side: another flying item (its own mass and box), or a tile or leaf (fixed, a mesh with
                // no panel size of its own).
                Flight? other = null;
                bool otherIsItem = BodyWorld.IsContactId(contact.Other)
                                   && _flights.TryGetValue(BodyWorld.BodyOfContactId(contact.Other), out other);
                float struckMass = otherIsItem ? _world.Mass(other!.Body) : DroppedThings.FixedMassKg;
                var struckShape = otherIsItem
                    ? other!.ItemShape
                    : new ItemShape(ItemShapeKind.Block,
                                    new Vector3(DroppedThings.FloorPanelWidthMetres, DroppedThings.FloorPanelHeightMetres,
                                                DroppedThings.FloorPanelThicknessMetres));
                var sounds = DroppedThings.Impact(contact, _world.Mass(flight.Body), struckMass,
                                                  flight.ItemShape, struckShape, struckFixed: !otherIsItem);
                if (sounds.Count > 0) heard(flight.Item.Id, flight.Name, sounds);
            }
        }

        // ═══ Prepared pieces ══════════════════════════════════════════════════════════════════════

        private PreparedPiece? Prepared(GeometryPiece piece)
        {
            lock (_gate)
            {
                if (_leavesWanted.Contains(piece.Signature))
                {
                    if (_leavesReady.Remove(piece.Signature, out var leaf)) return leaf;
                    if (_leavesComing.Add(piece.Signature)) Wake(new PendingPiece(true, piece.Key, piece.Signature, piece));
                    return null;
                }
                var key = (piece.Key, piece.Signature);
                if (_tilesReady.Remove(key, out var tile)) return tile;
                if (_tilesComing.Add(key)) Wake(new PendingPiece(false, piece.Key, piece.Signature, piece));
                return null;
            }
        }

        private void Wake(PendingPiece work)
        {
            _work.Enqueue(work);
            _wake.Release();
        }

        private void Work()
        {
            while (!_disposed)
            {
                _wake.Wait(TimeSpan.FromMilliseconds(100));
                while (_work.TryDequeue(out var work))
                {
                    PreparedPiece? prepared = null;
                    try { prepared = PieceBodies.Prepare(work.Piece); }
                    catch (Exception ex) { Log.Error(ex, "Bodies on {Map}: a piece could not be made.", _mapId); }
                    lock (_gate)
                    {
                        if (work.Leaf) _leavesComing.Remove(work.Signature);
                        else _tilesComing.Remove((work.Key, work.Signature));
                        if (prepared == null) continue;
                        if (work.Leaf)
                        {
                            if (_leavesReady.ContainsKey(work.Signature)) prepared.Dispose();
                            else _leavesReady[work.Signature] = prepared;
                        }
                        else if (_tilesReady.ContainsKey((work.Key, work.Signature))) prepared.Dispose();
                        else _tilesReady[(work.Key, work.Signature)] = prepared;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _wake.Release();
            _worker.Join(TimeSpan.FromSeconds(5));
            lock (_gate)
            {
                foreach (var piece in _tilesReady.Values) piece.Dispose();
                foreach (var piece in _leavesReady.Values) piece.Dispose();
                _tilesReady.Clear();
                _leavesReady.Clear();
            }
            _flights.Clear();
            _pieces.Dispose();
            _world.Dispose();
            _wake.Dispose();
        }
    }
}
