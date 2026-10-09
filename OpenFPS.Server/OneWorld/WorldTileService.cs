using System.Collections.Concurrent;
using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// Makes the world's tiles on demand (docs/WORLD_STREAMING.md, Stage 2: generating tiles on demand): a
/// tile wanted and not in the store is made in the background, at most <see cref="MaxAtOnce"/> at a time,
/// each given <see cref="Timeout"/>, and written to the store, which it is read from ever after. A tile
/// that could not be made (the survey could not be asked) is not stored and is tried again after
/// <see cref="RetryAfter"/>; one the survey covers none of is flat open ground at sea level, stored like
/// any other, so there is no invisible wall in the middle of nowhere.
/// </summary>
public sealed class WorldTileService
{
    public enum TileState { Unknown, Making, Ready, Failed }

    /// <summary>Posts a side of a tile's ground, and their spacing: 2 m (docs/GEOMETRY.md, decision 1).</summary>
    public const int Posts = 126;
    public const float Spacing = 2f;

    public WorldStore Store { get; }
    public IElevationSource Elevation { get; }
    public int MaxAtOnce { get; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a tile is loaded by somebody: the store's cap never drops one that is.</summary>
    public Func<WorldTileKey, bool>? InUse { get; set; }

    /// <summary>A tile was made and stored. Raised on the generator's thread.</summary>
    public event Action<WorldTileKey>? Made;

    public int MadeCount => _made;
    public int FailedCount => _failed;

    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<WorldTileKey, Task<WorldTile?>> _pending = new();
    private readonly ConcurrentDictionary<WorldTileKey, DateTime> _failedUntil = new();
    private int _made, _failed;

    public WorldTileService(WorldStore store, IElevationSource elevation, int maxAtOnce = 2)
    {
        Store = store;
        Elevation = elevation;
        MaxAtOnce = Math.Max(1, maxAtOnce);
        _slots = new SemaphoreSlim(MaxAtOnce);
    }

    public TileState StateOf(WorldTileKey key)
    {
        if (_pending.ContainsKey(key)) return TileState.Making;
        if (Store.Has(key)) return TileState.Ready;
        if (_failedUntil.TryGetValue(key, out var until) && until > DateTime.UtcNow) return TileState.Failed;
        return TileState.Unknown;
    }

    /// <summary>Asks for a tile: made in the background if it is not stored, not being made, and not waiting
    /// out a failure. Never blocks.</summary>
    public void Want(WorldTileKey key) => _ = GetAsync(key);

    /// <summary>The tile, made if need be; null if it could not be made now.</summary>
    public Task<WorldTile?> GetAsync(WorldTileKey key)
    {
        if (_pending.TryGetValue(key, out var making)) return making;
        if (Store.TryRead(key, out var stored)) return Task.FromResult<WorldTile?>(stored);
        if (_failedUntil.TryGetValue(key, out var until) && until > DateTime.UtcNow) return Task.FromResult<WorldTile?>(null);
        var done = new TaskCompletionSource<WorldTile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, done.Task)) return _pending.TryGetValue(key, out making) ? making : GetAsync(key);
        _ = Task.Run(async () =>
        {
            var tile = await MakeAsync(key).ConfigureAwait(false);
            // Out of the pending list before anyone hears it is done, so a new ask sees the store.
            _pending.TryRemove(key, out _);
            done.SetResult(tile);
        });
        return done.Task;
    }

    private async Task<WorldTile?> MakeAsync(WorldTileKey key)
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Store.TryRead(key, out var stored)) return stored;
            if (!Store.TryLock(key))
            {
                // Another server is making it: wait for its file, as long as a making may take.
                var deadline = DateTime.UtcNow + Timeout;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    if (Store.TryRead(key, out stored)) return stored;
                }
                throw new TimeoutException("another server holds its lock");
            }
            try
            {
                using var cts = new CancellationTokenSource(Timeout);
                var heights = await Elevation.HeightsAsync(key, Posts, Spacing, cts.Token).ConfigureAwait(false);
                var tile = Make(key, heights, heights == null ? "none" : Elevation.Name);
                Store.Write(key, tile, InUse);
                Interlocked.Increment(ref _made);
                _failedUntil.TryRemove(key, out _);
                Made?.Invoke(key);
                return tile;
            }
            finally { Store.Unlock(key); }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            _failedUntil[key] = DateTime.UtcNow + RetryAfter;
            Log.Warning("World: tile {Key} could not be made ({Error}); tried again in {Seconds:F0} s.", key, ex.Message, RetryAfter.TotalSeconds);
            return null;
        }
        finally { _slots.Release(); }
    }

    /// <summary>A tile from its posts' heights (null: the survey has nothing here, flat open ground at sea
    /// level), every cell dirt. Posts the survey misses are at sea level too.</summary>
    public static WorldTile Make(WorldTileKey key, float[]? heights, string source)
    {
        var h = new float[Posts * Posts];
        if (heights != null)
            for (int k = 0; k < h.Length; k++) h[k] = float.IsFinite(heights[k]) ? heights[k] : 0f;
        float lo = float.MaxValue;
        foreach (float v in h) lo = MathF.Min(lo, v);
        float baseY = MathF.Floor(lo * 100f) / 100f;
        return new WorldTile
        {
            Key = key.ToString(),
            Generator = WorldStore.GeneratorVersion,
            MadeUtc = DateTime.UtcNow,
            Source = source,
            Terrain = new WorldTile.TerrainData
            {
                Posts = Posts, Spacing = Spacing, BaseY = baseY,
                HeightsCm = OpenFPS.Common.Geometry.Heightfield.ToCentimetres(h, baseY),
                Cells = new byte[(Posts - 1) * (Posts - 1)],
                Materials = new[] { "Dirt" },
            },
        };
    }
}
