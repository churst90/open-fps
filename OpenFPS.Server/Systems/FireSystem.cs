using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Fire on each map, authoritative (docs/FIRE.md 12): the things on the map that can burn
/// (FuelCatalog, from what each is made of and how big it is), a FireSpread stepped once a second in the
/// map's own weather, and one sound emitter for every part burning, keyed so every client renders the
/// same fire at the same point of its life (FireSpec.KeyFor). A map's fuel is read once, the first time
/// anything there is lit or struck. Fire lit with /spawn fire, lightning and what catches from them all
/// go through here.
/// </summary>
public sealed class FireSystem
{
    private sealed class MapFire
    {
        public readonly FireSpread Sim;
        public readonly Dictionary<(int Obj, int Part), Entity> Emitters = new();
        public readonly HashSet<int> Removed = new();
        public float Waited;
        public Task? Stepping;
        public int Logged;
        public MapFire(int seed) { Sim = new FireSpread(seed); }
    }

    private readonly Dictionary<string, MapFire> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<int>? _resend;

    /// <summary>How often the spread is stepped, s.</summary>
    public const float StepSeconds = 1f;

    /// <param name="resend">Asks the server to send an entity's definition again: a fire's state
    /// (SynthRunning, Quench) lives there.</param>
    public FireSystem(Action<int>? resend = null) { _resend = resend; }

    /// <summary>A map's spread, if anything has been lit or struck there.</summary>
    public FireSpread? SpreadOf(string mapId) => _maps.TryGetValue(mapId, out var m) ? m.Sim : null;

    /// <summary>Moves a map's fire on: its weather from the map's own (wind, rain, temperature, humidity).</summary>
    public void Update(MapManager maps, string mapId, World world, in WorldEnvironmentComponent env, float rainMmPerHour, float dt)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return;
        m.Waited += dt;
        // A step running off the tick thread: its emitters follow once it is done.
        if (m.Stepping != null)
        {
            if (!m.Stepping.IsCompleted) return;
            m.Stepping = null;
            Finish(maps, mapId, world, m);
        }
        if (m.Waited < StepSeconds) return;
        float step = m.Waited;
        m.Waited = 0f;
        double now = Clock();
        var weather = new FireWeather(
            WindWeather.Steady(WindAir.FromBroadcast(env.WindVelocity, env.WindGustiness, now, 0, 0)),
            MathF.Max(0f, rainMmPerHour), env.Temperature, Math.Clamp(env.Humidity * 100f, 0f, 100f));
        // A forest crowning steps in tens of milliseconds (docs/FIRE.md 12.10): never on the tick.
        if (Background)
            m.Stepping = Task.Run(() => { lock (m.Sim) m.Sim.Step(now, step, weather); });
        else
        {
            m.Sim.Step(now, step, weather);
            Finish(maps, mapId, world, m);
        }
    }

    /// <summary>The shared clock the spread and every client's sound read (WindField.Now); the tests' own.</summary>
    public Func<double> Clock = WindField.Now;

    /// <summary>Whether the spread steps off the tick thread (false in the tests, which want it now).</summary>
    public bool Background = true;

    private void Finish(MapManager maps, string mapId, World world, MapFire m)
    {
        lock (m.Sim)
        {
            for (; m.Logged < m.Sim.Events.Count; m.Logged++)
                Log.Information("Fire on {Map}: {Event}", mapId, FireSpread.Describe(m.Sim.Events[m.Logged], 0, m.Sim.Objects));
            Sync(maps, mapId, world, m, Clock());
        }
    }

    /// <summary>
    /// /spawn fire PRESET: a thing burning as that preset, lit now, on the ground at <paramref name="ground"/>
    /// facing <paramref name="yaw"/>. Its neighbours can catch from it. The id of the thing.
    /// </summary>
    public int Light(MapManager maps, string mapId, World world, string preset, Vector3 ground, float yaw)
    {
        var m = Map(maps, mapId, world);
        var thing = FuelCatalog.ForFire(preset);
        double now = Clock();
        lock (m.Sim)
        {
            int id = m.Sim.Add(new FuelObject(thing.Name, ground, yaw, thing), now);
            m.Sim.Light(id, now);
            Sync(maps, mapId, world, m, now);
            return id;
        }
    }

    /// <summary>Lightning coming down over a point: what it strikes, and whether that catches. The name of
    /// what it struck, or null for the ground.</summary>
    public string? Strike(MapManager maps, string mapId, World world, Vector3 point, bool force = false)
    {
        var m = Map(maps, mapId, world);
        double now = Clock();
        lock (m.Sim)
        {
            int struck = m.Sim.Strike(point, now, force: force);
            Sync(maps, mapId, world, m, now);
            return struck >= 0 ? m.Sim.Objects[struck].Name : null;
        }
    }

    /// <summary>/spawn fire out: the nearest thing burning (not a map's own fire) within reach goes out
    /// and its sound with it. Its distance, or NaN for none.</summary>
    public float PutOutNearest(MapManager maps, string mapId, World world, Vector3 at, float within)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return float.NaN;
        lock (m.Sim) return PutOut(maps, mapId, world, m, at, within);
    }

    private float PutOut(MapManager maps, string mapId, World world, MapFire m, Vector3 at, float within)
    {
        double now = Clock();
        int best = -1;
        float bestD = within;
        foreach (var b in m.Sim.Burning(now, 0f))
        {
            if (b.Always || !b.Running || m.Removed.Contains(b.Object)) continue;
            float d = Vector3.Distance(b.Position, at);
            if (d < bestD) { bestD = d; best = b.Object; }
        }
        if (best < 0) return float.NaN;
        m.Sim.PutOut(best, now);
        m.Removed.Add(best);
        Sync(maps, mapId, world, m, now);
        return bestD;
    }

    /// <summary>Water put on the nearest thing to a point: the hook a hose or a bucket will use
    /// (FireSpread.AddWater).</summary>
    public bool Water(string mapId, Vector3 at, float within, float kgPerSecond, float seconds)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return false;
        lock (m.Sim)
        {
            int best = -1;
            float bestD = within;
            for (int i = 0; i < m.Sim.Objects.Count; i++)
            {
                float d = Vector3.Distance(m.Sim.Objects[i].Position, at);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) return false;
            m.Sim.AddWater(best, kgPerSecond, seconds, Clock());
            return true;
        }
    }

    // ── The map's fuel ───────────────────────────────────────────────────────────────────────────

    private MapFire Map(MapManager maps, string mapId, World world)
    {
        if (_maps.TryGetValue(mapId, out var m)) return m;
        m = new MapFire(StringComparer.OrdinalIgnoreCase.GetHashCode(mapId));
        int count = 0;
        var query = new QueryDescription().WithAll<Transform, ColliderComponent>();
        world.Query(in query, (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (world.Has<PlayerComponent>(e)) return;
            string sound = world.TryGet<SoundEmitterComponent>(e, out var em) ? em.SoundId ?? "" : "";
            string material = world.TryGet<MaterialComponent>(e, out var mat) ? mat.Material ?? "" : "";
            // The ground under a thing is taken as the map's datum: the maps are flat so far (docs/FIRE.md 12.10).
            float bottom = t.Position.Y - 0.5f * c.Size.Y;
            var fuel = FuelCatalog.ForThing(sound, material, c.Shape, c.Size, bottom);
            if (fuel == null) return;
            float yaw = YawOf(t.Rotation);
            string name = world.TryGet<IdentityComponent>(e, out var id) && id.Name.Length > 0 ? id.Name : fuel.Name;
            // A placed fire keeps its own emitter; the thing's ground is under its middle.
            var ground = fuel.AlwaysBurning ? t.Position : new Vector3(t.Position.X, 0f, t.Position.Z);
            m.Sim.Add(new FuelObject(name, ground, yaw, fuel));
            count++;
        });
        Log.Information("Fire on {Map}: {Count} things that can burn", mapId, count);
        _maps[mapId] = m;
        return m;
    }

    private static float YawOf(Quaternion q)
    {
        var f = Vector3.Transform(Vector3.UnitZ, q);
        return MathF.Atan2(f.X, f.Z);
    }

    /// <summary>An emitter for every part burning, gone with it; state changes sent.</summary>
    private void Sync(MapManager maps, string mapId, World world, MapFire m, double now)
    {
        var live = new HashSet<(int, int)>();
        foreach (var b in m.Sim.Burning(now))
        {
            if (b.Always || m.Removed.Contains(b.Object)) continue;
            var key = (b.Object, b.Part);
            live.Add(key);
            if (m.Emitters.TryGetValue(key, out var e) && world.IsAlive(e))
            {
                ref var em = ref world.Get<SoundEmitterComponent>(e);
                if (em.SynthRunning != b.Running || MathF.Abs(em.Quench - b.Quench) > 0.05f)
                {
                    em.SynthRunning = b.Running;
                    em.Quench = b.Quench;
                    _resend?.Invoke(e.Id);
                }
                continue;
            }
            var spec = FireSpec.ByName(FireSpread.KeyOf(b));
            var shape = b.Shape;
            var collider = new ColliderComponent
            {
                Shape = shape.Kind == FireShapeKind.Circle ? ColliderShape.Cylinder : ColliderShape.Box,
                Size = new Vector3(shape.Width, MathF.Max(0.5f, spec.FlameHeightMetres), shape.Depth),
                IsSolid = false,
            };
            string sound = FireSpread.KeyOf(b);
            var part = m.Sim.Objects[b.Object].Fuel.Parts[b.Part];
            e = maps.SpawnEntity(mapId, w => w.Create(
                new Transform { Position = b.Position, Rotation = Quaternion.CreateFromYawPitchRoll(b.Yaw, 0f, 0f), Scale = Vector3.One, IsDirty = true },
                collider,
                new IdentityComponent { Name = "Fire", Description = $"{m.Sim.Objects[b.Object].Name}: its {part.Name} burning ({spec.Name})." },
                new SoundEmitterComponent { IsSynth = true, SoundId = sound, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 3000f, MinDistance = 1f,
                                            SynthRunning = b.Running, Quench = b.Quench },
                EntityType.StaticObject));
            if (e == Entity.Null) continue;
            m.Emitters[key] = e;
            _resend?.Invoke(e.Id);
        }
        if (m.Emitters.Count == live.Count) return;
        foreach (var key in m.Emitters.Keys.Where(k => !live.Contains(k)).ToList())
        {
            maps.DestroyEntity(mapId, m.Emitters[key]);
            m.Emitters.Remove(key);
        }
    }

    /// <summary>Tests: forgets every map.</summary>
    internal void Reset() => _maps.Clear();
}
