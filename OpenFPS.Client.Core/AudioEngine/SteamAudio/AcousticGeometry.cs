using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The acoustic scene as a triangle world (docs/GEOMETRY.md stage 1): the boxes the Steam Audio scene is
/// built from (SteamAudioScene.BoxesFromWorld: solid, fixed, no sound source's own box, door leaves where
/// they stand), each a box shape in its tile, each tile's open ground flagged on its surfaces. One store
/// for both of its readers: the Steam Audio tile sub-scenes (TileSceneSet) and the hand-built acoustics
/// (Enclosure's survey). Only the tiles whose boxes changed are built again.
/// </summary>
public sealed class AcousticGeometry
{
    private readonly TriangleWorldBuilder _builder;
    public float TileMetres => _builder.TileMetres;

    public TriangleWorld World => _builder.Current;
    public TriangleWorldBuilder Builder => _builder;

    public AcousticGeometry(float tileMetres) => _builder = new TriangleWorldBuilder(tileMetres);

    /// <summary>The cell of the grid of what covers the ground, metres (as TileSceneSet had it).</summary>
    private const float CoverCell = 16f;

    /// <summary>A one-off world from a box list (the parity harness, tests).</summary>
    public static TriangleWorld FromBoxes(IReadOnlyList<SteamAudioScene.Box> boxes, float tileMetres)
        => new AcousticGeometry(tileMetres).Update(boxes);

    /// <summary>Brings the world up to <paramref name="boxes"/>, building only the tiles that changed.</summary>
    public TriangleWorld Update(IReadOnlyList<SteamAudioScene.Box> boxes)
    {
        // What stands over the ground, filed in a grid, so a slab is not tested against every box round it:
        // anything whose underside is a metre and a half up or more.
        var cover = new Dictionary<(int, int), List<(Vector3 Min, Vector3 Max)>>();
        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            var (lo, hi) = SteamAudioScene.WorldExtents(b);
            if (lo.Y < SteamAudioScene.LowestCover) continue;
            for (int cx = (int)MathF.Floor(lo.X / CoverCell); cx <= (int)MathF.Floor(hi.X / CoverCell); cx++)
                for (int cz = (int)MathF.Floor(lo.Z / CoverCell); cz <= (int)MathF.Floor(hi.Z / CoverCell); cz++)
                {
                    if (!cover.TryGetValue((cx, cz), out var l)) cover[(cx, cz)] = l = new List<(Vector3, Vector3)>();
                    l.Add((lo, hi));
                }
        }
        bool CoveredAt(float x, float z, float lowest)
        {
            if (!cover.TryGetValue(((int)MathF.Floor(x / CoverCell), (int)MathF.Floor(z / CoverCell)), out var l)) return false;
            foreach (var (lo, hi) in l)
                if (lo.Y >= lowest && x >= lo.X && x <= hi.X && z >= lo.Z && z <= hi.Z) return true;
            return false;
        }

        var specs = new List<SolidSpec>(boxes.Count);
        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            bool open = SteamAudioScene.IsOpenGround(b, SteamAudioScene.WorldExtents(b), CoveredAt);
            specs.Add(SpecOf(b, open));
        }
        return _builder.Build(specs, Array.Empty<SolidSpec>());
    }

    /// <summary>A scene box as a solid of the acoustic layer.</summary>
    public static SolidSpec SpecOf(in SteamAudioScene.Box b, bool openGround)
    {
        var flags = openGround ? SurfaceFlags.OpenGround : SurfaceFlags.None;
        var surface = new Surface(b.Material ?? "", new Construction(b.Size, b.Build), GeometryLayers.Acoustics, flags);
        return new SolidSpec(b.EntityId, b.Center, b.Rotation, b.Size, surface);
    }
}
