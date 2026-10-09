using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// A map's ground heights from a survey (USGS 3DEP for the real places), on a grid of the map's own
/// metres: <see cref="Columns"/> by <see cref="Rows"/> posts <see cref="Spacing"/> apart from
/// (<see cref="OriginX"/>, <see cref="OriginZ"/>), each a whole number of centimetres over
/// <see cref="BaseY"/>, row by row from the south-west. tools/gen_osm.py writes it and sets everything on
/// the same heights read the same way (its ground()), so the terrain laid from it and the map agree.
///
/// <para>The posts are in <see cref="Centimetres"/> (little-endian 16-bit in base64), or for a real place in
/// <see cref="File"/>: tools/places/ID/elevation.json shipped beside the map as it is, its posts 2 m apart on
/// the world's UTM grid (<see cref="Encoding"/> "zlib-row-delta": each row less the row before, deflated),
/// its heights over its own base; <see cref="BaseY"/> is that base in the map's heights.</para>
/// </summary>
public sealed class MapElevation
{
    public double OriginX { get; set; }
    public double OriginZ { get; set; }
    public double Spacing { get; set; } = 5.0;
    public int Columns { get; set; }
    public int Rows { get; set; }
    public double BaseY { get; set; }
    /// <summary>The map's height of the sea: the survey's heights are this much over it.</summary>
    public double SeaLevelY { get; set; }
    public string Centimetres { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>The file beside the map that holds the posts, or null when <see cref="Centimetres"/> does.</summary>
    public string? File { get; set; }
    /// <summary>How the posts are packed: empty for plain, or "zlib-row-delta".</summary>
    public string? Encoding { get; set; }

    private short[]? _posts;

    /// <summary>Reads <see cref="File"/> from <paramref name="directory"/> (the map's): its posts become this
    /// grid's. Throws when it is not there or not the grid the map says.</summary>
    public void Resolve(string directory)
    {
        if (string.IsNullOrEmpty(File) || _posts != null) return;
        string path = Path.Combine(directory, Path.GetFileName(File));
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var r = doc.RootElement;
        int cols = r.GetProperty("cols").GetInt32(), rows = r.GetProperty("rows").GetInt32();
        if (cols != Columns || rows != Rows)
            throw new InvalidDataException($"elevation: {path} has {cols} x {rows} posts, the map says {Columns} x {Rows}");
        Encoding = r.TryGetProperty("encoding", out var enc) ? enc.GetString() : null;
        Centimetres = r.GetProperty("data").GetString() ?? "";
        _ = Posts;
        Centimetres = "";
    }

    /// <summary>The posts, decoded once. Throws when there are not Columns x Rows of them.</summary>
    [JsonIgnore]
    public short[] Posts
    {
        get
        {
            if (_posts != null) return _posts;
            var bytes = Convert.FromBase64String(Centimetres);
            if (Encoding == "zlib-row-delta")
            {
                using var z = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var unpacked = new MemoryStream();
                z.CopyTo(unpacked);
                bytes = unpacked.ToArray();
            }
            else if (!string.IsNullOrEmpty(Encoding)) throw new InvalidDataException($"elevation: no such encoding '{Encoding}'");
            if (Columns < 2 || Rows < 2 || bytes.Length != Columns * Rows * 2)
                throw new InvalidDataException($"elevation: {bytes.Length} bytes for {Columns} x {Rows} posts");
            var posts = new short[Columns * Rows];
            for (int k = 0; k < posts.Length; k++) posts[k] = (short)(bytes[2 * k] | (bytes[2 * k + 1] << 8));
            if (Encoding == "zlib-row-delta")
                for (int k = Columns; k < posts.Length; k++) posts[k] = (short)(posts[k] + posts[k - Columns]);
            return _posts = posts;
        }
    }

    /// <summary>The ground's height at (x, z), bilinear between posts and held at the grid's edge: exactly
    /// gen_osm's ground(), in doubles.</summary>
    public double HeightAt(double x, double z)
    {
        var p = Posts;
        double fx = (x - OriginX) / Spacing, fz = (z - OriginZ) / Spacing;
        int i = Math.Min(Math.Max((int)Math.Floor(fx), 0), Columns - 2);
        int j = Math.Min(Math.Max((int)Math.Floor(fz), 0), Rows - 2);
        double tx = Math.Min(Math.Max(fx - i, 0.0), 1.0);
        double tz = Math.Min(Math.Max(fz - j, 0.0), 1.0);
        int k = j * Columns + i;
        double h00 = BaseY + p[k] * 0.01, h10 = BaseY + p[k + 1] * 0.01;
        double h01 = BaseY + p[k + Columns] * 0.01, h11 = BaseY + p[k + Columns + 1] * 0.01;
        return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz;
    }
}
