using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Geometry;
using Xunit.Abstractions;
using OpenFPS.Acoustics;
using OpenFPS.Materials;

namespace OpenFPS.Tests;

/// <summary>
/// The ground in the way of a straight segment (docs/GEOMETRY.md 12.6, "The ground in the way"): a line or a
/// leg through the earth passes nothing, and what gets past is the string over the ground's profile,
/// diffracted by the barrier model's law (Maekawa), as ISO 9613-2 takes a terrain profile's ridges as screens.
/// </summary>
public class OpeningRoutesTerrainTests
{
    private readonly ITestOutputHelper _o;
    public OpeningRoutesTerrainTests(ITestOutputHelper o) => _o = o;

    private static readonly int[] None = Array.Empty<int>();
    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));
    private static Vector3 Db3(Vector3 g) => new(Db(g.X), Db(g.Y), Db(g.Z));

    /// <summary>A tile of ground 248 m across centred on the origin, posts every 2 m (z = 0 on a post row).</summary>
    private static SolidSpec Ground(Func<float, float, float> height)
    {
        const int posts = 125;
        const float spacing = 2f;
        var heights = new float[posts * posts];
        for (int j = 0; j < posts; j++)
            for (int i = 0; i < posts; i++)
                heights[j * posts + i] = height(-124f + i * spacing, -124f + j * spacing);
        return SolidSpec.OfTerrain(1, Vector3.Zero, new Heightfield(posts, spacing, heights, null, null),
                                   OpenFPS.Audio.SteamAudio.SceneTerrain.Surface);
    }

    /// <summary>A ridge along x, its crest <paramref name="top"/> m high at z = <paramref name="at"/>, its sides 1 in 2.</summary>
    private static float Ridge(float z, float at, float top) => MathF.Max(0f, top - 0.5f * MathF.Abs(z - at));

    private static OpeningRoutes Routes(IEnumerable<(Vector3 Centre, Vector3 Size, string Material)> boxes, SolidSpec ground)
    {
        AcousticRegistry.EnsureInitialized();
        var solids = boxes.Select(b => new OpeningRoutes.Solid(b.Centre, b.Size, Quaternion.Identity, b.Material)).ToList();
        return OpeningRoutes.Build(solids, null, Array.Empty<OpeningRoutes.Declared>(), null, new[] { ground });
    }

    private static Vector3 WithoutGround(Func<Vector3> f)
    {
        OpeningRoutes.GroundNoObstacleForLab = true;
        try { return f(); }
        finally { OpeningRoutes.GroundNoObstacleForLab = false; }
    }

    /// <summary>What a detour of this many metres leaves, per band, against the free field at the straight distance.</summary>
    private static Vector3 Over(float delta, float dist)
    {
        var (l, m, h) = Diffraction.BandGains(delta);
        return new Vector3(l, m, h) * (dist / (dist + delta));
    }

    private static void Near(Vector3 expected, Vector3 got, float db, string what)
    {
        var e = Db3(expected); var g = Db3(got);
        for (int k = 0; k < 3; k++)
        {
            float a = k == 0 ? e.X : k == 1 ? e.Y : e.Z, b = k == 0 ? g.X : k == 1 ? g.Y : g.Z;
            Assert.True(MathF.Abs(a - b) <= db, $"{what}, band {k}: {b:F2} dB where {a:F2} was expected");
        }
    }

    /// <summary>A source behind a rise, nothing else in the scene: before, the line went through the hill for
    /// nothing; now it is the string over the crest, Maekawa's loss for its path difference, and the bearing is
    /// the crest's.</summary>
    [Fact]
    public void A_hill_between_is_gone_over_its_crest()
    {
        var routes = Routes(Array.Empty<(Vector3, Vector3, string)>(), Ground((x, z) => Ridge(z, 0f, 4f)));
        var src = new Vector3(3f, 0.5f, -50f);
        var ear = new Vector3(3f, 1.6f, 50f);
        float d = Vector3.Distance(src, ear);
        // Over the crest a joint (5 cm) over the ground, as over a box's top.
        float delta = MathF.Sqrt(50f * 50f + 3.55f * 3.55f) + MathF.Sqrt(50f * 50f + 2.45f * 2.45f) - d;
        Assert.True(routes.ThroughTheGround(src, ear));
        var before = WithoutGround(() => routes.LegGains(src, ear, None, None));
        var after = routes.LegGains(src, ear, None, None);
        float pd = routes.BarrierPathDifference(src, ear, out var edge, out bool verified);
        _o.WriteLine($"before {Db3(before)}, now {Db3(after)}; over the crest {pd:F3} m (expected {delta:F3}), from {edge}");
        Assert.Equal(Vector3.One, before);
        Assert.True(verified);
        Assert.InRange(pd, delta - 0.01f, delta + 0.01f);
        Near(Over(delta, d), after, 0.1f, "the leg");
        Assert.InRange(edge.Z, -0.01f, 0.01f);
        Assert.InRange(edge.Y, 4.04f, 4.06f);
        // The hand-rolled tracer's asker and the worker's correction say the same.
        Assert.True(routes.OverTheGround(src, ear, WallTransmission.SoundSpeed, out var gains, out var top, out bool found));
        Assert.True(found);
        Near(after, gains, 0.01f, "over the ground");
        Assert.True(Vector3.Distance(top, edge) < 0.01f);
        Assert.Equal(Vector3.Zero, routes.SimulatorCorrection(src, ear));
    }

    /// <summary>Two ridges: the string over both, as ISO 9613-2 goes over the two outermost edges of several
    /// screens.</summary>
    [Fact]
    public void Two_ridges_are_gone_over_by_one_string()
    {
        var routes = Routes(Array.Empty<(Vector3, Vector3, string)>(), Ground((x, z) => MathF.Max(Ridge(z, -20f, 3f), Ridge(z, 16f, 5f))));
        var src = new Vector3(0f, 0.5f, -50f);
        var ear = new Vector3(0f, 1.6f, 50f);
        float d = Vector3.Distance(src, ear);
        float delta = MathF.Sqrt(30f * 30f + 2.55f * 2.55f) + MathF.Sqrt(36f * 36f + 2f * 2f) + MathF.Sqrt(34f * 34f + 3.45f * 3.45f) - d;
        float pd = routes.BarrierPathDifference(src, ear, out var edge, out bool verified);
        _o.WriteLine($"over both {pd:F3} m (expected {delta:F3}), last from {edge}");
        Assert.True(verified);
        Assert.InRange(pd, delta - 0.01f, delta + 0.01f);
        Assert.InRange(edge.Z, 15.99f, 16.01f);
        Near(Over(delta, d), routes.LegGains(src, ear, None, None), 0.1f, "the leg");
    }

    /// <summary>A line clear of the ground, and one from a source standing on it: the ground is not in their way.</summary>
    [Fact]
    public void A_line_over_the_ground_or_from_a_source_on_it_is_not_blocked()
    {
        var routes = Routes(Array.Empty<(Vector3, Vector3, string)>(), Ground((x, z) => Ridge(z, 0f, 4f) + 0.02f * x));
        var src = new Vector3(0f, 6f, -50f);
        var ear = new Vector3(0f, 6f, 50f);
        Assert.False(routes.ThroughTheGround(src, ear));
        Assert.Equal(Vector3.One, routes.LegGains(src, ear, None, None));
        Assert.Equal(Vector3.One, routes.SimulatorCorrection(src, ear));
        Assert.False(routes.OverTheGround(src, ear, WallTransmission.SoundSpeed, out _, out _, out _));
        // A footstep on the slope to an ear 1.6 m over it, 30 m on along the ground: on the ground is not in it.
        var foot = new Vector3(10f, 0.2f, -60f);
        var head = new Vector3(40f, 0.8f + 1.6f, -60f);
        Assert.False(routes.ThroughTheGround(foot, head));
        Assert.Equal(Vector3.One, routes.LegGains(foot, head, None, None));
    }

    /// <summary>
    /// A berm 2 m high in front of a tall wall: the way round the wall's end went through the berm for
    /// nothing, so it beat the way over the wall's top. Now that leg is through the earth and passes
    /// nothing; what is left is the string over the wall, and the berm is under it. The same from the box
    /// list and from the triangle store.
    /// </summary>
    [Fact]
    public void A_leg_round_a_wall_that_dips_under_a_berm_is_blocked()
    {
        var wall = (new Vector3(0f, 5f, 0f), new Vector3(10f, 10f, 0.2f), "Concrete");
        var ground = Ground((x, z) => Ridge(z, -10f, 2f));
        var routes = Routes(new[] { wall }, ground);
        var builder = new TriangleWorldBuilder(250f);
        var spec = new SolidSpec(2, wall.Item1, Quaternion.Identity, wall.Item2,
                                 EntityGeometry.SurfaceOf("Concrete", wall.Item2, 0, 0, false, 0, 0, false, false, false));
        var tiled = OpeningRoutes.Build(builder.Build(new[] { spec, ground }, Array.Empty<SolidSpec>()), null,
                                        Array.Empty<OpeningRoutes.Declared>(), null, new OpeningRoutes.TileCache());
        var src = new Vector3(0f, 0.5f, -20f);
        var ear = new Vector3(0f, 1.6f, 10f);
        float d = Vector3.Distance(src, ear);
        var before = WithoutGround(() => routes.LegGains(src, ear, None, None));
        var after = routes.LegGains(src, ear, None, None);
        var fromTiles = tiled.LegGains(src, ear, None, None);
        routes.BarrierGains(src, ear, WallTransmission.SoundSpeed, out float pd, out _, out var edge, out bool found);
        // Over the wall's top and its 20 cm.
        float overTop = Vector3.Distance(src, new Vector3(0f, 10f, -0.1f)) + 0.2f + Vector3.Distance(new Vector3(0f, 10f, 0.1f), ear) - d;
        _o.WriteLine($"before {Db3(before)}, now {Db3(after)} (tiles {Db3(fromTiles)}); {pd:F2} m from {edge}, over the top {overTop:F2}");
        Assert.True(found);
        Assert.True(after.X < before.X * 0.7f, $"the way round the end still beats the way over ({Db(after.X):F1} against {Db(before.X):F1} dB)");
        Assert.True(edge.Y > 9.9f, $"heard from {edge}, not the wall's top");
        Assert.InRange(pd, overTop - 0.05f, overTop + 0.05f);
        Assert.True(Vector3.Distance(Db3(after), Db3(fromTiles)) < 0.1f, $"the box list {Db3(after)}, the triangle store {Db3(fromTiles)}");
    }

    /// <summary>
    /// What Steam Audio gives for the hill of <see cref="A_hill_between_is_gone_over_its_crest"/>, grassed and
    /// bare: its ground is the surface's faces, each with its cover's figure, and the porous covers' figures do
    /// not depend on thickness, so the line went in and out of the hill for a few decibels. The worker's
    /// correction makes the earth pass nothing, and what it hears is the way over the crest. Opt-in (Steam Audio).
    /// </summary>
    [SteamAudioEmbreeFact]
    public void Steam_Audio_hears_a_hill_through_its_surface_and_the_correction_closes_it()
    {
        IntPtr ctx = SteamAudioForTests.EmbreeContext;
        AcousticRegistry.Initialize();
        var src = new Vector3(3f, 0.5f, -50f);
        var ear = new Vector3(3f, 1.6f, 50f);
        foreach (string cover in new[] { "Grass", "Dirt", "Asphalt" })
        {
            const int posts = 125;
            var heights = new float[posts * posts];
            for (int j = 0; j < posts; j++)
                for (int i = 0; i < posts; i++)
                    heights[j * posts + i] = Ridge(-124f + j * 2f, 0f, 4f);
            var field = new Heightfield(posts, 2f, heights, null, new[] { cover });
            var ground = SolidSpec.OfTerrain(1, Vector3.Zero, field, OpenFPS.Audio.SteamAudio.SceneTerrain.Surface);
            using var scene = new OpenFPS.Audio.SteamAudio.SteamAudioScene(ctx);
            scene.Build(Array.Empty<OpenFPS.Audio.SteamAudio.SteamAudioScene.Box>(), new[] { ground });
            using var sim = new OpenFPS.Audio.SteamAudio.SteamAudioSimulator(ctx, maxSources: 2);
            sim.SetScene(scene);
            var source = sim.AcquireSource();
            sim.SetListener(ear);
            sim.SetSourceInputs(source, src, 0.1f);
            sim.Run();
            var raw = sim.GetResult(source);
            var routes = Routes(Array.Empty<(Vector3, Vector3, string)>(), ground);
            var k = routes.SimulatorCorrection(src, ear);
            var corrected = raw with { TransLow = raw.TransLow * k.X, TransMid = raw.TransMid * k.Y, TransHigh = raw.TransHigh * k.Z };
            var through = OpenFPS.Audio.SteamAudio.SteamAudioSimulator.ToAcousticParams(raw);
            var closed = OpenFPS.Audio.SteamAudio.SteamAudioSimulator.ToAcousticParams(corrected);
            var over = routes.BarrierGains(src, ear, WallTransmission.SoundSpeed, out _, out _, out _, out bool found);
            _o.WriteLine($"{cover}: visibility {raw.Visibility:F3}, through {Db(through.EqLow):F1}/{Db(through.EqMid):F1}/{Db(through.EqHigh):F1} dB; "
                       + $"the earth closed {Db(closed.EqLow):F1}/{Db(closed.EqMid):F1}/{Db(closed.EqHigh):F1}; over the crest {Db3(over)}");
            Assert.True(found);
            Assert.True(raw.Visibility < 0.5f, $"{cover}: Steam Audio sees through the hill ({raw.Visibility:F2})");
            Assert.True(closed.EqMid <= raw.Visibility + 1e-4f, $"{cover}: the corrected line still passes the earth");
            Assert.True(Db(over.Y) > Db(closed.EqMid), $"{cover}: over the crest is no louder than the line");
        }
    }

    /// <summary>With no ground, nothing is through the earth: the flat maps' answers are what they were.</summary>
    [Fact]
    public void Without_terrain_nothing_is_through_the_ground()
    {
        AcousticRegistry.EnsureInitialized();
        var routes = OpeningRoutes.Build(new List<OpeningRoutes.Solid> { new(new Vector3(0, 1, 0), new Vector3(4, 2, 0.2f), Quaternion.Identity, "Brick") },
                                         null, Array.Empty<OpeningRoutes.Declared>(), null, null);
        var src = new Vector3(0f, -5f, -20f);
        var ear = new Vector3(0f, -5f, 20f);
        Assert.False(routes.ThroughTheGround(src, ear));
        Assert.Equal(Vector3.One, routes.LegGains(src, ear, None, None));
        Assert.False(routes.OverTheGround(src, ear, WallTransmission.SoundSpeed, out _, out _, out _));
    }
}
