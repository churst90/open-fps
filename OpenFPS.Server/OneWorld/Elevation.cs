using System.Buffers.Binary;
using System.Globalization;

namespace OpenFPS.Server.OneWorld;

/// <summary>Where a tile's ground heights come from.</summary>
public interface IElevationSource
{
    /// <summary>What it is, for the tile's record and the licences.</summary>
    string Name { get; }

    /// <summary>
    /// The heights of a tile's posts (<paramref name="posts"/> a side, <paramref name="spacing"/> apart from
    /// its south-west corner), metres over the sea, row by row from the south; NaN where the survey has
    /// nothing. Null when the survey covers none of it. Throws when it cannot be asked (no network): that
    /// is not "nothing here", and the tile is tried again later.
    /// </summary>
    Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct);

    /// <summary>
    /// The same for any square of a zone, its first post at (<paramref name="west"/>, <paramref name="south"/>):
    /// a tile and the margin round it (WorldFeatures). A source that cannot is answered for the tile alone,
    /// held at its edge.
    /// </summary>
    Task<float[]?> WindowAsync(int zone, bool north, double west, double south, int posts, double spacing, CancellationToken ct)
        => throw new NotSupportedException();
}

/// <summary>
/// USGS 3DEP (public domain), asked for one tile at a time in the tile's own UTM metres: its ImageServer
/// resamples the best survey it holds (1 m lidar where there is some) to a grid whose cells' middles are
/// the tile's posts. The answer is a float TIFF, read here and thrown away; nothing downloaded is kept.
/// Requests carry a generic User-Agent and nothing that says who runs the server.
/// </summary>
public sealed class Usgs3Dep : IElevationSource
{
    public string Name => "USGS 3D Elevation Program (public domain)";

    private const string Service = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("openfps-world/0.1");
        return c;
    }

    public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
        => WindowAsync(key.Zone, key.North, key.Easting, key.Northing, posts, spacing, ct);

    public async Task<float[]?> WindowAsync(int zone, bool north, double west, double south, int posts, double spacing, CancellationToken ct)
    {
        // Cells as wide as the spacing, centred on the posts: half a cell past the posts on every side.
        double half = spacing / 2;
        double w = west - half, s = south - half;
        double e = w + posts * spacing, n = s + posts * spacing;
        int sr = Utm.Epsg(zone, north);
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string url = $"{Service}?bbox={F(w)},{F(s)},{F(e)},{F(n)}&bboxSR={sr}&imageSR={sr}&size={posts},{posts}"
                     + "&format=tiff&pixelType=F32&interpolation=RSP_BilinearInterpolation&f=image";
        var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        var grid = FloatTiff.Read(bytes);
        if (grid.Width != posts || grid.Height != posts)
            throw new InvalidDataException($"3DEP answered {grid.Width} x {grid.Height} for {posts} x {posts}");
        // The TIFF's first row is the north; the posts' first row is the south.
        var heights = new float[posts * posts];
        bool any = false;
        for (int j = 0; j < posts; j++)
            for (int i = 0; i < posts; i++)
            {
                float v = grid.Values[(posts - 1 - j) * posts + i];
                bool valid = float.IsFinite(v) && v > -1000f && v < 9000f && (grid.NoData is not { } nd || v != nd);
                heights[j * posts + i] = valid ? v : float.NaN;
                any |= valid;
            }
        return any ? heights : null;
    }
}

/// <summary>A server that makes no new tiles (world.json "Generate": false): only tiles already stored are
/// served, and a player is stopped at the edge of the rest.</summary>
public sealed class NoNewTiles : IElevationSource
{
    public string Name => "none";

    public Task<float[]?> HeightsAsync(WorldTileKey key, int posts, double spacing, CancellationToken ct)
        => throw new InvalidOperationException("this server makes no new tiles (world.json Generate is false)");
}

/// <summary>
/// The part of TIFF the elevation service answers with: one band of 32-bit floats, uncompressed, in strips or
/// tiles, either byte order; the GDAL no-data tag if present.
/// </summary>
public static class FloatTiff
{
    public readonly record struct Grid(int Width, int Height, float[] Values, float? NoData);

    public static Grid Read(byte[] b)
    {
        if (b.Length < 8) throw new InvalidDataException("not a TIFF");
        bool le = b[0] == 'I' && b[1] == 'I';
        if (!le && !(b[0] == 'M' && b[1] == 'M')) throw new InvalidDataException("not a TIFF");
        ushort U16(int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
        uint U32(int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        if (U16(2) != 42) throw new InvalidDataException("not a classic TIFF");
        int ifd = (int)U32(4);
        int count = U16(ifd);
        int width = 0, height = 0, bits = 0, format = 1, compression = 1, samples = 1;
        int tileW = 0, tileH = 0, rowsPerStrip = int.MaxValue;
        uint[] offsets = Array.Empty<uint>(), lengths = Array.Empty<uint>();
        float? noData = null;
        uint[] Values(int entry)
        {
            ushort type = U16(entry + 2);
            int n = (int)U32(entry + 4);
            int size = type == 3 ? 2 : 4;
            int at = n * size <= 4 ? entry + 8 : (int)U32(entry + 8);
            var v = new uint[n];
            for (int k = 0; k < n; k++) v[k] = type == 3 ? U16(at + 2 * k) : U32(at + 4 * k);
            return v;
        }
        for (int k = 0; k < count; k++)
        {
            int entry = ifd + 2 + 12 * k;
            ushort tag = U16(entry);
            switch (tag)
            {
                case 256: width = (int)Values(entry)[0]; break;
                case 257: height = (int)Values(entry)[0]; break;
                case 258: bits = (int)Values(entry)[0]; break;
                case 259: compression = (int)Values(entry)[0]; break;
                case 277: samples = (int)Values(entry)[0]; break;
                case 278: rowsPerStrip = (int)Values(entry)[0]; break;
                case 273: case 324: offsets = Values(entry); break;
                case 279: case 325: lengths = Values(entry); break;
                case 322: tileW = (int)Values(entry)[0]; break;
                case 323: tileH = (int)Values(entry)[0]; break;
                case 339: format = (int)Values(entry)[0]; break;
                case 42113:
                {
                    int n = (int)U32(entry + 4);
                    int at = n <= 4 ? entry + 8 : (int)U32(entry + 8);
                    var text = System.Text.Encoding.ASCII.GetString(b, at, Math.Max(0, n - 1)).Trim('\0', ' ');
                    if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float nd)) noData = nd;
                    break;
                }
            }
        }
        if (compression != 1) throw new InvalidDataException($"TIFF compression {compression} is not read here");
        if (bits != 32 || format != 3 || samples != 1) throw new InvalidDataException("not one band of 32-bit floats");
        if (width <= 0 || height <= 0 || offsets.Length == 0) throw new InvalidDataException("a TIFF with no picture");
        var values = new float[width * height];
        float F32(int o) => BitConverter.Int32BitsToSingle((int)U32(o));
        if (tileW > 0 && tileH > 0)
        {
            int across = (width + tileW - 1) / tileW;
            for (int t = 0; t < offsets.Length; t++)
            {
                int tx = t % across * tileW, ty = t / across * tileH;
                int at = (int)offsets[t];
                for (int y = 0; y < tileH; y++)
                    for (int x = 0; x < tileW; x++)
                    {
                        int gx = tx + x, gy = ty + y;
                        if (gx < width && gy < height) values[gy * width + gx] = F32(at + 4 * (y * tileW + x));
                    }
            }
        }
        else
        {
            int row = 0;
            for (int s = 0; s < offsets.Length && row < height; s++)
            {
                int at = (int)offsets[s];
                int rows = Math.Min(rowsPerStrip, height - row);
                for (int y = 0; y < rows; y++, row++)
                    for (int x = 0; x < width; x++) values[row * width + x] = F32(at + 4 * (y * width + x));
            }
        }
        return new Grid(width, height, values, noData);
    }
}
