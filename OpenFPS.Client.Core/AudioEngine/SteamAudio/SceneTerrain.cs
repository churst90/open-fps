using System.Numerics;
using OpenFPS.Common.Geometry;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The ground in the acoustic scene (docs/GEOMETRY.md 2.3 and stage 3): each tile of terrain as triangles
/// of the open ground, two to a cell, with its cells' materials, and the ground's height for deciding what
/// else lies on it.
/// </summary>
public static class SceneTerrain
{
    /// <summary>What the ground is to sound: the acoustic layer, and open ground, left out of the listener's trace.</summary>
    public static readonly Surface Surface = new("Dirt", default, GeometryLayers.Acoustics, SurfaceFlags.OpenGround);

    /// <summary>A tile's triangles appended to a mesh being made, in the frame whose origin is
    /// <paramref name="origin"/> (the world's for a whole scene, a tile's corner for its sub-scene), wound for
    /// Steam Audio's mirrored frame as the boxes are.</summary>
    internal static void Append(in SolidSpec spec, Vector3 origin, List<PV> verts, List<Phonon.IPLTriangle> tris, List<int> triMat,
                                Func<string, Vector3, int> materialIndex)
    {
        var f = spec.Terrain!;
        var corner = spec.TerrainCorner - origin;
        int first = verts.Count, p = f.Posts;
        for (int j = 0; j < p; j++)
            for (int i = 0; i < p; i++)
                verts.Add(Phonon.World(new Vector3(corner.X + i * f.Spacing, f.Heights[j * p + i] - origin.Y, corner.Z + j * f.Spacing)));
        var panel = new Vector3(f.Spacing, Heightfield.Depth, f.Spacing);
        var mats = new int[f.Materials.Length];
        for (int m = 0; m < mats.Length; m++) mats[m] = materialIndex(f.Materials[m], panel);
        int c = f.CellsPerSide;
        for (int j = 0; j < c; j++)
            for (int i = 0; i < c; i++)
            {
                int a = first + j * p + i, b = a + 1, d = a + p, e = d + 1;
                int mi = mats[f.Cells[j * c + i]];
                // Up in the game's frame is (a, e, b) and (a, d, e); Steam Audio's frame is mirrored, so the
                // other way round, as the boxes are.
                tris.Add(new Phonon.IPLTriangle { i0 = a, i1 = b, i2 = e }); triMat.Add(mi);
                tris.Add(new Phonon.IPLTriangle { i0 = a, i1 = e, i2 = d }); triMat.Add(mi);
            }
        // Coarse ground's skirt, its own corners each, the other way round as above.
        var shift = new Vector3(corner.X, -origin.Y, corner.Z);
        for (int k = f.SurfaceTriangleCount; k < f.TriangleCount; k++)
        {
            var t = f.Triangle(k);
            int v = verts.Count;
            verts.Add(Phonon.World(t.V0 + shift));
            verts.Add(Phonon.World(t.V0 + t.E1 + shift));
            verts.Add(Phonon.World(t.V0 + t.E2 + shift));
            tris.Add(new Phonon.IPLTriangle { i0 = v, i1 = v + 2, i2 = v + 1 }); triMat.Add(mats[f.Cells[f.CellOfTriangle(k)]]);
        }
    }
}

/// <summary>
/// The ground's height under a point, from the terrain tiles a world holds; 0 where there are none, the
/// ground of every map that has no terrain.
/// </summary>
public sealed class GroundHeights
{
    private readonly Dictionary<(int, int), Heightfield> _tiles = new();
    private readonly Dictionary<(int, int), Vector3> _corners = new();
    private readonly float _tileMetres;

    /// <summary>The lowest the ground goes, 0 with no terrain.</summary>
    public float Lowest { get; }

    public static readonly GroundHeights Flat = new(Array.Empty<SolidSpec>());

    /// <summary>Terrain tiles all of one size (250 m), keyed by which square of that size their middle is in.</summary>
    public GroundHeights(IReadOnlyList<SolidSpec> terrains)
    {
        _tileMetres = 250f;
        foreach (var t in terrains) if (t.Terrain != null) { _tileMetres = t.Terrain.Size; break; }
        float lowest = 0f;
        foreach (var t in terrains)
        {
            if (t.Terrain == null) continue;
            var key = Key(t.Position.X, t.Position.Z);
            _tiles[key] = t.Terrain;
            _corners[key] = t.TerrainCorner;
            lowest = MathF.Min(lowest, t.Terrain.MinY);
        }
        Lowest = lowest;
    }

    private (int, int) Key(float x, float z) => ((int)MathF.Floor(x / _tileMetres), (int)MathF.Floor(z / _tileMetres));

    public bool IsFlat => _tiles.Count == 0;

    /// <summary>The ground's height at (x, z), or 0 where no terrain is.</summary>
    public float At(float x, float z)
    {
        if (_tiles.Count == 0) return 0f;
        if (!_tiles.TryGetValue(Key(x, z), out var f)) return 0f;
        var c = _corners[Key(x, z)];
        float h = f.HeightAt(x - c.X, z - c.Z);
        return float.IsNaN(h) ? 0f : h;
    }
}
