using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Builds and owns a Steam Audio <c>IPLScene</c> from the game's solid box colliders, so the simulator
/// can ray-trace occlusion / transmission / reflections / pathing against real world geometry. Each box
/// becomes 8 vertices + 12 triangles with an acoustic material derived from <see cref="AcousticRegistry"/>.
/// Rebuild on map load; the scene is reused across simulation frames.
/// </summary>
public sealed class SteamAudioScene : IDisposable
{
    /// <summary>An axis-box collider in world space (size = full extents) with an acoustic material name,
    /// and how it is built (solid, or two leaves over a cavity: <see cref="WallBuild"/>). <paramref name="EntityId"/>
    /// is the entity it was taken from, 0 for one made by hand.</summary>
    public readonly record struct Box(Vector3 Center, Vector3 Size, Quaternion Rotation, string Material,
                                      WallBuild Build = default, int EntityId = 0);

    private readonly IntPtr _context;
    private IntPtr _scene;
    private IntPtr _mesh;
    /// <summary>The handle is someone else's: see <see cref="Borrowed"/>.</summary>
    private bool _borrowed;

    // ── Which ray tracer a context's scenes use ─────────────────────────────────────────────────
    //
    // Embree where it starts (docs/GEOMETRY.md 2.4 and 6.4: builds in a quarter of the time, traces
    // faster, and is the only tracer that handles a scene made of instanced tile sub-scenes), the default
    // where it does not. Per context, so a lab instrument with a context of its own keeps the default. A
    // simulator must be made for the type of the scenes it will be given (SteamAudioSimulator,
    // TracedReverb, TracedEchoes, LateField ask TypeFor).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IntPtr> EmbreeDevices = new();

    /// <summary>Makes this context's scenes Embree scenes. False, and the default tracer stays, if Embree
    /// does not start here (an ARM machine: Steam Audio carries Embree for x86 and x64 only).</summary>
    public static bool UseEmbree(IntPtr context)
    {
        if (EmbreeDevices.ContainsKey(context)) return true;
        try
        {
            var settings = new Phonon.IPLEmbreeDeviceSettings();
            if (Phonon.iplEmbreeDeviceCreate(context, ref settings, out IntPtr device) != Phonon.IPL_STATUS_SUCCESS || device == IntPtr.Zero)
                return false;
            EmbreeDevices[context] = device;
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    /// <summary>The scene type of a context's scenes.</summary>
    public static int TypeFor(IntPtr context)
        => EmbreeDevices.ContainsKey(context) ? Phonon.IPL_SCENETYPE_EMBREE : Phonon.IPL_SCENETYPE_DEFAULT;

    /// <summary>A new, empty scene of the context's type, or zero.</summary>
    internal static IntPtr CreateScene(IntPtr context)
    {
        var settings = new Phonon.IPLSceneSettings { type = TypeFor(context) };
        if (EmbreeDevices.TryGetValue(context, out var device)) settings.embreeDevice = device;
        return Phonon.iplSceneCreate(context, ref settings, out IntPtr scene) == Phonon.IPL_STATUS_SUCCESS ? scene : IntPtr.Zero;
    }

    /// <summary>
    /// A scene whose handle belongs to someone else (TileSceneSet's top scenes, used in turn): this
    /// object carries it to the simulators and the reflection search, and never releases it.
    /// </summary>
    internal static SteamAudioScene Borrowed(IntPtr context, IntPtr scene) => new(context) { _scene = scene, _borrowed = true };

    /// <summary>The boxes a borrowed scene now holds, for <see cref="Solids"/> and the bounds.</summary>
    internal void SetGeometry(IReadOnlyList<Box> boxes)
    {
        var solids = new List<EarlyReflections.Solid>(boxes.Count);
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            solids.Add(new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material));
            var (lo, hi) = WorldExtents(b);
            min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
        }
        Solids = solids;
        if (solids.Count > 0) { BoundsMin = min; BoundsMax = max; }
    }

    public IntPtr Handle => _scene;
    public bool IsBuilt => _scene != IntPtr.Zero;

    /// <summary>World-space AABB of all built geometry in the game's frame (valid after <see cref="Build"/>).
    /// Used to size the probe volume for pathing. Zero-sized when the scene is empty.</summary>
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    /// <summary>The boxes the scene was built from, as the reflection search takes them
    /// (EarlyReflections): the listener's trace works out from them which of its early energy the
    /// placed copies carry (EarlyCopies). Empty before <see cref="Build"/>.</summary>
    public IReadOnlyList<EarlyReflections.Solid> Solids { get; private set; } = Array.Empty<EarlyReflections.Solid>();

    public SteamAudioScene(IntPtr context) => _context = context;

    /// <summary>
    /// Extracts the solid box colliders from a <see cref="WorldSnapshot"/> as scene geometry — each solid
    /// box entity becomes one <see cref="Box"/> at its live transform, with the acoustic material taken
    /// from its <c>MaterialComponent</c>. Phase 4b uses boxes only (the simulator scene is built from
    /// boxes); non-box solids are skipped. Pure/static so the acoustic worker and the headless spikes
    /// share one definition of "what counts as audio geometry".
    /// </summary>
    public static List<Box> BoxesFromWorld(WorldSnapshot world)
    {
        var boxes = new List<Box>();
        if (world == null) return boxes;
        foreach (var snap in world.Entities.Values)
        {
            var def = snap.Definition;
            if (def == null || !def.Collider.IsSolid || def.Collider.Shape != ColliderShape.Box) continue;
            // A sound SOURCE must not be part of the occluding geometry, or its own collider sits at its
            // emission point and occludes itself (a solid beacon goes permanently silent). Beacons/NPCs/
            // machines that emit sound are excluded from the acoustic mesh; they are small relative to
            // walls, so losing their occlusion of OTHER sources is negligible.
            if (!string.IsNullOrEmpty(def.SoundEmitter.SoundId)) continue;
            // Nor anything that MOVES. The scene is built once, so a car's panels would stay where
            // the car was parked — a ghost of glass and steel in the bay — after it drove away.
            if (def.Moves) continue;
            var size = def.Collider.Size;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) continue;
            boxes.Add(new Box(snap.Transform.Position, size, snap.Transform.Rotation, def.Material.Material,
                              new WallBuild(def.Acoustics.LeafMetres, def.Acoustics.StudSpacingMetres), snap.Id));
        }
        return boxes;
    }

    /// <summary>
    /// The scene without its open ground: every thin, flat slab at ground level with open sky over
    /// it — road, pavement, lawn, a plaza, the ground itself. Floors with a ceiling over them stay,
    /// and so does everything higher up (a roof is a ceiling); they are part of a room.
    ///
    /// For the trace taken from the LISTENER's position (TracedReverb). That trace plays every sound
    /// as if it came from where the listener stands, so its ground bounce is the one from their own
    /// head to the floor and back — ten milliseconds, at a level set by nothing about the source —
    /// plus the convolution's own block of delay. Every voice came back a moment later off the ground
    /// under your feet, and a clean voice close by with a copy of itself 10-15 ms behind is a small
    /// room: "they sound like they're in a room when they aren't" (measured from the capture,
    /// 2026-09-27: -2 to -7 dB at 9-15 ms behind the direct voice). A source's own ground reflection,
    /// at its own geometry's delay, is modelled with the source (engines' GroundReflection, the ground
    /// wash after a shot); the ground's share of a place's tail is small, and the facades carry the
    /// street's.
    /// </summary>
    public static List<Box> WithoutOpenGround(IReadOnlyList<Box> boxes)
    {
        var ext = new (Vector3 Min, Vector3 Max)[boxes.Count];
        for (int i = 0; i < boxes.Count; i++) ext[i] = WorldExtents(boxes[i]);
        var kept = new List<Box>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
            if (!IsOpenGround(boxes[i], ext[i], ext)) kept.Add(boxes[i]);
        return kept;
    }

    /// <summary>How thin a slab is to count as a floor rather than a block, metres.</summary>
    private const float SlabThickness = 0.5f;
    /// <summary>Anything this far over a slab's top covers it; less is laid on it (a road on the ground).</summary>
    private const float Headroom = 1.5f;
    /// <summary>
    /// The highest a slab's top may be and still be the ground, metres. A roof is a thin slab with the
    /// sky over it too, and it is the ceiling of the room under it: left out, a top-floor room would
    /// lose its own ceiling from its sound.
    /// </summary>
    private const float GroundLevel = 1.0f;

    internal static bool IsOpenGround(in Box b, (Vector3 Min, Vector3 Max) own, (Vector3 Min, Vector3 Max)[] all)
        => IsOpenGround(b, own, (x, z, above) =>
        {
            foreach (var (omin, omax) in all)
            {
                if (omin.Y < above) continue;
                if (x >= omin.X && x <= omax.X && z >= omin.Z && z <= omax.Z) return true;
            }
            return false;
        });

    /// <summary>Anything this high or higher could stand over a slab of open ground (its top at most
    /// <see cref="GroundLevel"/>, cover at least <see cref="Headroom"/> over that).</summary>
    internal const float LowestCover = Headroom;

    /// <summary>The same test, asking <paramref name="coveredAt"/>(x, z, lowest) whether anything whose
    /// underside is at least <c>lowest</c> stands over the point (x, z).</summary>
    internal static bool IsOpenGround(in Box b, (Vector3 Min, Vector3 Max) own, Func<float, float, float, bool> coveredAt)
    {
        if (b.Size.Y > SlabThickness || b.Size.X < 1f || b.Size.Z < 1f) return false;
        // Turned about anything but the vertical, it is not a floor.
        var up = Vector3.Transform(Vector3.UnitY, b.Rotation);
        if (MathF.Abs(up.Y) < 0.99f) return false;
        var (min, max) = own;
        float top = max.Y;
        if (top > GroundLevel) return false;
        // Open to the sky over most of it: the middle and four points halfway to the corners.
        var c = (min + max) * 0.5f;
        var q = (max - min) * 0.25f;
        Span<Vector2> pts = stackalloc Vector2[]
        {
            new(c.X, c.Z), new(c.X - q.X, c.Z - q.Z), new(c.X + q.X, c.Z - q.Z),
            new(c.X - q.X, c.Z + q.Z), new(c.X + q.X, c.Z + q.Z),
        };
        int open = 0;
        foreach (var p in pts)
            if (!coveredAt(p.X, p.Y, top + Headroom)) open++;
        return open >= 3;
    }

    internal static (Vector3 Min, Vector3 Max) WorldExtents(in Box b)
    {
        var h = b.Size * 0.5f;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
            var w = b.Center + Vector3.Transform(corner, b.Rotation);
            min = Vector3.Min(min, w); max = Vector3.Max(max, w);
        }
        return (min, max);
    }

    public void Build(IReadOnlyList<Box> boxes)
    {
        Release();
        _scene = CreateScene(_context);
        if (_scene == IntPtr.Zero) return;

        var solids = new List<EarlyReflections.Solid>(boxes.Count);
        foreach (var b in boxes)
            if (b.Size.X > 0 && b.Size.Y > 0 && b.Size.Z > 0) solids.Add(new EarlyReflections.Solid(b.Center, b.Size, b.Rotation, b.Material));
        Solids = solids;

        _mesh = AddMesh(_context, _scene, boxes, out var bmin, out var bmax);
        if (_mesh != IntPtr.Zero) { BoundsMin = bmin; BoundsMax = bmax; }
        Phonon.iplSceneCommit(_scene);
    }

    /// <summary>
    /// The boxes as one static mesh, added to <paramref name="scene"/> (not committed); zero if there are
    /// none. Vertices in Steam Audio's frame. The bounds are in the game's.
    /// </summary>
    internal static IntPtr AddMesh(IntPtr context, IntPtr scene, IReadOnlyList<Box> boxes, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        boundsMin = boundsMax = Vector3.Zero;
        IntPtr mesh = IntPtr.Zero;
        var verts = new List<PV>();
        var tris = new List<Phonon.IPLTriangle>();
        var triMat = new List<int>();
        var materials = new List<Phonon.IPLMaterial>();
        var matIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            int mi = MaterialIndex(b, materials, matIndexByName);
            AppendBox(b, verts, tris, triMat, mi);
        }
        if (tris.Count == 0) return IntPtr.Zero;


        var vArr = verts.ToArray();
        var tArr = tris.ToArray();
        var miArr = triMat.ToArray();
        var mArr = materials.ToArray();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        // In the game's frame: the vertices are already in Steam Audio's, and the probe bake converts
        // these bounds itself (SteamAudioSimulator.BuildOrRebakeProbes).
        foreach (var v in vArr)
        {
            var g = new Vector3(v.x, v.y, Phonon.WorldZ(v.z));
            min = Vector3.Min(min, g);
            max = Vector3.Max(max, g);
        }
        boundsMin = min; boundsMax = max;

        var hV = GCHandle.Alloc(vArr, GCHandleType.Pinned);
        var hT = GCHandle.Alloc(tArr, GCHandleType.Pinned);
        var hMI = GCHandle.Alloc(miArr, GCHandleType.Pinned);
        var hM = GCHandle.Alloc(mArr, GCHandleType.Pinned);
        try
        {
            var meshS = new Phonon.IPLStaticMeshSettings
            {
                numVertices = vArr.Length, numTriangles = tArr.Length, numMaterials = mArr.Length,
                vertices = hV.AddrOfPinnedObject(), triangles = hT.AddrOfPinnedObject(),
                materialIndices = hMI.AddrOfPinnedObject(), materials = hM.AddrOfPinnedObject(),
            };
            if (Phonon.iplStaticMeshCreate(scene, ref meshS, out mesh) == Phonon.IPL_STATUS_SUCCESS)
                Phonon.iplStaticMeshAdd(mesh, scene);
            else mesh = IntPtr.Zero;
        }
        finally { hV.Free(); hT.Free(); hMI.Free(); hM.Free(); }
        return mesh;
    }

    /// <summary>Steam Audio's three band centres (phonon.h, IPLMaterial): what its ABSORPTION figures
    /// mean. Its transmission figures are the mixer's bands instead; see <see cref="MaterialIndex"/>.</summary>
    public static readonly (float Low, float Mid, float High) SteamAudioBandsHz = (400f, 2500f, 15000f);

    /// <summary>
    /// A material per (name, what the box lets through): what a wall lets through depends on how heavy
    /// and stiff it is and how it is built, not only on what it is made of
    /// (<see cref="WallTransmission.BandGains(string, Vector3, WallBuild)"/>, the model the hand-rolled
    /// tracer uses too).
    ///
    /// The transmission triple is the MIXER's three bands (<see cref="AcousticBands"/>), not Steam
    /// Audio's: the direct simulation only multiplies these figures along its rays, and the engine
    /// applies the products in the mixer's three-band EQ. Absorption is read by the reflection
    /// simulation at Steam Audio's own centres.
    ///
    /// Per FACE, and that is the power 2/3. Steam Audio's direct simulator casts its transmission
    /// rays alternately from the listener and the source, multiplies the transmission of every face
    /// they hit, and takes the square root of the product when there is more than one hit
    /// (core/src/core/direct_simulator.cpp). The loop stops when either ray finds nothing, so a
    /// single box is three hits, not four, and each face carries the box's transmission to the 2/3:
    /// one wall then loses exactly its own figure, in every band. n boxes in a row are 2n + 1 hits and
    /// lose (2n + 1)/3 of one each: two walls 5/3 of one (measured), not 2. (open-fps-patches 5.)
    /// </summary>
    private static int MaterialIndex(in Box b, List<Phonon.IPLMaterial> materials, Dictionary<string, int> byName)
    {
        string name = string.IsNullOrEmpty(b.Material) ? "Generic" : b.Material;
        var (tl, tm, th) = WallTransmission.BandGains(name, b.Size, b.Build);
        // Keyed on what it does, to a hundredth of a decibel: boxes that let the same through share one.
        static string Q(float g) => MathF.Round(20f * MathF.Log10(MathF.Max(1e-9f, g)), 2)
                                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
        string key = name + "@" + Q(tl) + "/" + Q(tm) + "/" + Q(th);
        if (byName.TryGetValue(key, out int idx)) return idx;
        var p = AcousticRegistry.GetProperties(name);
        float Abs(float hz) => AcousticRegistry.AtFrequency(p.AbsorptionLow, p.AbsorptionMid, p.AbsorptionHigh, hz);
        materials.Add(new Phonon.IPLMaterial
        {
            absLow = Abs(SteamAudioBandsHz.Low), absMid = Abs(SteamAudioBandsHz.Mid), absHigh = Abs(SteamAudioBandsHz.High),
            scattering = p.Scattering,
            transLow = MathF.Pow(tl, 2f / 3f), transMid = MathF.Pow(tm, 2f / 3f), transHigh = MathF.Pow(th, 2f / 3f),
        });
        idx = materials.Count - 1;
        byName[key] = idx;
        return idx;
    }

    // Box corner offsets (half-extents), then the 12 triangles (outward winding; occlusion ignores winding).
    private static readonly Vector3[] _corner =
    {
        new(-1,-1,-1), new(1,-1,-1), new(1,1,-1), new(-1,1,-1),
        new(-1,-1, 1), new(1,-1, 1), new(1,1, 1), new(-1,1, 1),
    };
    private static readonly int[] _faceIdx =
    {
        0,1,2, 0,2,3,   // -Z
        4,6,5, 4,7,6,   // +Z
        0,3,7, 0,7,4,   // -X
        1,5,6, 1,6,2,   // +X
        0,4,5, 0,5,1,   // -Y
        3,2,6, 3,6,7,   // +Y
    };

    private static void AppendBox(Box b, List<PV> verts, List<Phonon.IPLTriangle> tris, List<int> triMat, int mi)
    {
        int baseIdx = verts.Count;
        Vector3 half = b.Size * 0.5f;
        for (int c = 0; c < 8; c++)
        {
            Vector3 local = _corner[c] * half;
            Vector3 world = b.Center + Vector3.Transform(local, b.Rotation);
            verts.Add(Phonon.World(world));   // Steam Audio's z runs the other way: see Phonon.World
        }
        for (int f = 0; f < _faceIdx.Length; f += 3)
        {
            tris.Add(new Phonon.IPLTriangle { i0 = baseIdx + _faceIdx[f], i1 = baseIdx + _faceIdx[f + 1], i2 = baseIdx + _faceIdx[f + 2] });
            triMat.Add(mi);
        }
    }

    private void Release()
    {
        if (_borrowed) return;
        if (_mesh != IntPtr.Zero) Phonon.iplStaticMeshRelease(ref _mesh);
        if (_scene != IntPtr.Zero) Phonon.iplSceneRelease(ref _scene);
        _mesh = IntPtr.Zero; _scene = IntPtr.Zero;
    }

    public void Dispose() => Release();

    private static class Defaults
    {
        public static Phonon.IPLSceneSettings SceneSettings = new() { type = Phonon.IPL_SCENETYPE_DEFAULT };
    }
}
