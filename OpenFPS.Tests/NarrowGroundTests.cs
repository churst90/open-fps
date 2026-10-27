using System.Numerics;
using OpenFPS.Audio.SteamAudio;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// Ground that is stood on is open ground whatever its width: a kerb, a gutter, a threshold and a ramp
/// came back off the listener's feet 9-15 ms late like a floor (Sean, 2026-10-20, "a box"), where only a
/// piece a metre wide each way and level was left out. A bench seat, a table, a low wall and a roof stay
/// in the trace, and so does anything under cover. Both ways the rule is used are held to it: the whole
/// map's filter and the tiles' flags.
/// </summary>
public class NarrowGroundTests
{
    private static readonly Quaternion Q = Quaternion.Identity;
    private static readonly SteamAudioScene.Box Ground = new(new Vector3(0, -0.1f, 0), new Vector3(400, 0.2f, 400), Q, "Dirt", default, 1);

    private static SteamAudioScene.Box At(int id, float x, float topY, float sx, float thick, float sz, string material = "Concrete")
        => new(new Vector3(x, topY - thick * 0.5f, 0), new Vector3(sx, thick, sz), Q, material, default, id);

    /// <summary>Whether the whole-map filter leaves the piece out; the tiled store must agree.</summary>
    private static bool IsOpen(SteamAudioScene.Box piece, params SteamAudioScene.Box[] others)
    {
        var all = new List<SteamAudioScene.Box> { Ground, piece };
        all.AddRange(others);
        bool whole = !SteamAudioScene.WithoutOpenGround(all).Any(b => b.EntityId == piece.EntityId);
        var world = new AcousticGeometry(250f).Update(all);
        var accept = new AcceptAll();
        var (_, hi) = SteamAudioScene.WorldExtents(piece);
        var from = new Vector3(piece.Center.X, hi.Y + 0.5f, piece.Center.Z);
        Assert.True(world.Closest(from, -Vector3.UnitY, 5f, GeometryLayers.Acoustics, RayFaces.Front, ref accept, out var hit));
        // The ray meets the piece's own top (above the ground, under any cover): its flag is the tiles' answer.
        Assert.InRange(from.Y - hit.T, 0.005f, hi.Y + 0.03f);
        Assert.Equal(whole, world.SurfaceOf(hit).Is(SurfaceFlags.OpenGround));
        return whole;
    }

    [Fact]
    public void A_kerb_a_gutter_and_a_threshold_are_open_ground()
    {
        Assert.True(IsOpen(At(10, 5f, 0.15f, 0.15f, 0.15f, 40f)), "kerb");
        Assert.True(IsOpen(At(11, 6f, 0.04f, 0.3f, 0.04f, 40f, "Asphalt")), "gutter");
        Assert.True(IsOpen(At(12, 9f, 0.03f, 0.9f, 0.03f, 0.2f)), "threshold strip");
    }

    [Fact]
    public void A_ramp_walked_up_is_open_ground_and_a_steep_plank_is_not()
    {
        // 15 degrees: a body walks it; its low end is at the ground and its high end 0.8 m up.
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 15f * MathF.PI / 180f);
        var ramp = new SteamAudioScene.Box(new Vector3(4f, 0.42f, 0), new Vector3(2f, 0.1f, 3f), tilt, "Concrete", default, 20);
        var (_, hi) = SteamAudioScene.WorldExtents(ramp);
        Assert.InRange(hi.Y, 0.7f, 0.9f);
        Assert.True(IsOpen(ramp), "ramp");
        var steep = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 60f * MathF.PI / 180f);
        Assert.False(IsOpen(new SteamAudioScene.Box(new Vector3(4f, 0.6f, 0), new Vector3(2f, 0.1f, 3f), steep, "Wood", default, 21)), "60 degree plank");
    }

    [Fact]
    public void A_bench_a_table_a_low_wall_and_a_roof_are_not()
    {
        Assert.False(IsOpen(At(30, 5f, 0.45f, 1.6f, 0.45f, 0.5f, "Wood")), "bench seat");
        Assert.False(IsOpen(At(31, 5f, 0.75f, 1.5f, 0.05f, 0.8f, "Wood")), "table top");
        Assert.False(IsOpen(At(32, 5f, 0.9f, 0.3f, 0.9f, 4f, "Brick")), "low wall");
        Assert.False(IsOpen(At(33, 5f, 0.45f, 0.3f, 0.45f, 4f, "Brick")), "wall just over a step");
        Assert.False(IsOpen(At(34, 5f, 3.2f, 6f, 0.2f, 6f)), "roof");
    }

    [Fact]
    public void A_strip_under_cover_is_not()
    {
        var canopy = At(40, 5f, 3.0f, 6f, 0.2f, 6f);
        Assert.False(IsOpen(At(41, 5f, 0.15f, 0.15f, 0.15f, 3f), canopy), "kerb under a canopy");
        Assert.False(IsOpen(At(42, 5f, 0.03f, 0.9f, 0.03f, 0.2f), canopy), "threshold under a lintel");
        Assert.True(IsOpen(At(43, 30f, 0.15f, 0.15f, 0.15f, 3f), canopy), "the same kerb in the open");
    }
}
