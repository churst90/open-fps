using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A timber-framed house wall is several stuffs in one box (Assemblies): cladding and sheathing, a stud cavity, and
/// plasterboard, two leaves over a cavity the studs bridge. The real places' walls were 25 cm of solid timber; a line
/// along one paid 1.7 m of wood. What a wall of one assembly lets through is the plies' layered panel, crossed either
/// way; a board of its own against an unlined wall is the same wall; and a line along it runs in its cavity.
/// </summary>
public class TimberFrameWallTests
{
    private readonly ITestOutputHelper _o;
    public TimberFrameWallTests(ITestOutputHelper o) => _o = o;

    private static float Db(float gain) => -20f * MathF.Log10(MathF.Max(1e-12f, gain));
    private static float Db(Vector3 g, int band) => Db(band == 0 ? g.X : band == 1 ? g.Y : g.Z);
    private static Vector3 G((float L, float M, float H) g) => new(g.L, g.M, g.H);
    private static MaterialProperties P(string m) { AcousticRegistry.Initialize(); return AcousticRegistry.GetProperties(m); }
    private static Assemblies.Assembly A(string name) => Assemblies.Named(name)!;
    private static WallBuild Built(string name) => new(0f, 0f, A(name).Id);

    private static void Near(Vector3 want, Vector3 got, float tolDb, string what)
    {
        for (int band = 0; band < 3; band++)
            Assert.True(MathF.Abs(Db(want, band) - Db(got, band)) <= tolDb,
                        $"{what}, band {band}: want {Db(want, band):F2} dB, got {Db(got, band):F2} dB");
    }

    [Fact]
    public void Every_assembly_is_known_stuff_and_every_cavity_has_a_board_either_side()
    {
        AcousticRegistry.Initialize();
        Assert.Equal(Assemblies.All.Count, Assemblies.All.Select(a => a.Id).Distinct().Count());
        foreach (var a in Assemblies.All)
        {
            Assert.NotEqual(0, a.Id);
            Assert.Same(a, Assemblies.Get(a.Id));
            Assert.Same(a, Assemblies.Named(a.Name));
            var props = a.Plies.Select(p => { Assert.True(AcousticRegistry.IsKnown(p.Material), $"{a.Name}: {p.Material}"); return P(p.Material); }).ToList();
            Assert.True(Assemblies.HasCavity(a), a.Name);
            for (int i = 0; i < props.Count; i++)
                if (WallTransmission.IsCavity(props[i]) && !(i + 1 < props.Count && WallTransmission.IsCavity(props[i + 1])))
                {
                    // A cavity ending here: something airtight before it and after it, or the wall is unlined there.
                    bool before = props.Take(i).Any(q => WallTransmission.Seals(q));
                    Assert.True(before, $"{a.Name}: nothing airtight before ply {i}");
                }
        }
    }

    [Theory]
    [InlineData("GlassWool", true)] [InlineData("None", true)] [InlineData("Foam", true)]
    [InlineData("Dirt", false)] [InlineData("Carpet", false)] [InlineData("Snow", false)] [InlineData("Plaster", false)] [InlineData("Grass", false)]
    public void A_cavity_is_what_is_softer_than_its_air_and_lets_it_through(string material, bool cavity)
        => Assert.Equal(cavity, WallTransmission.IsCavity(P(material)));

    /// <summary>Boards over a batt-filled cavity, as three layers, are the stud partition the two-leaf build already was.</summary>
    [Fact]
    public void Boards_either_side_of_glass_fibre_are_the_two_leaf_partition()
    {
        var two = G(WallTransmission.BandGains("Plaster", new Vector3(4f, 2.6f, 0.1143f), new WallBuild(0.0127f, 0.4064f)));
        var three = G(WallTransmission.LayeredBandGains(new[]
        {
            new WallTransmission.Layer(P("Plaster"), 0.0127f, WallBuild.Solid),
            new WallTransmission.Layer(P("GlassWool"), 0.0889f, new WallBuild(0f, 0.4064f)),
            new WallTransmission.Layer(P("Plaster"), 0.0127f, WallBuild.Solid),
        }, 4f, 2.6f));
        Near(two, three, 0.01f, "board, glass fibre, board against the two-leaf partition");
    }

    /// <summary>Glass fibre on the face of a construction is no cavity: nothing airtight beyond it.</summary>
    [Fact]
    public void Glass_fibre_on_a_face_is_its_weight_not_a_cavity()
    {
        var board = G(WallTransmission.BandGains("Plaster", new Vector3(4f, 2.6f, 0.0127f), WallBuild.Solid));
        var both = G(WallTransmission.LayeredBandGains(new[]
        {
            new WallTransmission.Layer(P("Plaster"), 0.0127f, WallBuild.Solid),
            new WallTransmission.Layer(P("GlassWool"), 0.0889f, new WallBuild(0f, 0.4064f)),
        }, 4f, 2.6f));
        Near(board, both, 1.0f, "a board with batts on its back against the board");
    }

    /// <summary>The siding wall against what it was (25 cm of solid timber): lighter, so it takes less, most of all at the
    /// bottom; the double wall's rise above its resonance. Printed by third octave for the record against Bradley and Birta's
    /// measured walls (a dip at 125 Hz from the studs, coincidence at 2.5 to 3.1 kHz).</summary>
    [Fact]
    public void The_siding_wall_is_lighter_and_more_open_low_than_solid_timber()
    {
        var face = new Vector3(4f, 2.75f, 0f);
        var siding = new Vector3(face.X, face.Y, A("WoodFrameSiding").Metres);
        var brick = new Vector3(face.X, face.Y, A("WoodFrameBrickVeneer").Metres);
        var timber = new Vector3(face.X, face.Y, 0.25f);
        var s = G(WallTransmission.BandGains("Wood", siding, Built("WoodFrameSiding")));
        var b = G(WallTransmission.BandGains("Brick", brick, Built("WoodFrameBrickVeneer")));
        var t = G(WallTransmission.BandGains("Wood", timber, WallBuild.Solid));
        var solidBrick = G(WallTransmission.BandGains("Brick", new Vector3(face.X, face.Y, 0.25f), WallBuild.Solid));
        _o.WriteLine($"bands low/mid/high dB: siding {Db(s, 0):F1}/{Db(s, 1):F1}/{Db(s, 2):F1}, brick veneer {Db(b, 0):F1}/{Db(b, 1):F1}/{Db(b, 2):F1}, "
                     + $"25 cm timber {Db(t, 0):F1}/{Db(t, 1):F1}/{Db(t, 2):F1}, 25 cm brick {Db(solidBrick, 0):F1}/{Db(solidBrick, 1):F1}/{Db(solidBrick, 2):F1}");
        _o.WriteLine($"mass: siding {Assemblies.MassPerArea(A("WoodFrameSiding"), siding.Z):F1} kg/m2, brick veneer {Assemblies.MassPerArea(A("WoodFrameBrickVeneer"), brick.Z):F1}, timber {650f * 0.25f:F1}");
        var thirds = new[] { 50f, 63f, 80f, 100f, 125f, 160f, 200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f, 2000f, 2500f, 3150f, 4000f };
        _o.WriteLine("Hz      siding  veneer  timber");
        foreach (float hz in thirds)
            _o.WriteLine($"{hz,6:F0}  {WallTransmission.BoxLossDb(P("Wood"), siding, Built("WoodFrameSiding"), hz),6:F1}  "
                         + $"{WallTransmission.BoxLossDb(P("Brick"), brick, Built("WoodFrameBrickVeneer"), hz),6:F1}  {WallTransmission.BoxLossDb(P("Wood"), timber, WallBuild.Solid, hz),6:F1}");
        for (int band = 0; band < 3; band++) Assert.True(Db(s, band) < Db(t, band), $"band {band}: siding {Db(s, band):F1} dB, timber {Db(t, band):F1}");
        Assert.True(Db(t, 0) - Db(s, 0) > 4f, $"low band: siding {Db(s, 0):F1} dB against timber {Db(t, 0):F1}");
        // Two leaves the studs bridge: about the mass law of its whole weight at the bottom, and above the resonance
        // better than that law by the studs' term, not by the free double wall's 18 dB an octave.
        float m = Assemblies.MassPerArea(A("WoodFrameSiding"), siding.Z);
        float r125 = WallTransmission.BoxLossDb(P("Wood"), siding, Built("WoodFrameSiding"), 125f);
        float r250 = WallTransmission.BoxLossDb(P("Wood"), siding, Built("WoodFrameSiding"), 250f);
        float r1k = WallTransmission.BoxLossDb(P("Wood"), siding, Built("WoodFrameSiding"), 1000f);
        Assert.InRange(r125 - WallTransmission.MassLawDb(m, 125f), -3f, 3f);
        Assert.True(r1k > WallTransmission.MassLawDb(m, 1000f) + 1f, $"1 kHz {r1k:F1} dB, mass law {WallTransmission.MassLawDb(m, 1000f):F1}");
        Assert.True(r1k - r250 < 24f, $"250 Hz {r250:F1} dB, 1 kHz {r1k:F1} dB");
        // The veneer keeps out more than the siding in every band.
        for (int band = 0; band < 3; band++) Assert.True(Db(b, band) > Db(s, band), $"band {band}");
    }

    /// <summary>The plies are crossed in order either way, and an unlined wall with its board in contact is the lined wall:
    /// the board joins the leaf it touches, the cavity stays between.</summary>
    [Theory]
    [InlineData("WoodFrameSiding", "WoodFrameSidingUnlined", "Wood")]
    [InlineData("WoodFrameBrickVeneer", "WoodFrameBrickVeneerUnlined", "Brick")]
    public void An_unlined_wall_and_its_board_are_the_lined_wall_crossed_either_way(string lined, string unlined, string face)
    {
        float tw = A(unlined).Metres, tb = 0.0127f;
        var size = new Vector3(4f, 2.75f, tw);
        var lin = G(WallTransmission.BandGains(face, new Vector3(4f, 2.75f, A(lined).Metres), Built(lined)));
        var n = Vector3.UnitZ;
        foreach (var dir in new[] { n, -n })
        {
            // Wall from z 0 to tw (its plies from the outside, z 0), the board from tw to tw + tb.
            var wall = Constructions.Of(dir.Z > 0 ? 0f : tb, dir.Z > 0 ? tw : tw + tb, P(face), size, Built(unlined), 1, n);
            var board = Constructions.Of(dir.Z > 0 ? tw : 0f, dir.Z > 0 ? tw + tb : tb, P("Plaster"), new Vector3(4f, 2.75f, tb), WallBuild.Solid, 1, n);
            var got = Constructions.Through(new List<Constructions.Crossing> { wall, board }, dir);
            Near(lin, got, 0.3f, $"{unlined} and a board, crossed {(dir.Z > 0 ? "out to in" : "in to out")}");
        }
        // The board on the wrong face (against the cladding) is not the same wall: the order is read.
        var wrongWall = Constructions.Of(tb, tb + tw, P(face), size, Built(unlined), 1, n);
        var wrongBoard = Constructions.Of(0f, tb, P("Plaster"), new Vector3(4f, 2.75f, tb), WallBuild.Solid, 1, n);
        var wrong = Constructions.Through(new List<Constructions.Crossing> { wrongBoard, wrongWall }, n);
        Assert.True(MathF.Abs(Db(wrong, 1) - Db(lin, 1)) > 3f, $"board on the cladding {Db(wrong, 1):F1} dB, lined {Db(lin, 1):F1}");
    }

    [Fact]
    public void A_line_along_an_assembly_runs_in_its_cavity_and_keeps_its_panel()
    {
        var wall = new Vector3(6f, 2.75f, A("WoodFrameSiding").Metres);
        var c = Constructions.Of(1f, 3f, P("Wood"), wall, Built("WoodFrameSiding"), 1, Vector3.UnitZ, 2f);
        Assert.Equal(0f, c.Run);
        Assert.Equal(c.AsPanel, c.Gains);
    }

    /// <summary>The hand-rolled tracer through a generated wall's box (its plies from its -Z face) and its board against
    /// it: the lined wall's figure, from outside in and inside out.</summary>
    [Fact]
    public void The_tracer_hears_an_unlined_wall_and_its_board_as_the_lined_wall()
    {
        var world = new ClientWorldState();
        world.Clear(new Vector3(200, 40, 200));
        float tw = A("WoodFrameSidingUnlined").Metres, tb = 0.0127f;
        void Add(int id, float z0, float z1, string mat, AcousticComponent a)
            => world.RegisterDefinition(new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = new Vector3(0, 1.4f, (z0 + z1) / 2), Rotation = Quaternion.Identity },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(6f, 2.75f, z1 - z0), IsSolid = true },
                Material = new MaterialComponent { Material = mat }, Acoustics = a,
            });
        Add(1, 0f, tw, "Wood", new AcousticComponent { Assembly = A("WoodFrameSidingUnlined").Id });
        Add(2, tw, tw + tb, "Plaster", new AcousticComponent());
        var snap = world.GetSnapshot();
        var lin = G(WallTransmission.BandGains("Wood", new Vector3(6f, 2.75f, A("WoodFrameSiding").Metres), Built("WoodFrameSiding")));
        foreach (var (a, b) in new[] { (new Vector3(0.2f, 1.5f, -3f), new Vector3(-0.3f, 1.3f, 3f)), (new Vector3(-0.3f, 1.3f, 3f), new Vector3(0.2f, 1.5f, -3f)) })
        {
            new SpatialService().GetOcclusionData(snap, a, b, out _, out _, out float l, out float m, out float h);
            Near(lin, new Vector3(l, m, h), 0.5f, $"traced from z {a.Z}");
        }
    }
}
