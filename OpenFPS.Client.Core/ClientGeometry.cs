using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// The client's static geometry as a triangle world (docs/GEOMETRY.md stage 1), kept up with the
/// definitions it holds.
///
/// <para>Built off the game thread: a tile arriving is a few thousand boxes, and building their tile's
/// tree on the thread that runs the frame would cost the frame. While a build runs, the last one serves,
/// and the owners whose solid changed since that one was started are handed to each snapshot as stale
/// (not counted from the triangles) and, if they still exist, as unindexed (tested the old way), so a
/// query is never wrong while the triangles catch up; it is only slower for those few things.</para>
///
/// <para>Door leaves are movers: their poses follow the snapshot's transforms every time a snapshot is
/// made, with no build at all.</para>
/// </summary>
public sealed class ClientGeometry
{
    private readonly object _lock = new();
    private TriangleWorldBuilder _builder;
    private TriangleWorld? _published;
    private HashSet<int> _dirty = new();
    private HashSet<int>? _inFlight;
    private bool _running, _everStarted;
    private long _epoch;
    private readonly HashSet<int> _unindexedRole = new();
    private readonly HashSet<int> _movedMovers = new();
    private readonly HashSet<int> _moverIds = new();

    // What the last snapshot was handed, kept while nothing changes it.
    private long _changes, _handedAt = -1;
    private IReadOnlySet<int>? _handedStale;
    private List<int> _handedUnindexed = new();
    private TriangleWorld? _posed;

    /// <summary>Runs a build off the calling thread. The game uses a niced thread; a test runs it in place.</summary>
    public Action<Action> Runner { get; set; } = work => Platform.BackgroundPriority.RunLowered("Geometry", work);

    /// <summary>Called when a build has been published, so the next snapshot picks it up.</summary>
    public Action? Published { get; set; }

    /// <summary>Builds finished, and how long the last took, milliseconds; pieces built by it. Diagnostic.</summary>
    public int Builds { get; private set; }
    public double LastBuildMs { get; private set; }
    public int LastPiecesBuilt { get; private set; }

    public ClientGeometry(float tileMetres) => _builder = new TriangleWorldBuilder(tileMetres) { Parallel = false };

    /// <summary>A new map: everything held goes, and a build for the last map is thrown away when it ends.</summary>
    public void Reset(float tileMetres)
    {
        lock (_lock)
        {
            _epoch++;
            _builder = new TriangleWorldBuilder(tileMetres) { Parallel = false };
            _published = null; _posed = null;
            _dirty = new HashSet<int>(); _inFlight = null;
            _running = false; _everStarted = false;
            _unindexedRole.Clear(); _movedMovers.Clear(); _moverIds.Clear();
            _changes++;
        }
    }

    /// <summary>The map's tiles are another size: everything is built again in the new tiles.</summary>
    public void Retile(float tileMetres)
    {
        lock (_lock)
        {
            if (_builder.TileMetres == (tileMetres > 0f ? tileMetres : 250f)) return;
            _epoch++;
            _builder = new TriangleWorldBuilder(tileMetres) { Parallel = false };
            _running = false; _everStarted = false;
            if (_inFlight != null) { _dirty.UnionWith(_inFlight); _inFlight = null; }
            _changes++;
        }
    }

    /// <summary>
    /// A definition arrived, changed or went (<paramref name="after"/> null). Only a change to what the
    /// triangle world would hold of it counts: the same box re-sent, or a door re-sent where it swung to,
    /// is not a change to its solid.
    /// </summary>
    public void Note(EntityDefinition? before, EntityDefinition? after)
    {
        var rb = EntityGeometry.RoleOf(before);
        var ra = EntityGeometry.RoleOf(after);
        if (rb == GeometryRole.None && ra == GeometryRole.None) return;
        int id = after?.EntityId ?? before!.EntityId;
        lock (_lock)
        {
            if (ra == GeometryRole.Unindexed) _unindexedRole.Add(id); else _unindexedRole.Remove(id);
            if (ra == GeometryRole.Mover) _moverIds.Add(id); else _moverIds.Remove(id);
            System.Threading.Volatile.Write(ref _moverCount, _moverIds.Count);
            bool inBefore = rb is GeometryRole.Static or GeometryRole.Mover or GeometryRole.SightOnly;
            bool inAfter = ra is GeometryRole.Static or GeometryRole.Mover or GeometryRole.SightOnly;
            bool same = rb == ra && inAfter && SameSolid(before!, after!, ra);
            if (ra == GeometryRole.Mover && same) _movedMovers.Add(id);
            if ((inBefore || inAfter) && !same) _dirty.Add(id);
            _changes++;
        }
    }

    /// <summary>A transform changed: if it is a door leaf's, its pose follows at the next snapshot.</summary>
    public void NoteMoved(int id)
    {
        if (System.Threading.Volatile.Read(ref _moverCount) == 0) return;
        lock (_lock)
        {
            if (_moverIds.Contains(id)) { _movedMovers.Add(id); _changes++; }
        }
    }

    private int _moverCount;

    /// <summary>Whether two definitions make the same solid, wherever a mover stands.</summary>
    private static bool SameSolid(EntityDefinition a, EntityDefinition b, GeometryRole role)
    {
        var sa = EntityGeometry.SpecOf(a, a.Transform, role);
        var sb = EntityGeometry.SpecOf(b, b.Transform, role);
        if (role == GeometryRole.Mover) { sa = sa with { Position = Vector3.Zero, Rotation = Quaternion.Identity }; sb = sb with { Position = Vector3.Zero, Rotation = Quaternion.Identity }; }
        return sa.Equals(sb);
    }

    /// <summary>
    /// What a snapshot gets: the triangle world (its door leaves at the poses <paramref name="poseOf"/>
    /// gives), the owners it holds stale, and the static solids to test the old way. Starts a build when
    /// something changed and none is running; <paramref name="collect"/> is called on the build's thread
    /// for every definition with its transform.
    /// </summary>
    public (TriangleWorld? World, IReadOnlySet<int>? Stale, List<int> Unindexed) ForSnapshot(
        Func<IEnumerable<(EntityDefinition Def, Transform Transform)>> collect,
        Func<int, bool> exists,
        Func<int, (bool Known, Vector3 Position, Quaternion Rotation)> poseOf)
    {
        TriangleWorld? world;
        List<int>? moved = null;
        lock (_lock)
        {
            if (!_running && (_dirty.Count > 0 || !_everStarted)) Start(collect);
            world = _published;
            if (_movedMovers.Count > 0) { moved = new List<int>(_movedMovers); _movedMovers.Clear(); }
            if (_handedAt != _changes)
            {
                HashSet<int>? stale = null;
                if (_inFlight is { Count: > 0 }) (stale ??= new HashSet<int>()).UnionWith(_inFlight);
                if (_dirty.Count > 0) (stale ??= new HashSet<int>()).UnionWith(_dirty);
                var unindexed = new List<int>(_unindexedRole);
                if (stale != null) foreach (int id in stale) if (exists(id)) unindexed.Add(id);
                unindexed.Sort();
                _handedStale = stale;
                _handedUnindexed = unindexed;
                _handedAt = _changes;
            }
            if (world == null) return (null, _handedStale, _handedUnindexed);
            if (_posed != null && _posed.Version >= world.Version && moved == null) return (_posed, _handedStale, _handedUnindexed);
            if (_posed == null || _posed.Version < world.Version)
                _posed = world.WithMoverPoses(poseOf);                  // a new build: every leaf where it now is
            else if (moved != null)
            {
                var only = new HashSet<int>(moved);
                _posed = _posed.WithMoverPoses(id => only.Contains(id) ? poseOf(id) : (false, default, default));
            }
            return (_posed, _handedStale, _handedUnindexed);
        }
    }

    private void Start(Func<IEnumerable<(EntityDefinition Def, Transform Transform)>> collect)
    {
        _running = true;
        _everStarted = true;
        _inFlight = _dirty;
        _dirty = new HashSet<int>();
        _changes++;
        long epoch = _epoch;
        var builder = _builder;
        Runner(() =>
        {
            TriangleWorld? built = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var statics = new List<SolidSpec>();
                var movers = new List<SolidSpec>();
                foreach (var (def, t) in collect())
                {
                    var role = EntityGeometry.RoleOf(def);
                    if (role is GeometryRole.Static or GeometryRole.SightOnly) statics.Add(EntityGeometry.SpecOf(def, t, role));
                    else if (role == GeometryRole.Mover) movers.Add(EntityGeometry.SpecOf(def, t, role));
                }
                // The first build of a map (at join) on every core; after that a tile or two on this niced thread.
                builder.Parallel = builder.TileCount == 0;
                built = builder.Build(statics, movers);
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "ClientGeometry: the triangle world could not be built."); }
            lock (_lock)
            {
                if (epoch != _epoch) return;                            // a new map since: not this one's
                _running = false;
                if (built != null)
                {
                    _published = built;
                    _inFlight = null;
                    Builds++;
                    LastBuildMs = clock.Elapsed.TotalMilliseconds;
                    LastPiecesBuilt = builder.LastBuilt;
                }
                else
                {
                    // Nothing published: what was in flight is still owed.
                    if (_inFlight != null) _dirty.UnionWith(_inFlight);
                    _inFlight = null;
                }
                _changes++;
            }
            Published?.Invoke();
        });
    }
}
