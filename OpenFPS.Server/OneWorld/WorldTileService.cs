using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// Makes the world's tiles on demand (docs/WORLD_STREAMING.md, Stage 2: generating tiles on demand): a
/// tile wanted and not in the store is queued, made in the background at most <see cref="MaxAtOnce"/> at a
/// time, each given <see cref="Timeout"/>, and written to the store, which it is read from ever after.
/// The queue is taken in order of when somebody could reach each tile (seconds, soonest first), so the
/// tiles ahead of a driver are made before the ones beside or behind them, and a tile nobody is on their
/// way to (<see cref="Background"/>) waits for every tile a player needs. A tile that could not be made
/// (the survey could not be asked) is not stored and is tried again after <see cref="RetryAfter"/>; one the
/// survey covers none of is flat open ground at sea level, stored like any other, so there is no invisible
/// wall in the middle of nowhere.
/// </summary>
public sealed class WorldTileService
{
    public enum TileState { Unknown, Making, Ready, Failed }

    /// <summary>Posts a side of a tile's ground, and their spacing: 2 m (docs/GEOMETRY.md, decision 1).</summary>
    public const int Posts = 126;
    public const float Spacing = 2f;

    /// <summary>The priority of a tile nobody is on their way to (the rings built at start): after every
    /// tile a player could reach; what is added to it orders them among themselves.</summary>
    public const double Background = 1e6;

    public WorldStore Store { get; }
    public IElevationSource Elevation { get; }
    public int MaxAtOnce { get; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a tile is loaded by somebody: the store's cap never drops one that is.</summary>
    public Func<WorldTileKey, bool>? InUse { get; set; }

    /// <summary>Makes a tile some other way than from the survey, or null to leave it to the survey: a tile
    /// of a real place copied from its map (WorldPlaces).</summary>
    public Func<WorldTileKey, WorldTile?>? Placed { get; set; }

    /// <summary>A tile was made and stored. Raised on the generator's thread.</summary>
    public event Action<WorldTileKey>? Made;

    public int MadeCount => _made;
    public int FailedCount => _failed;

    /// <summary>Tiles waiting for their turn.</summary>
    public int QueuedCount { get { lock (_gate) return _queued.Count; } }

    /// <summary>Tiles being made now.</summary>
    public int MakingCount { get { lock (_gate) return _making.Count; } }

    private sealed class Queued
    {
        public double Priority;
        public readonly TaskCompletionSource<WorldTile?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly Dictionary<WorldTileKey, Queued> _queued = new();
    private readonly Dictionary<WorldTileKey, Task<WorldTile?>> _making = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<WorldTileKey, DateTime> _failedUntil = new();
    private int _made, _failed;

    public WorldTileService(WorldStore store, IElevationSource elevation, int maxAtOnce = 2)
    {
        Store = store;
        Elevation = elevation;
        MaxAtOnce = Math.Max(1, maxAtOnce);
    }

    public TileState StateOf(WorldTileKey key)
    {
        lock (_gate)
            if (_queued.ContainsKey(key) || _making.ContainsKey(key)) return TileState.Making;
        if (Store.Has(key)) return TileState.Ready;
        if (_failedUntil.TryGetValue(key, out var until) && until > DateTime.UtcNow) return TileState.Failed;
        return TileState.Unknown;
    }

    /// <summary>Asks for a tile: queued if it is not stored, not being made, and not waiting out a failure;
    /// already queued, it keeps the sooner of its two priorities. Never blocks.</summary>
    public void Want(WorldTileKey key, double priority = Background)
    {
        lock (_gate)
        {
            if (_queued.TryGetValue(key, out var q)) { q.Priority = Math.Min(q.Priority, priority); return; }
            if (_making.ContainsKey(key)) return;
        }
        if (Store.Has(key)) return;
        _ = Ask(key, priority);
    }

    /// <summary>
    /// This pass's seconds to reach each tile somebody wants: a queued tile in it takes that priority, a
    /// queued tile no longer in it goes back to <see cref="Background"/> (still made, after the rest), and
    /// a wanted tile not queued yet is queued.
    /// </summary>
    public void Rank(IReadOnlyDictionary<WorldTileKey, double> wanted)
    {
        var missing = new List<KeyValuePair<WorldTileKey, double>>();
        lock (_gate)
        {
            foreach (var (k, q) in _queued)
                q.Priority = wanted.TryGetValue(k, out double p) ? p : Math.Max(q.Priority, Background);
            foreach (var kv in wanted)
                if (!_queued.ContainsKey(kv.Key) && !_making.ContainsKey(kv.Key)) missing.Add(kv);
        }
        foreach (var (k, p) in missing) Want(k, p);
    }

    /// <summary>The tile, made if need be; null if it could not be made now.</summary>
    public Task<WorldTile?> GetAsync(WorldTileKey key) => Ask(key, Background);

    private Task<WorldTile?> Ask(WorldTileKey key, double priority)
    {
        lock (_gate)
        {
            if (_queued.TryGetValue(key, out var q)) { q.Priority = Math.Min(q.Priority, priority); return q.Done.Task; }
            if (_making.TryGetValue(key, out var making)) return making;
        }
        if (Store.TryRead(key, out var stored)) return Task.FromResult<WorldTile?>(stored);
        if (_failedUntil.TryGetValue(key, out var until) && until > DateTime.UtcNow) return Task.FromResult<WorldTile?>(null);
        Task<WorldTile?> task;
        lock (_gate)
        {
            if (_queued.TryGetValue(key, out var q)) return q.Done.Task;
            if (_making.TryGetValue(key, out var making)) return making;
            var added = new Queued { Priority = priority };
            _queued[key] = added;
            task = added.Done.Task;
        }
        Pump();
        return task;
    }

    /// <summary>Starts the soonest queued tiles while there is room to make them.</summary>
    private void Pump()
    {
        while (true)
        {
            WorldTileKey key = default;
            Queued? next = null;
            lock (_gate)
            {
                if (_making.Count >= MaxAtOnce || _queued.Count == 0) return;
                foreach (var (k, q) in _queued)
                    if (next == null || q.Priority < next.Priority
                        || (q.Priority == next.Priority && (k.X, k.Z).CompareTo((key.X, key.Z)) < 0))
                    { key = k; next = q; }
                _queued.Remove(key);
                _making[key] = next!.Done.Task;
            }
            var made = key;
            var done = next!.Done;
            _ = Task.Run(async () =>
            {
                WorldTile? tile = null;
                try { tile = await MakeAsync(made).ConfigureAwait(false); }
                finally
                {
                    // Out of the list before anyone hears it is done, so a new ask sees the store.
                    lock (_gate) _making.Remove(made);
                    done.TrySetResult(tile);
                    Pump();
                }
            });
        }
    }

    private async Task<WorldTile?> MakeAsync(WorldTileKey key)
    {
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
                var tile = Placed?.Invoke(key);
                if (tile == null)
                {
                    using var cts = new CancellationTokenSource(Timeout);
                    var heights = await Elevation.HeightsAsync(key, Posts, Spacing, cts.Token).ConfigureAwait(false);
                    tile = Make(key, heights, heights == null ? "none" : Elevation.Name);
                }
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
