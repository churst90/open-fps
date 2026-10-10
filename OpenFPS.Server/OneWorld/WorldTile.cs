using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// One tile of the world as the store keeps it (docs/WORLD_STREAMING.md, Persistence): its ground, and
/// whatever stands on it as map entities, in the tile's own metres (x east and z north from its south-west
/// corner, y metres over the sea). Written once by the generator and read whenever the tile is wanted.
/// </summary>
public sealed class WorldTile
{
    public string Key { get; set; } = "";
    /// <summary>The generator that made it (<see cref="WorldStore.GeneratorVersion"/>).</summary>
    public int Generator { get; set; }
    public DateTime MadeUtc { get; set; }
    /// <summary>Where its ground came from, for the licences ("USGS 3DEP", or "none" for open ground
    /// where nothing was surveyed).</summary>
    public string Source { get; set; } = "";
    /// <summary>Where its ground's materials came from, with the attribution its licence asks for; null where
    /// there was no land cover (every cell dirt).</summary>
    public string? LandCover { get; set; }
    public TerrainData? Terrain { get; set; }
    /// <summary>What stands on it, as a map's entities, positions in the tile's own metres.</summary>
    public List<EntityData> Entities { get; set; } = new();
    /// <summary>The map of a real place it was copied from (WorldPlaces), or null for a tile made from the
    /// survey alone. A placed tile is never dropped by the store's cap.</summary>
    public string? Place { get; set; }
    /// <summary>Which version of that map it was copied from: a tile of an older one is copied again.</summary>
    public string? PlaceVersion { get; set; }

    /// <summary>A tile of ground: posts a side, spacing, heights in whole centimetres over BaseY (metres over
    /// the sea), row by row from the south-west, and a material per cell.</summary>
    public sealed class TerrainData
    {
        public int Posts { get; set; }
        public float Spacing { get; set; }
        public float BaseY { get; set; }
        public short[] HeightsCm { get; set; } = Array.Empty<short>();
        public byte[] Cells { get; set; } = Array.Empty<byte>();
        public string[] Materials { get; set; } = Array.Empty<string>();

        public TerrainTileComponent ToComponent() => new()
        {
            Posts = Posts, Spacing = Spacing, HeightsCm = HeightsCm, Cells = Cells, Materials = Materials,
        };
    }

    /// <summary>A map file's options (vectors and turns spelled as maps spell them), heights in base64,
    /// nothing written for what is not there.</summary>
    private static readonly JsonSerializerOptions Json = new(MapRepository.JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ShortsAsBase64() },
    };

    /// <summary>The tile as gzip-compressed JSON.</summary>
    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, this, Json);
        return ms.ToArray();
    }

    public static WorldTile FromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<WorldTile>(gz, Json) ?? throw new InvalidDataException("an empty tile");
    }

    /// <summary>Heights as little-endian 16-bit whole centimetres in base64, not a list of numbers: a third
    /// of the size before compression.</summary>
    private sealed class ShortsAsBase64 : JsonConverter<short[]>
    {
        public override short[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var bytes = Convert.FromBase64String(reader.GetString() ?? "");
            var v = new short[bytes.Length / 2];
            for (int k = 0; k < v.Length; k++) v[k] = (short)(bytes[2 * k] | (bytes[2 * k + 1] << 8));
            return v;
        }

        public override void Write(Utf8JsonWriter writer, short[] value, JsonSerializerOptions options)
        {
            var bytes = new byte[value.Length * 2];
            for (int k = 0; k < value.Length; k++) { bytes[2 * k] = (byte)value[k]; bytes[2 * k + 1] = (byte)(value[k] >> 8); }
            writer.WriteStringValue(Convert.ToBase64String(bytes));
        }
    }
}
