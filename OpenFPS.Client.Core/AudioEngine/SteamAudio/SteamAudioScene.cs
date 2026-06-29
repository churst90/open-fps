using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenFPS.Common;
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

    public SteamAudioScene(IntPtr context) => _context = context;

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
