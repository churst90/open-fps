using System.Numerics;
using OpenFPS.Materials;
using OpenFPS.Sound;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;

namespace OpenFPS.Audio.Acoustics;

/// <summary>
/// The walls answering a live engine. An engine is synthesized in the mixer, so there is no file to
/// replay; an echo voice reads the engine's ring buffer (EngineVoiceState) back at the mirrored path's
/// delay, placed at the image source. The read position is slewed, which is the reflection's Doppler,
/// so the echo voice carries no velocity of its own. The faces worth bouncing off are kept between
/// frames so a reflection holds one voice instead of clicking at every rescan.
/// </summary>
public sealed class EngineReflections
{
    /// <summary>What the world's dispose waits on for this one's background tasks; null when made alone.</summary>
    internal OpenFPS.Audio.Core.BackgroundWork? Work { get; set; }

    /// <summary>A face smaller than this both ways is something sound goes round at engine frequencies,
    /// not a mirror (ImageSource's Fresnel test would give it almost nothing); dropped before the scan.</summary>
    private const float MinFaceHalfExtent = 1.5f;

    /// <summary>Two: the retaining wall in front and whatever big flat thing is behind you.</summary>
    public const int MaxEchoesPerEngine = 2;

    /// <summary>
    /// How many are rendered now: the first thing given up when the mixer runs short. A reflection is
    /// cheap to generate and as dear to place as any source (HRTF, filters); eight with four engines is
    /// most of a mixer. Shed before cars: a listener notices a car vanishing, not its second reflection.
    /// </summary>
    public int EchoesPerEngine { get; set; } = MaxEchoesPerEngine;

    /// <summary>
    /// Reflection voices at once across the map: a voice budget, not a limit on how many cars may be
    /// answered. It goes to the most audible reflections wherever they come from (<see cref="AudibilityFloor"/>):
    /// one loud car by a concrete wall may take several, thirty distant ones off a soft surface none.
    /// </summary>
    public int MaxReflectionVoices { get; set; } = 16;

    /// <summary>
    /// The level a reflection must deliver to the ear to be worth a voice, set each frame so that about
    /// <see cref="MaxReflectionVoices"/> clear it. The Nth value taken outright, not servoed: a control
    /// loop would hunt, and hunting is a reflection fading in and out. One frame stale.
    /// </summary>
    public float AudibilityFloor => _audibilityFloor;
    private float _audibilityFloor;
    private readonly List<float> _audibilities = new();

    /// <summary>Works out the next frame's floor. Once per audio update, after every source has been
    /// offered to <see cref="Update"/>.</summary>
    public void EndFrame() => EndFrame(default);

    public void EndFrame(Vector3 listener)
    {
        if (_audibilities.Count <= MaxReflectionVoices)
        {
            // Everything can have a voice; the image-source search has its own absolute floor.
            _audibilityFloor = 0f;
        }
        else
        {
            _audibilities.Sort();
            _audibilityFloor = _audibilities[_audibilities.Count - MaxReflectionVoices];
        }
        _audibilities.Clear();
        if (!PrecomputeInBackground) return;
        if (_finding is { IsCompleted: true } done)
        {
            _finding = null;
            if (done.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) _found = done.Result;
        }
        if (_finding != null || _wanted.Count == 0) return;
        // The image-source search for every engine is 30 ms on the city's thirty-odd live cars. Done
        // here, on the frame's own thread, it held every source still: built ahead instead, and used
        // by the next frame's voices (a reflection a frame late is a metre of a car's motion).
        var wants = new Dictionary<int, Want>(_wanted);
        _wanted.Clear();
        var surfaces = _surfaces; var nodes = _faceNodes; var order = _faceOrder; var boxes = _boxes; var world = _world;
        _finding = BackgroundWork.Run(Work, "EngineReflections find", () => FindAll(wants, surfaces, nodes, order, boxes, world));
    }

    /// <summary>The first-order reflections found off the placement thread, by engine. Read and written
    /// only on the thread that calls <see cref="Update"/> and <see cref="EndFrame"/>.</summary>
    private Dictionary<int, Reflection[]> _found = new();

    private readonly record struct Want(Vector3 Source, Vector3 Listener, float SpeedOfSound, int Count);

    private readonly Dictionary<int, Want> _wanted = new();
    private System.Threading.Tasks.Task<Dictionary<int, Reflection[]>>? _finding;

    /// <summary>Whether the image-source search is built ahead on a worker thread (the game). A test or
    /// the replay builds it in place, where every frame must come out the same.</summary>
    internal bool PrecomputeInBackground { get; set; }

    private static Dictionary<int, Reflection[]> FindAll(Dictionary<int, Want> wants,
                                                          List<ReflectingSurface> surfaces,
                                                          OpenFPS.Geometry.BvhNode[]? nodes, int[] order,
                                                          List<OpenFPS.Audio.SteamAudio.SteamAudioScene.Box> boxes,
                                                          OpenFPS.Geometry.TriangleWorld? world)
    {
        OpenFPS.Audio.Core.PlacementThread.Heavy("The engines' reflections");
        var found = new Dictionary<int, Reflection[]>(wants.Count);
        var near = new List<ReflectingSurface>();
        var hits = new List<int>();
        foreach (var (id, want) in wants)
        {
            var into = new Reflection[MaxEchoesPerEngine];
            int n = FirstOrderNear(near, hits, surfaces, nodes, order, boxes, world,
                                   want.Source, want.Listener, want.SpeedOfSound, into);
            found[id] = n == 0 ? Array.Empty<Reflection>() : into[..n];
        }
        return found;
    }

    /// <summary>Metres from the path's middle: a reflection off anything further belongs in the reverb tail.</summary>
    private const float SurfaceSearchRadius = 260f;

    /// <summary>An echo no longer found fades this long before its voice goes, so a car passing a gap in
    /// the wall does not click.</summary>
    private const float ReleaseSeconds = 0.8f;

    private List<ReflectingSurface> _surfaces = new();
    /// <summary>The boxes the surfaces came from, for the obstruction test: one list, so the mirror and the
    /// obstruction cannot disagree.</summary>
    private List<OpenFPS.Audio.SteamAudio.SteamAudioScene.Box> _boxes = new();
    private int _builtFrom = -1;
    private int _lastEntityCount = -1;

    private sealed class Echo
    {
        /// <summary>Which echo it is, counted over every engine: what its id and seed are given by (VoiceOf).</summary>
        public int Number;
        public float Silent;
        public bool Seen;
        /// <summary>What the release fades out: a dying echo is not moved.</summary>
        public Reflection Last;
        public float Gain;
    }

    // Keyed by engine entity, then by the surface that answers for it.
    private readonly Dictionary<int, Dictionary<int, Echo>> _voices = new();
    private int _nextEcho;

    /// <summary>The id and seed of an engine's echo by its number, given by the host, so an echo shares an id
    /// with nothing else.</summary>
    public Func<int, int, (int Id, int Seed)> VoiceOf { get; set; } = (_, _) => throw new InvalidOperationException("The echoes have no ids: their host gives them (VoiceOf).");

    /// <summary>
    /// Rebuilds the reflecting surfaces if the static geometry changed, judged by the entity count and then
    /// the number of solid boxes: right while no map has walls that move without changing the count.
    /// </summary>
    public void SyncGeometry(IAcousticWorld world) => SyncGeometry(world, null);

    /// <summary>
    /// With the scene's acoustic triangle world when there is one (SpatialAcoustics.ReflectionWorldFor): a
    /// leg of a mirrored path is then asked of its tree instead of every solid box.
    /// </summary>
    public void SyncGeometry(IAcousticWorld world, OpenFPS.Geometry.TriangleWorld? acoustic)
    {
        _world = acoustic;
        if (!PrecomputeInBackground)
        {
            // A test or replay, where the geometry is in place before the frame that uses it.
            if (world.BodyCount == _lastEntityCount) return;
            Apply(BuildGeometry(world, world.BodyCount, _builtFrom));
            return;
        }
        // The geometry is built off this thread: 6,300 boxes make a face list and a tree in 18-37 ms,
        // and the city's body count changes while it arrives, so it was built again and again in line.
        if (_building is { IsCompleted: true } done)
        {
            _building = null;
            if (done.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) Apply(done.Result);
        }
        if (_building != null) return;
        // The cheap test first: BoxesFromWorld walks every entity and allocates, sixty times a second.
        if (world.BodyCount == _lastEntityCount) return;
        int bodyCount = world.BodyCount;
        int builtFrom = _builtFrom;
        _building = BackgroundWork.Run(Work, "EngineReflections geometry", () => BuildGeometry(world, bodyCount, builtFrom));
    }

    private void Apply(GeometryBuild g)
    {
        _lastEntityCount = g.BodyCount;
        if (g.Surfaces == null) return;
        _surfaces = g.Surfaces;
        _faceNodes = g.Nodes;
        _faceOrder = g.Order ?? Array.Empty<int>();
        _builtFrom = g.BuiltFrom;
        _boxes = g.Boxes ?? _boxes;
    }

    private sealed record GeometryBuild(List<ReflectingSurface>? Surfaces, OpenFPS.Geometry.BvhNode[]? Nodes,
                                        int[]? Order, List<OpenFPS.Audio.SteamAudio.SteamAudioScene.Box>? Boxes,
                                        int BuiltFrom, int BodyCount);

    private System.Threading.Tasks.Task<GeometryBuild>? _building;

    /// <summary>The faces of a world and their tree, made on a worker thread. Null surfaces: the box count
    /// did not change, so the geometry in use is still the world's.</summary>
    private static GeometryBuild BuildGeometry(IAcousticWorld world, int bodyCount, int builtFrom)
    {
        OpenFPS.Audio.Core.PlacementThread.Heavy("The engines' reflections' geometry");
        var boxes = OpenFPS.Audio.SteamAudio.SteamAudioScene.BoxesFromWorld(world);
        if (boxes.Count == builtFrom) return new GeometryBuild(null, null, null, boxes, builtFrom, bodyCount);

        var surfaces = new List<ReflectingSurface>();
        Span<ReflectingSurface> six = stackalloc ReflectingSurface[6];
        var shaped = new List<ReflectingSurface>();
        int id = 1;
        foreach (var b in boxes)
        {
            var props = AcousticRegistry.GetProperties(b.Material);
            // A shape mirrors from its facets, a box from its six faces.
            var shape = OpenFPS.Geometry.Shapes.Make(b.Form, b.Size);
            shaped.Clear();
            int n = shape != null
                ? ImageSource.FacesOfShape(shape.Outer, b.Center, b.Rotation, props.Absorption, id, shaped, props.Scattering)
                : ImageSource.FacesOfBox(b.Center, b.Size, b.Rotation, props.Absorption, id, six, props.Scattering);
            id += Math.Max(6, n);
            for (int i = 0; i < n; i++)
            {
                var f = shape != null ? shaped[i] : six[i];
                if (f.HalfU.Length() < MinFaceHalfExtent && f.HalfV.Length() < MinFaceHalfExtent) continue;
                // Keyed by the plane, not the box: a long wall is a row of blocks (the speedway's
                // grandstand is twelve), and keyed per box the bounce crossing a seam tore the voice
                // down and started another several times a second per car.
                surfaces.Add(f with { SurfaceId = PlaneId(f) });
            }
        }
        BuildFaceTree(surfaces, out var nodes, out var order);
        return new GeometryBuild(surfaces, nodes, order, boxes, boxes.Count, bodyCount);
    }

    // ── The faces in a tree ─────────────────────────────────────────────────────────────────────
    //
    // A search mirrors through the faces within SurfaceSearchRadius of the path's middle. The tree hands
    // back only those to measure, rather than every face on the map (six a box, 130,000 on Magnolia).
    private OpenFPS.Geometry.BvhNode[]? _faceNodes;
    private int[] _faceOrder = Array.Empty<int>();
    private readonly List<int> _faceHits = new();

    private static void BuildFaceTree(List<ReflectingSurface> surfaces, out OpenFPS.Geometry.BvhNode[]? nodes, out int[] order)
    {
        if (surfaces.Count == 0) { nodes = null; order = Array.Empty<int>(); return; }
        var lo = new Vector3[surfaces.Count]; var hi = new Vector3[surfaces.Count];
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            var e = Vector3.Abs(s.HalfU) + Vector3.Abs(s.HalfV);
            lo[i] = s.Centre - e; hi[i] = s.Centre + e;
        }
        nodes = OpenFPS.Geometry.BvhBuilder.Build(lo, hi, surfaces.Count, 4, out order);
    }

    /// <summary>In the order the list holds them.</summary>
    private static void FacesNear(OpenFPS.Geometry.BvhNode[]? nodes, int[] order, Vector3 at, float radius, List<int> into)
    {
        into.Clear();
        if (nodes == null) return;
        var min = at - new Vector3(radius); var max = at + new Vector3(radius);
        Span<int> stack = stackalloc int[OpenFPS.Geometry.BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref nodes[stack[--sp]];
            if (n.Max.X < min.X || n.Min.X > max.X || n.Max.Y < min.Y || n.Min.Y > max.Y || n.Max.Z < min.Z || n.Min.Z > max.Z) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++) into.Add(order[i]);
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
        into.Sort();
    }

    /// <summary>
    /// A stable id for the infinite plane a face lies in: its normal and its distance from the
    /// origin, both quantised so that coplanar faces of neighbouring blocks agree.
    /// </summary>
    private static int PlaneId(in ReflectingSurface f)
    {
        // A tenth on the normal and a tenth of a metre on the offset: finer than any wall is crooked,
        // coarser than floating-point noise between two boxes built the same way.
        int nx = (int)MathF.Round(f.Normal.X * 10f);
        int ny = (int)MathF.Round(f.Normal.Y * 10f);
        int nz = (int)MathF.Round(f.Normal.Z * 10f);
        int d = (int)MathF.Round(Vector3.Dot(f.Normal, f.Centre) * 10f);
        unchecked
        {
            int h = 17;
            h = h * 31 + nx; h = h * 31 + ny; h = h * 31 + nz; h = h * 31 + d;
            return h == 0 ? 1 : h;
        }
    }

    /// <summary>Reflection voices alive now, across every car. Diagnostic.</summary>
    public int VoiceCount { get { int n = 0; foreach (var m in _voices.Values) n += m.Count; return n; } }

    /// <summary>Reflecting faces in the current geometry. Diagnostic.</summary>
    public int SurfaceCount => _surfaces.Count;

    /// <summary>
    /// Brings one engine's echo voices up to date. They copy <paramref name="direct"/>, the engine's own
    /// emitter, so they attenuate the same way, with only the surface's share in their volume. With
    /// <paramref name="traced"/> (AudioEngineFacade.HasTracedEchoes) the mirror images fade out: the trace
    /// has them and every order past them.
    /// </summary>
    public void Update(int entityId, in SpatialEmitter direct, in AcousticPathData directPath,
                       Vector3 listener, float speedOfSound, float dt, AudioEngineFacade audio, bool traced = false)
    {
        if (_surfaces.Count == 0) return;
        if (!_voices.TryGetValue(entityId, out var mine)) _voices[entityId] = mine = new Dictionary<int, Echo>();
        foreach (var e in mine.Values) e.Seen = false;

        Vector3 source = direct.Position;
        float directDist = MathF.Max(1f, Vector3.Distance(source, listener));

        int want = traced ? 0 : Math.Clamp(EchoesPerEngine, 0, MaxEchoesPerEngine);
        if (want == 0 && mine.Count == 0) return;
        Span<Reflection> found = stackalloc Reflection[MaxEchoesPerEngine];
        int n;
        if (PrecomputeInBackground && want > 0)
        {
            _wanted[entityId] = new Want(source, listener, speedOfSound, want);
            if (_found.TryGetValue(entityId, out var cached))
            {
                n = Math.Min(want, cached.Length);
                for (int i = 0; i < n; i++) found[i] = cached[i];
            }
            else n = 0;
        }
        else n = want == 0 ? 0 : Math.Min(want, FirstOrderNear(source, listener, speedOfSound, found, nearestPart: false));

        for (int i = 0; i < n; i++)
        {
            var r = found[i];
            // FMOD attenuates the mirrored position, so the volume carries only what the surface did:
            // the distance term is undone here and reapplied by the placement.
            float gain = Math.Clamp(r.Gain * r.PathLength / directDist, 0f, 1f);
            if (gain < ImageSource.MinGain) continue;

            // Asked of the reflection, not the car: the source's level, times what the surface kept, over
            // the path it took. It falls out of the materials and the geometry, on any map.
            float audibility = direct.Volume * gain * MathF.Max(0.1f, direct.MinDistance) / MathF.Max(1f, r.PathLength);
            _audibilities.Add(audibility);
            if (audibility < _audibilityFloor) continue;

            if (!mine.TryGetValue(r.SurfaceId, out var echo))
            {
                echo = new Echo { Number = _nextEcho++ };
                mine[r.SurfaceId] = echo;
            }
            // Asked whether it plays, not remembered: on the car's first frame the engine's voice (whose
            // ring the echo reads) is made later in the tick, the start fails, and the next frame retries.
            var (id, seed) = VoiceOf(entityId, echo.Number);
            var e = Make(id, seed, entityId, direct, r, gain);
            CarryPath(ref e, directPath, r, listener, directDist);
            Voiced?.Invoke(id, entityId);
            audio.Sound(e);
            echo.Seen = true;
            echo.Silent = 0f;
            echo.Last = r;
            echo.Gain = gain;
        }

        // Walls that stopped answering fade to zero and only then stop: cut at their last gain, they
        // clicked every time a car passed the end of a wall.
        List<int>? drop = null;
        foreach (var kv in mine)
        {
            var echo = kv.Value;
            if (echo.Seen) continue;
            echo.Silent += dt;
            var (id, seed) = VoiceOf(entityId, echo.Number);
            if (echo.Silent >= ReleaseSeconds)
            {
                audio.StopSound(id);
                (drop ??= new List<int>()).Add(kv.Key);
                continue;
            }
            // Only the level goes: moving a dying echo would Doppler it into a whistle.
            float fade = 1f - echo.Silent / ReleaseSeconds;
            var dying = Make(id, seed, entityId, direct, echo.Last, echo.Gain * fade);
            CarryPath(ref dying, directPath, echo.Last, listener, directDist);
            Voiced?.Invoke(id, entityId);
            audio.UpdateSpatialAttributes(dying);
        }
        if (drop != null) foreach (int k in drop) mine.Remove(k);
    }

    /// <summary>
    /// Stops this engine's echoes, when the car goes. Cut, not faded: an echo follows its engine's own
    /// fade a fraction of a second later, and stopping them together keeps the tail from outliving the car.
    /// </summary>
    public void Forget(int entityId, AudioEngineFacade audio)
    {
        if (!_voices.TryGetValue(entityId, out var mine)) return;
        foreach (var e in mine.Values) audio.StopSound(VoiceOf(entityId, e.Number).Id);
        _voices.Remove(entityId);
    }

    /// <summary>
    /// The reflections the world offers a sound at this point, obstruction-tested, for any source (the
    /// grandstand crowd, a door, a rifle). Use this rather than a search of one's own: the transient path
    /// once skipped the leg test, and its echoes kept sounding off walls the source could not see.
    /// </summary>
    public int FindReflections(Vector3 source, Vector3 listener, float speedOfSound, Span<Reflection> into,
                               int diffuseTaps = 0)
        => FirstOrderNear(source, listener, speedOfSound, into, diffuseTaps, nearestPart: true);

    /// <summary>
    /// Squared, to the face's nearest part, not its centre: the city's ground is one box a kilometre
    /// across, and from its centre the ground under anyone 260 m out was never considered, nor the far
    /// half of any long facade.
    /// </summary>
    internal static float DistanceSquaredToFace(in ReflectingSurface s, Vector3 p)
    {
        Vector3 rel = p - s.Centre;
        float hu = s.HalfU.Length(), hv = s.HalfV.Length();
        Vector3 u = hu > 1e-6f ? s.HalfU / hu : Vector3.Zero, v = hv > 1e-6f ? s.HalfV / hv : Vector3.Zero;
        float du = Vector3.Dot(rel, u), dv = Vector3.Dot(rel, v);
        Vector3 nearest = s.Centre + u * Math.Clamp(du, -hu, hu) + v * Math.Clamp(dv, -hv, hv);
        return Vector3.DistanceSquared(nearest, p);
    }

    /// <summary>
    /// The first-order reflections through the faces near the path: found by their nearest part with
    /// <paramref name="nearestPart"/> (one-off sounds), by their centre otherwise (engines). Engines keep
    /// the centre until their echoes stop phasing: counted along their whole length, long facades gave
    /// every passing car coherent echoes, heard as cars passing "inside out".
    /// </summary>
    private int FirstOrderNear(Vector3 source, Vector3 listener, float speedOfSound, Span<Reflection> into,
                               int diffuseTaps = 0, bool nearestPart = true)
        => FirstOrderNear(_near, _faceHits, _surfaces, _faceNodes, _faceOrder, _boxes, _world,
                          source, listener, speedOfSound, into, diffuseTaps, nearestPart);

    private static int FirstOrderNear(List<ReflectingSurface> near, List<int> hits,
                                      List<ReflectingSurface> surfaces, OpenFPS.Geometry.BvhNode[]? nodes, int[] order,
                                      List<OpenFPS.Audio.SteamAudio.SteamAudioScene.Box> boxes,
                                      OpenFPS.Geometry.TriangleWorld? world,
                                      Vector3 source, Vector3 listener, float speedOfSound, Span<Reflection> into,
                                      int diffuseTaps = 0, bool nearestPart = true)
    {
        // Judged from the midpoint, near where a specular bounce between the two has to land.
        Vector3 mid = (source + listener) * 0.5f;
        float r2 = SurfaceSearchRadius * SurfaceSearchRadius;
        near.Clear();
        FacesNear(nodes, order, mid, SurfaceSearchRadius, hits);
        foreach (int i in hits)
        {
            var s = surfaces[i];
            if ((nearestPart ? DistanceSquaredToFace(s, mid) : Vector3.DistanceSquared(s.Centre, mid)) <= r2) near.Add(s);
        }
        return ImageSource.FirstOrder(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(near),
                                      source, listener, speedOfSound, into, (a, b) => Blocked(boxes, world, a, b), diffuseTaps);
    }

    private readonly List<ReflectingSurface> _near = new();

    /// <summary>Legs are asked of it when there is one.</summary>
    private OpenFPS.Geometry.TriangleWorld? _world;

    /// <summary>Whether a leg starts inside a solid, as the box test counted a segment wholly inside a box
    /// as crossing it.</summary>
    private static bool Inside(OpenFPS.Geometry.TriangleWorld world, Vector3 p)
    {
        var inside = _inside ??= new List<OpenFPS.Geometry.SolidRef>(4);
        inside.Clear();
        var all = new OpenFPS.Geometry.AcceptAll();
        world.Containing(p, OpenFPS.Geometry.GeometryLayers.Acoustics, ref all, inside);
        return inside.Count > 0;
    }

    [ThreadStatic] private static List<OpenFPS.Geometry.SolidRef>? _inside;

    /// <summary>
    /// Whether anything stands in this leg of a mirrored path. Untested, a reflection sounds off a wall
    /// the car can no longer see ("the cars disappear and I hear the ghost of their reflections"). The
    /// ends are pulled in: a bounce point lies on a surface and would block its own leg.
    /// </summary>
    private static bool Blocked(List<OpenFPS.Audio.SteamAudio.SteamAudioScene.Box> boxes,
                                OpenFPS.Geometry.TriangleWorld? world, Vector3 a, Vector3 b)
    {
        if (boxes.Count == 0) return false;
        Vector3 d = b - a;
        float len = d.Length();
        if (len < 1e-3f) return false;
        Vector3 unit = d / len;
        const float Skin = 0.05f;
        if (world is { } geometry)
        {
            if (len <= 2f * Skin) return false;
            var all = new OpenFPS.Geometry.AcceptAll();
            return geometry.Any(a + unit * Skin, unit, len - 2f * Skin, OpenFPS.Geometry.GeometryLayers.Acoustics,
                                OpenFPS.Geometry.RayFaces.Both, ref all)
                   || Inside(geometry, a + unit * Skin);
        }
        Vector3 from = a + unit * Skin, to = b - unit * Skin;
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (GeometryUtils.LineIntersectsOBB(from, to, box.Center, box.Size, box.Rotation)) return true;
        }
        return false;
    }

    /// <summary>
    /// Gives an echo its acoustic path, in its own emitter, its one writer: the worker never sees echo ids,
    /// so without one they were unoccluded and full-bandwidth. Distance, delay and air are the echo's own:
    /// with the car's air, an echo 120 m round a facade was brighter than the car 40 m away, and brightness
    /// is how the ear judges nearness.
    /// </summary>
    private static void CarryPath(ref SpatialEmitter e, in AcousticPathData directPath, in Reflection r,
                                  Vector3 listener, float directDist)
    {
        e.ApparentPosition = r.ApparentPosition;
        e.EffectiveDistance = MathF.Max(r.PathLength, Vector3.Distance(listener, r.ApparentPosition));
        (e.AirLowDb, e.AirMidDb, e.AirHighDb) = EchoAir(directPath, directDist, r.PathLength);
        // Not the direct path's blocking or its edge: both legs were found clear (Blocked), and copying the
        // direct path's occlusion muffled the echo a second time.
        e.Occlusion = 0f;
        e.EqLow = e.EqMid = e.EqHigh = 1f;
        e.TransmissionBleed = 0f;
        e.ApertureFactor = 1f;
        e.TargetRegionId = directPath.RegionId;
        e.RoomGain = directPath.RoomGain;
        e.CarriesPath = true;
    }

    /// <summary>Told of every echo voice placed (its id, its engine's), so the host can know whose it is.</summary>
    public Action<int, int>? Voiced { get; set; }

    /// <summary>
    /// The air over the echo's path, per band: ISO 9613-1 is proportional to distance, so the direct
    /// path's per metre times the echo's length; the standard atmosphere when the direct path is too short.
    /// </summary>
    internal static (float Low, float Mid, float High) EchoAir(in AcousticPathData direct, float directDist, float pathLength)
    {
        if (directDist < 1f)
            return OpenFPS.Sound.Core.AudioPhysics.AirLossDb(pathLength, 0.5f, 20f, 1013.25f);
        float k = MathF.Max(0f, pathLength) / directDist;
        return (direct.AirLowDb * k, direct.AirMidDb * k, direct.AirHighDb * k);
    }

    private static SpatialEmitter Make(int voiceId, int seed, int engineId, in SpatialEmitter direct,
                                       in Reflection r, float gain) => new()
    {
        EntityId = voiceId,
        Seed = seed,
        SoundId = "engine-echo",
        IsSynth = true,
        EchoOfEntity = engineId,
        EchoDelaySeconds = r.DelaySeconds,
        EchoGain = gain,
        EchoScattering = r.Scattering,
        EngineKey = "",
        Mode = PlaybackMode.LoopOne,
        Type = EmitterType.WorldLocked,
        Position = r.ApparentPosition,
        ApparentPosition = r.ApparentPosition,
        // The mirrored source is not moving on its own; the glide comes from the delay slewing.
        Velocity = Vector3.Zero,
        Volume = direct.Volume,
        Range = direct.Range,
        MinDistance = direct.MinDistance,
        Pitch = 1f,
        // Never sent to the reverb (the provider skips reflections): that would count the room twice.
        IsReflection = true,
        TargetRegionId = direct.TargetRegionId,
    };
}
