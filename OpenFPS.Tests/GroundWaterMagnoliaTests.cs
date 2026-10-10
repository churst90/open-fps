using System.Diagnostics;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Server.Water;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Water over Magnolia's real ground (docs/RUNNING_WATER.md 13): the map loaded as the server loads it, its
/// drainage worked out at load, its creek and roadside ditches given voices, their flows through a storm, and
/// the place's copies in the world draining as the map does.
/// </summary>
[Collection("GroundWater")]
public class GroundWaterMagnoliaTests : IDisposable
{
    private const string Id = "magnolia_tx";
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-groundwater-magnolia-" + Guid.NewGuid().ToString("N"));

    public GroundWaterMagnoliaTests(ITestOutputHelper o) { _o = o; GroundWaterSystem.Reset(); }

    public void Dispose()
    {
        GroundWaterSystem.Reset();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private MapManager Load()
    {
        string maps = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(Path.Combine(maps, "places"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "places", Id + ".json"), Path.Combine(maps, "places", Id + ".json"));
        RealPlaceMapTests.CopyGround(Id, Path.Combine(maps, "places"));
        AcousticRegistry.Initialize();
        var mm = new MapManager(new MapRepository(maps), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        mm.Initialize();
        return mm;
    }

    [Fact]
    public void Magnolia_drains_to_its_creeks_and_ditches_and_they_run_in_a_storm()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "places", Id + ".json"))) return;
        var maps = Load();
        var net = GroundWaterSystem.NetworkOf(Id);
        Assert.NotNull(net);
        _o.WriteLine($"{net!.Tiles.Count} tiles; built in {net.BuildTime.TotalMilliseconds:F0} ms; {net.Lines.Count} lines ({net.Lines.Sum(l => l.LengthMetres) / 1000:F1} km); " +
                     $"{net.Ponds.Count} ponds ({net.Ponds.Count(p => p.IsPuddle)} puddles) of {net.Hollows} hollows, {net.Culverts} culverts; {net.Voices.Count} voices");
        Assert.Equal(196, net.Tiles.Count);
        Assert.Equal(0, net.LoopsBroken);
        Assert.True(net.Lines.Count > 100);
        foreach (var kind in Enum.GetValues<GroundChannelKind>()) Assert.Contains(net.Voices, v => v.Kind == kind);
        Assert.Contains(net.Ponds, p => p.IsPuddle);
        Assert.True(net.Culverts > 0);
        // The biggest catchment within the place is a creek of a few square kilometres.
        float biggest = net.Lines.Max(l => l.SquareMetres);
        Assert.InRange(biggest, 1e6f, 2e7f);

        // Every voice is a thing of the map on the runoff layer, with a sound a client can read.
        Assert.True(maps.TryGetMap(Id, out var world, out _, out _, out _));
        int voices = 0;
        world.Query(new QueryDescription().WithAll<SoundEmitterComponent>(), (ref SoundEmitterComponent s) =>
        {
            if (!s.SoundId.StartsWith("flow:ground/", StringComparison.Ordinal)) return;
            voices++;
            var spec = RunningWaterSpec.ByName(s.SoundId[5..]);
            Assert.NotNull(spec.Ground);
            Assert.InRange(spec.SourceLevelDb, 30f, 100f);
        });
        Assert.Equal(net.Voices.Count, voices);

        // Through a storm: the biggest creek's voice and the ditch with the most road draining into it.
        var creek = net.Voices.Where(v => v.Kind == GroundChannelKind.Creek).OrderByDescending(v => v.SquareMetres).First();
        var ditch = net.Voices.Where(v => v.Kind == GroundChannelKind.Ditch).OrderByDescending(v => v.Catchment.AreaSquareMetres[(int)GroundSurface.Impervious]).First();
        GroundWaterSystem.Settle(Id, 0f);
        float creekDry = GroundWaterSystem.FlowAt(Id, creek.Position), ditchDry = GroundWaterSystem.FlowAt(Id, ditch.Position);
        var tick = Stopwatch.StartNew();
        for (int t = 0; t < 600; t++) GroundWaterSystem.Update(Id, Rainfall.HeavyRate, 0.05f, 1f);
        _o.WriteLine($"a second of the map's ground water (the ladders, {net.Ponds.Count} ponds): {tick.Elapsed.TotalMilliseconds / 600:F2} ms");
        float creekWet = GroundWaterSystem.FlowAt(Id, creek.Position), ditchWet = GroundWaterSystem.FlowAt(Id, ditch.Position);
        for (int t = 0; t < 1800; t++) GroundWaterSystem.Update(Id, 0f, 0.05f, 1f);
        float creekAfter = GroundWaterSystem.FlowAt(Id, creek.Position), ditchAfter = GroundWaterSystem.FlowAt(Id, ditch.Position);
        _o.WriteLine($"creek ({creek.SquareMetres / 1e4:F0} ha): {creekDry:F2} dry, {creekWet:F2} after 10 min of 25 mm/h, {creekAfter:F2} L/s 30 min after");
        _o.WriteLine($"ditch ({ditch.SquareMetres / 1e4:F1} ha): {ditchDry:F2} dry, {ditchWet:F2}, {ditchAfter:F2} L/s");
        Assert.True(creekDry > 1f, "a creek of a few square kilometres carries base flow in dry weather");
        Assert.Equal(0f, ditchDry);
        Assert.True(creekWet > 3f * creekDry);
        Assert.True(ditchWet > 1f);
        Assert.InRange(creekAfter, creekDry, creekWet);
        Assert.InRange(ditchAfter, 0f, 0.5f * ditchWet);

        // Where the creek is, how wet it is, what reaches a patch on its bank.
        var wet = GroundWaterSystem.WetnessAt(Id, creek.Position);
        Assert.True(wet.FlowLitresPerSecond > 0f && wet.WaterDepthMm > 0f);
    }

    /// <summary>The place's tiles in the world drain as the map does: the same posts routed whole, the tiles cut
    /// on the world's grid instead of the map's.</summary>
    [Fact]
    public void Magnolia_s_world_tiles_drain_as_its_map()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "places", Id + ".json"))) return;
        var maps = Load();
        var net = GroundWaterSystem.NetworkOf(Id)!;
        var clock = Stopwatch.StartNew();
        var places = WorldPlaces.FromMaps(maps);
        var place = Assert.Single(places.Places);
        int same = 0, compared = 0, tiles = 0;
        for (int x = place.Min.X + 3; x <= place.Min.X + 5; x++)
            for (int z = place.Min.Z + 3; z <= place.Min.Z + 5; z++)
            {
                var t = places.Make(new WorldTileKey(place.Zone, place.North, x, z))!;
                Assert.NotNull(t.Drainage);
                tiles++;
                for (int j = 0; j < 125; j += 3)
                    for (int i = 0; i < 125; i += 3)
                    {
                        // The cell's middle in the map's metres.
                        float mx = (float)(x * WorldTileKey.TileMetres + (i + 0.5) * 2 - place.Easting);
                        float mz = (float)(z * WorldTileKey.TileMetres + (j + 0.5) * 2 - place.Northing);
                        int c = net.CellAt(mx, mz);
                        if (c < 0) continue;
                        var centre = net.Centre(c);
                        if (MathF.Abs(centre.X - mx) > 0.01f || MathF.Abs(centre.Z - mz) > 0.01f) continue;
                        compared++;
                        int n = net.Next(c);
                        byte mine = t.Drainage!.Flow[j * 125 + i];
                        if (n < 0) continue;
                        var to = net.Centre(n);
                        int dx = (int)MathF.Round((to.X - centre.X) / 2f), dz = (int)MathF.Round((to.Z - centre.Z) / 2f);
                        if (mine < 8 && Drainage.Dx[mine] == dx && Drainage.Dz[mine] == dz) same++;
                    }
            }
        _o.WriteLine($"{tiles} tiles of the place in the world ({clock.ElapsedMilliseconds} ms with the copy): {same} of {compared} cells drain the way the map's do");
        Assert.True(compared > 1000, $"{compared} cells on the same grid");
        Assert.True(same > 0.97 * compared, $"{same} of {compared}");
    }
}
