using System.Numerics;
using OpenFPS.Sound.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// A near-wall copy is as whole as the surface round the point the probe met (BoundaryModel.Share): a door's
/// jamb a foot deep, met between the ears, was a whole wall's copy of everything at each ear (Resonance, Sean
/// in a shop's open doorway, 2026-10-14). A wall stays whole however many pieces it is built of and however
/// the head is turned to it, so nothing changes in a room or along a street.
/// </summary>
public class NearWallShareTests
{
    private const int SampleRate = 48000;
    private static readonly Vector3 Ear = new(0f, 1.6f, 0f);

    private static EntityDefinition Box(int id, Vector3 at, Vector3 size, string material = "Brick") => new()
    {
        EntityId = id,
        Type = EntityType.StaticObject,
        Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
        Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
        Material = new MaterialComponent { Material = material },
    };

    /// <summary>The boxes as the client holds them: the triangle world the game casts into, or the boxes
    /// themselves (a map before its triangles are built).</summary>
    private static WorldSnapshot World(bool triangles, params (Vector3 At, Vector3 Size)[] boxes)
    {
        var defs = new List<EntityDefinition> { Box(1, new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f), "Concrete") };
        for (int i = 0; i < boxes.Length; i++) defs.Add(Box(10 + i, boxes[i].At, boxes[i].Size));
        if (triangles)
        {
            var client = new ClientWorldState();
            client.Geometry.Runner = work => work();
            client.RefreshRunner = work => work();
            client.Clear(new Vector3(100f, 40f, 100f));
            foreach (var d in defs) client.RegisterDefinition(d);
            client.GetSnapshot();
            var snap = client.GetSnapshot();
            Assert.True(SpatialService.UsesTriangles(snap));
            return snap;
        }
        var world = new WorldSnapshot();
        foreach (var d in defs) world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
        Assert.False(SpatialService.UsesTriangles(world));
        return world;
    }

    private static BoundaryProbe[] Probe(WorldSnapshot world, float yawDegrees)
    {
        var probes = new BoundaryProbe[BoundaryModel.ProbeDirections.Length];
        var head = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDegrees * MathF.PI / 180f);
        new AudioWorld.BoundaryProber().Probe(world, Ear, head, AudioPhysics.SpeedOfSound, probes);
        return probes;
    }

    /// <summary>What a probe's copy sends to the louder ear, 0 when it makes none.</summary>
    private static float Gain(BoundaryProbe probe)
        => BoundaryModel.TryBuildTap(probe, AudioPhysics.SpeedOfSound, SampleRate, out var tap) ? MathF.Max(tap.GainL, tap.GainR) : 0f;

    /// <summary>A brick wall 3 m high and 20 m long, its face 1.05 m east of the ear.</summary>
    private static readonly (Vector3, Vector3) EastWall = (new Vector3(1.2f, 1.5f, 0f), new Vector3(0.3f, 3f, 20f));

    [Theory]
    [InlineData(false, 0f)] [InlineData(false, 15f)] [InlineData(false, 30f)] [InlineData(false, 45f)]
    [InlineData(true, 0f)] [InlineData(true, 15f)] [InlineData(true, 30f)] [InlineData(true, 45f)]
    public void A_wall_met_at_an_angle_is_whole(bool triangles, float yaw)
    {
        var probes = Probe(World(triangles, EastWall), yaw);
        int heard = 0;
        foreach (var p in probes)
        {
            if (!(p.Distance < BoundaryModel.MaxDistance) || BoundaryModel.IsFloor(p.HeadDirection)) continue;
            heard++;
            Assert.True(p.Share == 1f, $"turned {yaw} degrees, the probe {p.HeadDirection} meets the wall {p.Distance:F2} m off as {p.Share:F2} of one");
            // And its copy is what it always was.
            Assert.Equal(Gain(p with { Share = 1f }), Gain(p));
        }
        Assert.True(heard >= 1, "no probe met the wall");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void A_wall_of_four_pieces_sends_back_what_a_whole_wall_does(bool triangles)
    {
        // Split where the probe meets it, along and up: four pieces meeting at the point hit.
        var pieces = World(triangles,
            (new Vector3(1.2f, 0.8f, -5f), new Vector3(0.3f, 1.6f, 10f)), (new Vector3(1.2f, 0.8f, 5f), new Vector3(0.3f, 1.6f, 10f)),
            (new Vector3(1.2f, 2.3f, -5f), new Vector3(0.3f, 1.4f, 10f)), (new Vector3(1.2f, 2.3f, 5f), new Vector3(0.3f, 1.4f, 10f)));
        var whole = World(triangles, EastWall);
        foreach (float yaw in new[] { 0f, 30f })
        {
            var a = Probe(pieces, yaw);
            var b = Probe(whole, yaw);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal(b[i].Share, a[i].Share);
                Assert.Equal(Gain(b[i]), Gain(a[i]), 5);
            }
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void A_doors_jamb_sends_back_under_a_quarter_of_a_wall(bool triangles)
    {
        // A brick front 0.3 m deep across the ear's path, a doorway 1.9 m wide and 2.1 m high in it (Marlow
        // Tower's front entrance), the ear in the middle of it facing through: each jamb 0.95 m to a side.
        const float Depth = 0.3f, Half = 0.95f;
        var doorway = World(triangles,
            (new Vector3(-Half - 5f, 1.5f, 0f), new Vector3(10f, 3f, Depth)),
            (new Vector3(Half + 5f, 1.5f, 0f), new Vector3(10f, 3f, Depth)),
            (new Vector3(0f, 2.55f, 0f), new Vector3(2f * Half, 0.9f, Depth)));
        // The same distance to a whole wall each side: a corridor.
        var corridor = World(triangles,
            (new Vector3(-Half - 0.15f, 1.5f, 0f), new Vector3(0.3f, 3f, 20f)),
            (new Vector3(Half + 0.15f, 1.5f, 0f), new Vector3(0.3f, 3f, 20f)));
        var jamb = Probe(doorway, 0f);
        var wall = Probe(corridor, 0f);
        foreach (int side in new[] { 0, 1 })      // +X and -X in head space
        {
            Assert.Equal(Half, jamb[side].Distance, 3);
            Assert.Equal(Half, wall[side].Distance, 3);
            float ratio = Gain(jamb[side]) / Gain(wall[side]);
            Assert.True(ratio < 0.25f, $"the jamb sends back {ratio:F2} of a wall's copy (share {jamb[side].Share:F2})");
            Assert.True(ratio > 0f, "a jamb is still a surface");
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void A_wall_that_stays_whole_is_not_raised_when_its_neighbours_go(bool triangles)
    {
        // A doorway 1 m wide in a front 0.2 m deep, its head 0.3 m over the ear, and the room's side wall flush
        // with its right jamb: as whole walls the three overran the copies' budget and were trimmed together.
        // Taken as part walls the jamb and the head fall under it; the side wall must not take what they gave up.
        var doorway = World(triangles,
            (new Vector3(-0.5f - 5f, 1.5f, 0f), new Vector3(10f, 3f, 0.2f)),
            (new Vector3(0.65f, 1.5f, 0f), new Vector3(0.3f, 3f, 10f)),
            (new Vector3(0f, 2.45f, 0f), new Vector3(1f, 1.1f, 0.2f)));
        var probes = Probe(doorway, 0f);
        Assert.Equal(1f, probes[0].Share);                     // the side wall, 0.5 m to the right
        Assert.True(probes[1].Share < 1f && probes[2].Share < 1f, $"jamb {probes[1].Share:F2}, head {probes[2].Share:F2}");

        int n = probes.Length;
        Span<BoundaryTap> now = stackalloc BoundaryTap[n], whole = stackalloc BoundaryTap[n];
        Span<bool> liveNow = stackalloc bool[n], liveWhole = stackalloc bool[n];
        var asWalls = probes.Select(p => p with { Share = 1f }).ToArray();
        float trimNow = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.BoundaryTaps(probes, AudioPhysics.SpeedOfSound, SampleRate, now, liveNow);
        float trimWhole = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.BoundaryTaps(asWalls, AudioPhysics.SpeedOfSound, SampleRate, whole, liveWhole);
        Assert.True(trimWhole < 1f, "the walls as whole ones did not reach the budget: the test proves nothing");
        for (int i = 0; i < n; i++)
        {
            if (!liveNow[i]) continue;
            Assert.True(liveWhole[i]);
            Assert.True(MathF.Max(now[i].GainL, now[i].GainR) * trimNow <= MathF.Max(whole[i].GainL, whole[i].GainR) * trimWhole + 1e-6f,
                        $"probe {i} sends back {20 * MathF.Log10(MathF.Max(now[i].GainL, now[i].GainR) * trimNow / (MathF.Max(whole[i].GainL, whole[i].GainR) * trimWhole)):F1} dB over the walls it was");
        }
    }
}
