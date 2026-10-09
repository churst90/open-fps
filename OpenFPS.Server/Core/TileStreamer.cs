using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Server.Core;

/// <summary>
/// What one client has of a streamed map: the detail of each tile it holds, how far it asked to see,
/// and the definitions still owed to it. Lives on the session. See <see cref="TileStreamer"/>.
/// </summary>
public sealed class TileInterest
{
    /// <summary>Each tile the client has, and how much of it. Tiles it does not have are absent.</summary>
    public Dictionary<TileKey, TileDetail> Levels { get; private set; } = new();
    public StreamRadii Radii { get; set; } = StreamRadii.Default;
    /// <summary>Where the levels were last worked out; null before the first time.</summary>
    public Vector3? LastCentre { get; set; }
    /// <summary>Set when the radii change, so the next tick works the levels out again wherever the
    /// player is.</summary>
    public bool Stale { get; set; }
    /// <summary>The map's tiles as they were when the levels were last worked out (MapTiles.Version): on
    /// the world a tile arriving is worked in at once, even for a player standing still at its edge.</summary>
    public long SeenVersion { get; set; } = -1;

    /// <summary>Totals for this map, for the log and the tests.</summary>
    public long DefinitionsSent, Removed, TilesLoaded, TilesDropped, BytesSent;
    /// <summary>Bytes sent since the last TileStreamUpdate, for its log line.</summary>
    public long BytesSinceUpdate;

    internal sealed class Job
    {
        public TileKey Tile;
        public int Next;
    }
    internal readonly Queue<Job> Pending = new();
    internal readonly List<TileState> Done = new();
    internal int DefinitionsSinceUpdate, RemovedSinceUpdate;

    /// <summary>Definitions still to send.</summary>
    public int PendingTiles => Pending.Count;

    internal void SetLevels(Dictionary<TileKey, TileDetail> levels) => Levels = levels;

    /// <summary>Forgets everything: a new map, or the map sent again.</summary>
    public void Reset()
    {
        Levels = new Dictionary<TileKey, TileDetail>();
        LastCentre = null;
        Stale = false;
        Pending.Clear();
        Done.Clear();
        SeenVersion = -1;
        DefinitionsSinceUpdate = RemovedSinceUpdate = 0;
        DefinitionsSent = Removed = TilesLoaded = TilesDropped = BytesSent = BytesSinceUpdate = 0;
    }
}

/// <summary>
/// Sends each client the tiles of a streamed map near it, and takes away the ones it has left behind.
/// The server keeps the whole map (stage 1 of docs/WORLD_STREAMING.md); this only decides what each
/// client is told about.
///
/// On joining, <see cref="Begin"/> chooses the tiles round where the player will arrive and the join
/// sends them all at once. After that <see cref="Update"/> runs every
/// tick for every player on the map: when the player has moved <see cref="MoveMetres"/> or changed their
/// detail setting it works the levels out again; tiles that went down have their entities removed at
/// once, tiles that went up are queued nearest first, and the queue is sent at most
/// <see cref="DefinitionsPerTick"/> definitions a tick, so a tile arriving never costs one tick much.
/// </summary>
public static class TileStreamer
{
    /// <summary>How far a player moves before the tile levels are worked out again, metres.</summary>
    public const float MoveMetres = 8f;

    /// <summary>The most definitions sent to one client in one tick: three batches. A Magnolia tile is
    /// at most 1,660 entities, so the biggest arrives over three ticks.</summary>
    public const int DefinitionsPerTick = 768;

    /// <summary>
    /// The levels for a player arriving at <paramref name="at"/>, set on <paramref name="interest"/>, and
    /// every fixed entity they should be sent with them: the global ones first, then each tile's,
    /// nearest tile first, each once.
    /// </summary>
    public static List<int> Begin(TileInterest interest, MapTiles tiles, Vector3 at)
    {
        var radii = interest.Radii;
        interest.Reset();
        interest.Radii = radii;
        var levels = Ready(tiles, TileSelection.Desired(at, tiles.TileMetres, radii, null, tiles.Min, tiles.Max));
        interest.SetLevels(levels);
        interest.SeenVersion = tiles.Version;
        interest.LastCentre = at;
        interest.TilesLoaded = levels.Count;

        var ids = new List<int>(tiles.Global);
        var seen = new HashSet<int>(ids);
        foreach (var key in levels.Keys.OrderBy(k => k.DistanceFrom(at, tiles.TileMetres)).ThenBy(k => k.X).ThenBy(k => k.Z))
            foreach (int id in tiles.Members(key))
                if (seen.Add(id) && tiles.Wanted(id, levels)) ids.Add(id);
        return ids;
    }

    /// <summary>The tiles a client has, as the update that says so.</summary>
    public static TileStreamUpdate Snapshot(TileInterest interest, MapTiles tiles, int definitions)
    {
        var update = new TileStreamUpdate { TileMetres = tiles.TileMetres, Definitions = definitions };
        foreach (var (key, level) in interest.Levels.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Z))
            update.Tiles.Add(new TileState(key, level));
        return update;
    }

    /// <summary>
    /// One tick for one player on a streamed map: the levels worked out again if they have moved far
    /// enough, removals sent, and some of what is owed. <paramref name="send"/> takes each message to
    /// the client (reliable and ordered).
    /// </summary>
    public static void Update(UserSession session, MapTiles tiles, World world, Dictionary<int, Entity> lookup,
                              Vector3 at, Action<IMessage> send)
    {
        var interest = session.Tiles;
        if (interest.Stale || interest.SeenVersion != tiles.Version || interest.LastCentre is not { } last
            || Vector3.DistanceSquared(new Vector3(last.X, 0f, last.Z), new Vector3(at.X, 0f, at.Z)) >= MoveMetres * MoveMetres)
            Rechoose(session, tiles, world, lookup, at, send);
        Drain(session, tiles, world, lookup, send);
    }

    private static void Rechoose(UserSession session, MapTiles tiles, World world, Dictionary<int, Entity> lookup,
                                 Vector3 at, Action<IMessage> send)
    {
        var interest = session.Tiles;
        interest.Stale = false;
        interest.LastCentre = at;
        var before = interest.Levels;
        interest.SeenVersion = tiles.Version;
        var after = Ready(tiles, TileSelection.Desired(at, tiles.TileMetres, interest.Radii, before, tiles.Min, tiles.Max));

        var down = new List<TileKey>();
        var up = new List<TileKey>();
        foreach (var (key, was) in before)
        {
            var now = after.TryGetValue(key, out var l) ? l : TileDetail.None;
            if (now < was) down.Add(key);
        }
        foreach (var (key, now) in after)
        {
            var was = before.TryGetValue(key, out var l) ? l : TileDetail.None;
            if (now > was) up.Add(key);
        }
        if (down.Count == 0 && up.Count == 0) return;
        interest.SetLevels(after);

        if (down.Count > 0)
        {
            var gone = new List<int>();
            foreach (var key in down)
            {
                foreach (int id in tiles.Members(key))
                    if (session.KnownEntities.Contains(id) && !tiles.Wanted(id, after)) Forget(session, id, gone);
                var now = after.TryGetValue(key, out var l) ? l : TileDetail.None;
                interest.Done.Add(new TileState(key, now));
                if (now == TileDetail.None) interest.TilesDropped++;
            }
            // Anything fixed that was not in the map file (put down or built since) and now stands in
            // no tile the client has. Moving things are the broadcast's to remove as they always were.
            foreach (int id in session.KnownEntities)
            {
                if (tiles.IsTiled(id) || id == session.Entity.Id || session.VisibleDynamicEntities.Contains(id)) continue;
                if (!lookup.TryGetValue(id, out var e) || !world.IsAlive(e) || !world.Has<Transform>(e)) continue;
                if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e) || tiles.IsGlobal(id)) continue;
                if (!tiles.Holds(world.Get<Transform>(e).Position, after)) gone.Add(id);
            }
            foreach (int id in gone) { session.KnownEntities.Remove(id); session.SentStates.Remove(id); }
            if (gone.Count > 0)
            {
                send(new EntityRemoved { EntityIds = gone });
                interest.RemovedSinceUpdate += gone.Count;
                interest.Removed += gone.Count;
            }
        }

        foreach (var key in up.OrderBy(k => k.DistanceFrom(at, tiles.TileMetres)))
        {
            interest.Pending.Enqueue(new TileInterest.Job { Tile = key });
            if (!before.ContainsKey(key)) interest.TilesLoaded++;
        }
        // Only removals: say so now. Loads are said as each tile finishes.
        if (interest.Pending.Count == 0) Announce(interest, tiles, send);
    }

    /// <summary>On the world, only the tiles that are there: one not made yet is chosen when it arrives.</summary>
    private static Dictionary<TileKey, TileDetail> Ready(MapTiles tiles, Dictionary<TileKey, TileDetail> levels)
    {
        if (!tiles.Dynamic) return levels;
        foreach (var key in levels.Keys.Where(k => !tiles.IsReady(k)).ToList()) levels.Remove(key);
        return levels;
    }

    private static void Forget(UserSession session, int id, List<int> gone)
    {
        session.KnownEntities.Remove(id);
        session.SentStates.Remove(id);
        session.VisibleDynamicEntities.Remove(id);
        gone.Add(id);
    }

    private static void Drain(UserSession session, MapTiles tiles, World world, Dictionary<int, Entity> lookup, Action<IMessage> send)
    {
        var interest = session.Tiles;
        if (interest.Pending.Count == 0) return;
        int budget = DefinitionsPerTick;
        var batch = new EntityDefinitionBatch();
        bool finished = false;
        while (budget > 0 && interest.Pending.Count > 0)
        {
            var job = interest.Pending.Peek();
            var members = tiles.Members(job.Tile);
            while (job.Next < members.Count && budget > 0)
            {
                int id = members[job.Next++];
                if (session.KnownEntities.Contains(id) || !tiles.Wanted(id, interest.Levels)) continue;
                if (!lookup.TryGetValue(id, out var e) || !world.IsAlive(e)) continue;
                batch.Definitions.Add(EntityDefinitionFactory.From(world, e));
                session.KnownEntities.Add(id);
                budget--;
                if (batch.Definitions.Count >= EntityDefinitionBatch.Size)
                {
                    send(EntityDefinitionPack.Pack(batch));
                    batch = new EntityDefinitionBatch();
                }
            }
            if (job.Next < members.Count) break;
            interest.Pending.Dequeue();
            interest.Done.Add(new TileState(job.Tile, interest.Levels.TryGetValue(job.Tile, out var l) ? l : TileDetail.None));
            finished = true;
        }
        int sent = DefinitionsPerTick - budget;
        if (batch.Definitions.Count > 0) send(EntityDefinitionPack.Pack(batch));
        interest.DefinitionsSinceUpdate += sent;
        interest.DefinitionsSent += sent;
        if (finished) Announce(interest, tiles, send);
    }

    private static void Announce(TileInterest interest, MapTiles tiles, Action<IMessage> send)
    {
        if (interest.Done.Count == 0) return;
        send(new TileStreamUpdate
        {
            TileMetres = tiles.TileMetres,
            Tiles = new List<TileState>(interest.Done),
            Definitions = interest.DefinitionsSinceUpdate,
            Removed = interest.RemovedSinceUpdate,
        });
        interest.Done.Clear();
        interest.DefinitionsSinceUpdate = interest.RemovedSinceUpdate = 0;
    }
}
