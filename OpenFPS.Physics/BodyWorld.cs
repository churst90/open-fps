using System.Numerics;
using JoltPhysicsSharp;
using OpenFPS.Geometry;
using OpenFPS.Materials;

namespace OpenFPS.Physics;

/// <summary>How a body takes part in the world (docs/PHYSICS_BOUNDARY.md 18.2, step 2).</summary>
public enum BodyMotion : byte
{
    /// <summary>Never moves: a tile, a wall, the floor. May be a triangle mesh.</summary>
    Static = 0,
    /// <summary>Moved by the host (a door leaf, a person walked by the approved walk); pushes dynamic bodies and is
    /// never pushed. May be a triangle mesh.</summary>
    Kinematic = 1,
    /// <summary>Moved by the solver: gravity, contacts, friction. A sphere, box or capsule; Jolt has no dynamic mesh.</summary>
    Dynamic = 2,
}

/// <summary>What two surfaces do at a contact, from the material table (docs/MATTER.md 2.4).</summary>
public readonly record struct ContactMaterial(float Friction, float Restitution)
{
    /// <summary>Used where the table found no measurement for a material: not zero, because a missing number is not a
    /// frictionless surface. A plain hard surface against a plain hard surface.</summary>
    public const float UnmeasuredFriction = 0.5f;

    /// <summary>Used where the table found no restitution: a dull knock, not a bounce.</summary>
    public const float UnmeasuredRestitution = 0.1f;

    /// <summary>The material's own values: kinetic friction (a body that is moving is the one that matters), else static,
    /// and its restitution. Jolt joins two bodies' values as the table's pair rule says: friction the geometric mean,
    /// restitution the larger.</summary>
    public static ContactMaterial Of(string? material)
    {
        if (string.IsNullOrEmpty(material)) material = "Generic";
        var p = AcousticRegistry.GetProperties(material);
        return new ContactMaterial(p.KineticFriction ?? p.StaticFriction ?? UnmeasuredFriction,
                                   p.Restitution ?? UnmeasuredRestitution);
    }
}

/// <summary>
/// Rigid bodies, solved (docs/PHYSICS_BOUNDARY.md 18.2, steps 1 and 2): shapes and bodies by our own handles,
/// poses and velocities in and out as our own types, a step of the world at 120 Hz, and every body read back as a
/// <see cref="BodyState"/>. Jolt Physics is behind it and nothing outside this project names it (the
/// PhysicsBoundary guard). No game system uses a world yet.
///
/// Gravity is the world's: a value given in, <see cref="Earth.StandardGravity"/> unless told otherwise, never a
/// constant here. Stepping is on the calling thread alone (no worker threads), so a scene run twice gives the same
/// positions. Not thread safe: one thread steps and reads.
/// </summary>
public sealed class BodyWorld : IDisposable
{
    /// <summary>The step, s (docs/PHYSICS_BOUNDARY.md 11): four to a 30 Hz tick.</summary>
    public const float StepSeconds = 1f / 120f;

    public const int StepsPerTick = 4;

    private static readonly ObjectLayer NonMoving = 0;
    private static readonly ObjectLayer Moving = 1;

    // Jolt's one-time start is the process's, not a world's: it stays up for the process (a second world must not lose
    // the first's), and the native library is released when the process ends.
    private static readonly object InitLock = new();
    private static bool _started;

    /// <summary>Starts Jolt's native library if it is not running: false, with the reason, where it cannot be loaded.</summary>
    public static bool TryStartNative(out string? problem)
    {
        lock (InitLock)
        {
            problem = null;
            if (_started) return true;
            try
            {
                if (!Foundation.Init(false)) { problem = "Jolt's Foundation.Init returned false"; return false; }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                problem = e.Message;
                return false;
            }
            _started = true;
            return true;
        }
    }

    private sealed class Entry
    {
        public Body Body = null!;
        public int ShapeId;
        public BodyMotion Motion;
        public float MassKg;
    }

    private readonly PhysicsSystem _system;
    private readonly JobSystem _jobs;
    // The solver keeps pointers to these and does not own them: held here, or the collector frees them under it and the
    // process dies in the next step (2026-10-27, one run in two of BodyWorldTests).
    private readonly ObjectLayerPairFilterTable _pairs;
    private readonly BroadPhaseLayerInterfaceTable _broad;
    private readonly ObjectVsBroadPhaseLayerFilterTable _objectVsBroad;
    private readonly Dictionary<int, Shape> _shapes = new();
    private readonly SortedDictionary<int, Entry> _bodies = new();
    private int _nextShape = 1, _nextBody = 1;
    private bool _disposed;

    /// <param name="gravity">Metres per second squared, downward: the world's own (docs/PHYSICS_BOUNDARY.md, Decisions
    /// 2026-10-11).</param>
    public BodyWorld(float gravity = Earth.StandardGravity, int maxBodies = 4096)
    {
        if (!TryStartNative(out var problem)) throw new InvalidOperationException("Jolt Physics could not start: " + problem);
        _pairs = new ObjectLayerPairFilterTable(2);
        _pairs.EnableCollision(NonMoving, Moving);
        _pairs.EnableCollision(Moving, Moving);
        _broad = new BroadPhaseLayerInterfaceTable(2, 2);
        _broad.MapObjectToBroadPhaseLayer(NonMoving, 0);
        _broad.MapObjectToBroadPhaseLayer(Moving, 1);
        _objectVsBroad = new ObjectVsBroadPhaseLayerFilterTable(_broad, 2, _pairs, 2);
        _system = new PhysicsSystem(new PhysicsSystemSettings
        {
            MaxBodies = maxBodies, MaxBodyPairs = Math.Max(1024, maxBodies * 8), MaxContactConstraints = Math.Max(1024, maxBodies * 8),
            ObjectLayerPairFilter = _pairs, BroadPhaseLayerInterface = _broad, ObjectVsBroadPhaseLayerFilter = _objectVsBroad,
        });
        // No worker threads: the caller's thread runs every job, in the same order each time.
        _jobs = new JobSystemThreadPool(new JobSystemThreadPoolConfig { maxJobs = 2048, maxBarriers = 8, numThreads = 0 });
        Gravity = gravity;
    }

    /// <summary>Metres per second squared, downward; read from the solver, not remembered.</summary>
    public float Gravity
    {
        get { Check(); _system.GetGravity(out var g); return -g.Y; }
        set { Check(); _system.Gravity = new Vector3(0f, -value, 0f); }
    }

    public int BodyCount => _bodies.Count;

    public int ShapeCount => _shapes.Count;

    // ── Shapes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A ball of <paramref name="radius"/> metres; the shape's handle.</summary>
    public int AddSphere(float radius)
    {
        Check();
        return Keep(new SphereShape(Positive(radius, nameof(radius))));
    }

    /// <summary>A box of these half sizes (x east, y up, z north); the shape's handle.</summary>
    public int AddBox(Vector3 halfExtents)
    {
        Check();
        Positive(halfExtents.X, nameof(halfExtents)); Positive(halfExtents.Y, nameof(halfExtents)); Positive(halfExtents.Z, nameof(halfExtents));
        // Jolt rounds a box's edges by its convex radius (default 0.05 m) and shrinks the box to match, which must be
        // less than the smallest half size.
        float convex = MathF.Min(0.05f, MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z)) * 0.5f);
        return Keep(new BoxShape(halfExtents, convex));
    }

    /// <summary>An upright capsule: a cylinder of <paramref name="cylinderHalfHeight"/> either side of its middle, with a
    /// half ball of <paramref name="radius"/> on each end; the shape's handle.</summary>
    public int AddCapsule(float cylinderHalfHeight, float radius)
    {
        Check();
        return Keep(new CapsuleShape(Positive(cylinderHalfHeight, nameof(cylinderHalfHeight)), Positive(radius, nameof(radius))));
    }

    /// <summary>A triangle mesh from our own <see cref="MeshAsset"/> (its local frame, counter-clockwise from outside, as
    /// Jolt takes it: no mirror). For static and kinematic bodies only. The shape's handle.</summary>
    public int AddMesh(MeshAsset mesh)
    {
        Check();
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.TriangleCount == 0) throw new ArgumentException("a mesh with no triangles", nameof(mesh));
        var triangles = new IndexedTriangle[mesh.TriangleCount];
        for (int i = 0; i < triangles.Length; i++)
            triangles[i] = new IndexedTriangle(mesh.Indices[i * 3], mesh.Indices[i * 3 + 1], mesh.Indices[i * 3 + 2]);
        using var settings = new MeshShapeSettings(mesh.Vertices, triangles);
        return Keep(settings.Create());
    }

    /// <summary>A mesh made ahead (<see cref="BuiltMesh.Build"/>, on any thread) and put in this world: the world takes the
    /// shape over, so the mesh is not disposed by the caller after this. The shape's handle.</summary>
    public int AddMesh(BuiltMesh built)
    {
        Check();
        ArgumentNullException.ThrowIfNull(built);
        return Keep(built.Release());
    }

    /// <summary>The shape of a mesh, made the first time and shared after: bodies of one vehicle kind all stand on the
    /// one mesh (by its content hash). Let go of with <see cref="RemoveShape"/> like any other.</summary>
    public int AddMeshOnce(MeshAsset mesh)
    {
        Check();
        ArgumentNullException.ThrowIfNull(mesh);
        if (_meshShapes.TryGetValue(mesh.Hash, out int had) && _shapes.ContainsKey(had)) return had;
        int id = AddMesh(mesh);
        _meshShapes[mesh.Hash] = id;
        return id;
    }

    private readonly Dictionary<ulong, int> _meshShapes = new();

    /// <summary>Lets go of a shape no body uses.</summary>
    public void RemoveShape(int shapeId)
    {
        Check();
        if (_bodies.Values.Any(b => b.ShapeId == shapeId)) throw new InvalidOperationException($"shape {shapeId} is used by a body");
        if (!_shapes.Remove(shapeId, out var shape)) throw new KeyNotFoundException($"no shape {shapeId}");
        shape.Dispose();
        foreach (var kv in _meshShapes) if (kv.Value == shapeId) { _meshShapes.Remove(kv.Key); break; }
    }

    private int Keep(Shape shape)
    {
        int id = _nextShape++;
        _shapes[id] = shape;
        return id;
    }

    private static float Positive(float v, string name)
        => v > 0f && float.IsFinite(v) ? v : throw new ArgumentOutOfRangeException(name, v, "must be a positive, finite size");

    // ── Bodies ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a body of a shape at a pose and returns its handle (the <see cref="BodyState.Id"/>). Friction and
    /// restitution come from the material's own values (<see cref="ContactMaterial.Of"/>). A dynamic body needs its
    /// mass (kg); for the others it is ignored and the body is immovable.
    /// </summary>
    public int Add(int shapeId, BodyMotion motion, Vector3 position, Quaternion orientation, string material, float massKg = 0f)
    {
        Check();
        if (!_shapes.TryGetValue(shapeId, out var shape)) throw new KeyNotFoundException($"no shape {shapeId}");
        if (motion == BodyMotion.Dynamic)
        {
            if (!(massKg > 0f) || !float.IsFinite(massKg)) throw new ArgumentOutOfRangeException(nameof(massKg), massKg, "a dynamic body needs a positive mass");
            if (shape is MeshShape) throw new ArgumentException("a triangle mesh cannot be a dynamic body (Jolt has no dynamic mesh)", nameof(shapeId));
        }
        if (_bodies.Count >= _system.MaxBodies) throw new InvalidOperationException($"the world holds {_system.MaxBodies} bodies at most");

        var contact = ContactMaterial.Of(material);
        var joltMotion = motion switch { BodyMotion.Static => MotionType.Static, BodyMotion.Kinematic => MotionType.Kinematic, _ => MotionType.Dynamic };
        // Not BodyCreationSettings.MotionQuality: in 2.22.0 its setter passes the enum by reference to a C function that
        // takes it by value, so a body made with it got quality 244 and continuous collision silently did nothing
        // (docs/PHYSICS_BOUNDARY.md 14.8). The body interface's setter passes it by value.
        using var settings = new BodyCreationSettings(shape, position, orientation, joltMotion, motion == BodyMotion.Static ? NonMoving : Moving)
        {
            Friction = contact.Friction, Restitution = contact.Restitution,
            // Jolt damps every body by 5 percent a second unless told not to, a drag we never chose: the air's drag is
            // the physics' own law (docs/PHYSICS_BOUNDARY.md 16.8), so a body falls at the world's gravity alone.
            LinearDamping = 0f, AngularDamping = 0f,
            // A ball rolling over a flat mesh hops 55 mm at the seams without it: a sphere meeting an edge the mesh shares
            // with a coplanar neighbour takes the edge's normal. Costs nothing on shapes that are not meshes.
            EnhancedInternalEdgeRemoval = motion == BodyMotion.Dynamic,
        };
        if (motion == BodyMotion.Dynamic)
        {
            settings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
            settings.MassPropertiesOverride = new MassProperties { Mass = massKg };
        }
        var body = _system.BodyInterface.CreateBody(settings);
        _system.BodyInterface.AddBody(body, motion == BodyMotion.Static ? Activation.DontActivate : Activation.Activate);
        int id = _nextBody++;
        _bodies[id] = new Entry { Body = body, ShapeId = shapeId, Motion = motion, MassKg = motion == BodyMotion.Dynamic ? massKg : 0f };
        return id;
    }

    /// <summary>Takes a body out of the world and frees it. Its shape stays.</summary>
    public void Remove(int bodyId)
    {
        Check();
        if (!_bodies.Remove(bodyId, out var e)) throw new KeyNotFoundException($"no body {bodyId}");
        var bi = _system.BodyInterface;
        var jolt = e.Body.ID;
        bi.RemoveAndDestroyBody(jolt);
    }

    public bool Has(int bodyId) => _bodies.ContainsKey(bodyId);

    private Entry Get(int bodyId)
    {
        Check();
        return _bodies.TryGetValue(bodyId, out var e) ? e : throw new KeyNotFoundException($"no body {bodyId}");
    }

    /// <summary>Puts a body at a pose (metres, and the rotation it stands in). A body moved this way is not stepped
    /// there: its contacts are worked out at the next step.</summary>
    public void SetPose(int bodyId, Vector3 position, Quaternion orientation)
    {
        var e = Get(bodyId);
        var id = e.Body.ID;
        _system.BodyInterface.SetPositionAndRotation(id, position, orientation, e.Motion == BodyMotion.Static ? Activation.DontActivate : Activation.Activate);
    }

    /// <summary>Moves a kinematic body to a pose over <paramref name="seconds"/>, so it carries the velocity that gets it there
    /// and pushes what it meets; <see cref="SetPose"/> would put it there without a speed. The pose is reached at the
    /// next step.</summary>
    public void MoveKinematic(int bodyId, Vector3 position, Quaternion orientation, float seconds)
    {
        var e = Get(bodyId);
        if (e.Motion != BodyMotion.Kinematic) throw new InvalidOperationException($"body {bodyId} is not kinematic");
        if (!(seconds > 0f) || !float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "a positive time");
        var id = e.Body.ID;
        _system.BodyInterface.MoveKinematic(id, position, orientation, seconds);
    }

    public Vector3 Position(int bodyId)
    {
        var id = Get(bodyId).Body.ID;
        return _system.BodyInterface.GetPosition(id);
    }

    public Quaternion Orientation(int bodyId)
    {
        var id = Get(bodyId).Body.ID;
        return _system.BodyInterface.GetRotation(id);
    }

    /// <summary>Metres per second, in the world's axes.</summary>
    public void SetVelocity(int bodyId, Vector3 linear)
    {
        var id = Get(bodyId).Body.ID;
        _system.BodyInterface.SetLinearVelocity(id, linear);
    }

    public Vector3 Velocity(int bodyId)
    {
        var id = Get(bodyId).Body.ID;
        return _system.BodyInterface.GetLinearVelocity(id);
    }

    /// <summary>Radians per second about the world's axes (the vector's direction is the axis).</summary>
    public void SetAngularVelocity(int bodyId, Vector3 angular)
    {
        var id = Get(bodyId).Body.ID;
        _system.BodyInterface.SetAngularVelocity(id, angular);
    }

    public Vector3 AngularVelocity(int bodyId)
    {
        var id = Get(bodyId).Body.ID;
        return _system.BodyInterface.GetAngularVelocity(id);
    }

    /// <summary>Kilograms, as the solver holds it (its inverse mass, turned over): 0 for a body that cannot be moved by
    /// force.</summary>
    public float Mass(int bodyId)
    {
        var e = Get(bodyId);
        if (e.Motion != BodyMotion.Dynamic) return 0f;
        float inverse = e.Body.MotionProperties.InverseMassUnchecked;
        return inverse > 0f ? 1f / inverse : 0f;
    }

    /// <summary>Gives a dynamic body this mass, keeping its shape's inertia in proportion.</summary>
    public void SetMass(int bodyId, float massKg)
    {
        var e = Get(bodyId);
        if (e.Motion != BodyMotion.Dynamic) throw new InvalidOperationException($"body {bodyId} is not dynamic");
        if (!(massKg > 0f) || !float.IsFinite(massKg)) throw new ArgumentOutOfRangeException(nameof(massKg), massKg, "a positive mass");
        e.Body.MotionProperties.ScaleToMass(massKg);
        e.MassKg = massKg;
    }

    public float Friction(int bodyId)
    {
        var id = Get(bodyId).Body.ID;
        return _system.BodyInterface.GetFriction(id);
    }

    public void SetFriction(int bodyId, float friction)
    {
        var id = Get(bodyId).Body.ID;
        _system.BodyInterface.SetFriction(id, friction);
    }

    public float Restitution(int bodyId)
    {
        var id = Get(bodyId).Body.ID;
        return _system.BodyInterface.GetRestitution(id);
    }

    public void SetRestitution(int bodyId, float restitution)
    {
        var id = Get(bodyId).Body.ID;
        _system.BodyInterface.SetRestitution(id, restitution);
    }

    /// <summary>The body's state as the physics leaves it (docs/PHYSICS_BOUNDARY.md 4.5). A static body is
    /// <see cref="BodyState.Sleeping"/>: it is at rest and will stay so.</summary>
    public BodyState State(int bodyId) => StateOf(bodyId, Get(bodyId));

    private BodyState StateOf(int bodyId, Entry e)
    {
        var bi = _system.BodyInterface;
        var id = e.Body.ID;
        bi.GetLinearAndAngularVelocity(id, out var linear, out var angular);
        return new BodyState(bodyId, bi.GetPosition(id), bi.GetRotation(id), linear, angular, e.MassKg > 0f ? Mass(bodyId) : 0f,
                             e.ShapeId, Grounding.Unknown, Sleeping: !bi.IsActive(id));
    }

    /// <summary>Every body's state, by handle, into <paramref name="into"/> (cleared first).</summary>
    public void ReadBack(List<BodyState> into)
    {
        Check();
        into.Clear();
        foreach (var (id, e) in _bodies) into.Add(StateOf(id, e));
    }

    // ── Stepping ───────────────────────────────────────────────────────────────────────────────

    /// <summary>One 120 Hz step.</summary>
    public void Step()
    {
        Check();
        var error = _system.Update(StepSeconds, 1, _jobs);
        if (error != PhysicsUpdateError.None) throw new InvalidOperationException($"Jolt step: {error}");
    }

    /// <summary>A server tick's worth: <see cref="StepsPerTick"/> steps.</summary>
    public void Tick()
    {
        for (int i = 0; i < StepsPerTick; i++) Step();
    }

    /// <summary>Rebuilds the broad phase once after many bodies were added together (a tile's worth), so the first
    /// step does not pay for it.</summary>
    public void Optimize()
    {
        Check();
        _system.OptimizeBroadPhase();
    }

    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var bi = _system.BodyInterface;
        foreach (var e in _bodies.Values)
        {
            var id = e.Body.ID;
            bi.RemoveAndDestroyBody(id);
        }
        _bodies.Clear();
        foreach (var shape in _shapes.Values) shape.Dispose();
        _shapes.Clear();
        _jobs.Dispose();
        _system.Dispose();
        _objectVsBroad.Dispose();
        _broad.Dispose();
        _pairs.Dispose();
    }
}
