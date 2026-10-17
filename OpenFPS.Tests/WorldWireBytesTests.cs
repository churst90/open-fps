using System.Numerics;
using System.Security.Cryptography;
using MemoryPack;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// What the world's values are on the wire, byte for byte, as they were before package stage 4 moved them
/// into OpenFPS.World (docs/ENGINE_PACKAGES.md, stage 4): an entity's definition with its type and a tile of
/// ground, a tile stream's update with its tiles' states, and the city's roads message. The expected hashes
/// were written on main `cedb4677`, before any move; components serialise positionally, so a type moving
/// between assemblies must not move a byte (memory "component-wire-format").
/// </summary>
public class WorldWireBytesTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static EntityDefinition Definition()
    {
        var d = new EntityDefinition
        {
            EntityId = 4211,
            Type = EntityType.Item,
            Moves = true,
            Team = "red",
            RidingEntityId = 77,
            Terrain = new TerrainTileComponent
            {
                Posts = 3, Spacing = 2f, HeightsCm = new short[] { 0, 5, -3, 12, 40, 7, 1, 2, 3 },
                Cells = new byte[] { 0, 1, 1, 0 }, Materials = new[] { "Dirt", "Grass" },
            },
        };
        d.Transform = new Transform { Position = new Vector3(1.5f, -2.25f, 300f), Rotation = Quaternion.CreateFromYawPitchRoll(0.3f, 0f, 0f) };
        d.Identity.Name = "Lamp";
        return d;
    }

    /// <summary>Every value of the type an entity is, alone and inside its definition sent as a message.</summary>
    [Fact]
    public void AnEntitysTypeIsTheSameBytes()
    {
        var types = Enum.GetValues<EntityType>().Select(t => Convert.ToHexString(MemoryPackSerializer.Serialize(t)));
        Assert.Equal("00000000,01000000,02000000,03000000,04000000,05000000,06000000,07000000", string.Join(",", types));
        Assert.Equal(Expected.Definition, Sha(MemoryPackSerializer.Serialize<IMessage>(Definition())));
    }

    [Fact]
    public void ATileStreamsUpdateIsTheSameBytes()
    {
        var u = new TileStreamUpdate
        {
            TileMetres = 250f, Definitions = 12, Removed = 3,
            Tiles = { new TileState(new TileKey(-2, 5), TileDetail.Coarse), new TileState(new TileKey(0, 0), TileDetail.Full) },
        };
        Assert.Equal(Expected.TileStream, Sha(MemoryPackSerializer.Serialize<IMessage>(u)));
    }

    /// <summary>The city's roads as a client is sent them (the MapRoads message's text), crossings left out.</summary>
    [Fact]
    public void TheCitysRoadsMessageIsTheSameText()
    {
        var city = MapRepository.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "city.json"))!;
        string json = MapRoadsBuilder.Build(city, Array.Empty<CrossingRails>()).ToJson();
        Assert.Equal(Expected.CityRoads, Sha(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private static class Expected
    {
        // 2026-10-17: SoundEmitterComponent.Line appended (a source laid along a line of its own, docs/RUNNING_WATER.md
        // 15.6): the emitter has one member more, null here. 2026-10-17: PortalComponent.GapMetres and UndercutMetres
        // appended (a shut door's gaps, DoorGaps): the portal has two floats more, zero here.
        public const string Definition = "4772c8d9af12df4d612161903f654387b8fe217f472c69b21be5fe04798e092b";
        public const string TileStream = "e40761c663bb9337d817ce3814e979f91896f28cf198bc06672be7f637419f69";
        public const string CityRoads = "1794c6f218491f38537875c63a3837ffb35756886a78541cc2ee98a730042a65";
    }
}
