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
    /// <summary>An axis-box collider in world space (size = full extents) with an acoustic material name.</summary>
    public readonly record struct Box(Vector3 Center, Vector3 Size, Quaternion Rotation, string Material);

    private readonly IntPtr _context;
    private IntPtr _scene;
    private IntPtr _mesh;

    public IntPtr Handle => _scene;
    public bool IsBuilt => _scene != IntPtr.Zero;

    /// <summary>World-space AABB of all built geometry (valid after <see cref="Build"/>). Used to size the
    /// probe volume for pathing. Zero-sized when the scene is empty.</summary>
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

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
            boxes.Add(new Box(snap.Transform.Position, size, snap.Transform.Rotation, def.Material.Material));
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
        {
            bool covered = false;
            foreach (var (omin, omax) in all)
            {
                if (omin.Y < top + Headroom) continue;
                if (p.X >= omin.X && p.X <= omax.X && p.Y >= omin.Z && p.Y <= omax.Z) { covered = true; break; }
            }
            if (!covered) open++;
        }
        return open >= 3;
    }

    private static (Vector3 Min, Vector3 Max) WorldExtents(in Box b)
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
        if (Phonon.iplSceneCreate(_context, ref Defaults.SceneSettings, out _scene) != Phonon.IPL_STATUS_SUCCESS)
        { _scene = IntPtr.Zero; return; }

        var verts = new List<PV>();
        var tris = new List<Phonon.IPLTriangle>();
        var triMat = new List<int>();
        var materials = new List<Phonon.IPLMaterial>();
        var matIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            int mi = MaterialIndex(b.Material, materials, matIndexByName);
            AppendBox(b, verts, tris, triMat, mi);
        }
        if (tris.Count == 0) { Phonon.iplSceneCommit(_scene); return; }


        var vArr = verts.ToArray();
        var tArr = tris.ToArray();
        var miArr = triMat.ToArray();
        var mArr = materials.ToArray();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in vArr)
        {
            min = Vector3.Min(min, new Vector3(v.x, v.y, v.z));
            max = Vector3.Max(max, new Vector3(v.x, v.y, v.z));
        }
        BoundsMin = min; BoundsMax = max;

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
            if (Phonon.iplStaticMeshCreate(_scene, ref meshS, out _mesh) == Phonon.IPL_STATUS_SUCCESS)
                Phonon.iplStaticMeshAdd(_mesh, _scene);
        }
        finally { hV.Free(); hT.Free(); hMI.Free(); hM.Free(); }

        Phonon.iplSceneCommit(_scene);
    }

    private static int MaterialIndex(string name, List<Phonon.IPLMaterial> materials, Dictionary<string, int> byName)
    {
        if (byName.TryGetValue(name ?? "Generic", out int idx)) return idx;
        var p = AcousticRegistry.GetProperties(string.IsNullOrEmpty(name) ? "Generic" : name);
        materials.Add(new Phonon.IPLMaterial
        {
            absLow = p.AbsorptionLow, absMid = p.AbsorptionMid, absHigh = p.AbsorptionHigh,
            scattering = p.Scattering,
            transLow = p.TransmissionLow, transMid = p.TransmissionMid, transHigh = p.TransmissionHigh,
        });
        idx = materials.Count - 1;
        byName[name ?? "Generic"] = idx;
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
            verts.Add(new PV { x = world.X, y = world.Y, z = world.Z });
        }
        for (int f = 0; f < _faceIdx.Length; f += 3)
        {
            tris.Add(new Phonon.IPLTriangle { i0 = baseIdx + _faceIdx[f], i1 = baseIdx + _faceIdx[f + 1], i2 = baseIdx + _faceIdx[f + 2] });
            triMat.Add(mi);
        }
    }

    private void Release()
    {
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
