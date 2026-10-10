using System.Collections.Concurrent;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Water;

namespace OpenFPS.Server.Systems;

/// <summary>
/// The water on each map's ground (docs/RUNNING_WATER.md section 13): the rain each surface sheds through the
/// map's drainage network (GroundWater, sent to every client for the voices on the lines), the hollows that
/// keep water filling and overflowing (ponds and puddles), and water added at a point (a bucket, a hose, a
/// burst main) carried downhill. Asked by other systems how wet a place is and how much water is reaching a
/// thing (the fire). Advanced on the tick thread.
/// </summary>
public static class GroundWaterSystem
{
    /// <summary>How wet one place is now.</summary>
    /// <param name="Surface">What the ground there is.</param>
    /// <param name="RainMmPerHour">The rain falling on it.</param>
    /// <param name="EventMm">The rain the ground has taken since it was last dry, mm.</param>
    /// <param name="SoilWetness">How near the ground there is to holding all it can, 0..1: the rain since it was
    /// dry over that plus the ground's own retention (TR-55's S), the water poured on it counted with the rain.</param>
    /// <param name="WaterDepthMm">Water on the surface there: a pond's or a puddle's depth, a drainage line's, or
    /// the sheet running over a slope, mm.</param>
    /// <param name="FlowLitresPerSecond">The water running past it (on a drainage line, or as a sheet from upslope).</param>
    /// <param name="PouredMm">Water poured on it in the last hour (a bucket, a hose), mm over the ground it wet.</param>
    public readonly record struct Wetness(GroundSurface Surface, float RainMmPerHour, float EventMm, float SoilWetness,
                                          float WaterDepthMm, float FlowLitresPerSecond, float PouredMm);

    /// <summary>The water reaching a round patch now, L/s: the rain falling on it, the water running onto it
    /// from upslope, and water poured that reaches it.</summary>
    public readonly record struct WaterReach(float RainLitresPerSecond, float RunOnLitresPerSecond, float PouredLitresPerSecond)
    {
        public float Total => RainLitresPerSecond + RunOnLitresPerSecond + PouredLitresPerSecond;
    }

    /// <summary>How long a place counts as wetted by water poured on it, s.</summary>
    public const float PouredMemorySeconds = 3600f;

    /// <summary>How wide a strip water poured on the ground runs in as it goes, m. A judgement: a bucket's water
    /// spreads a metre or so and gathers into the fall line.</summary>
    public const float PouredStripMetres = 0.5f;

    /// <summary>How often the ponds and the poured water are moved on, s.</summary>
    public const float StepSeconds = 0.25f;

    private sealed class Parcel
    {
        public int Cell;
        public int From = -1;   // the cell it came from (-1: poured here)
        public float Litres;
        public float Along;     // m into the step out of its cell
    }

    private sealed class Pour
    {
        public int Cell;
        public float LitresPerSecond;
        public float SecondsLeft;
    }

    private sealed class MapGround(GroundWater water)
    {
        public readonly object Gate = new();
        public DrainageNetwork Net = null!;
        public readonly GroundWater Water = water;
        public float[] PondVolume = Array.Empty<float>();
        public float[] PondSpill = Array.Empty<float>();
        public int[] PondsByRank = Array.Empty<int>();
        /// <summary>The ponds some voice names (only theirs go on the wire).</summary>
        public HashSet<int> Heard = new();

        public readonly List<Parcel> Parcels = new();
        public readonly List<Pour> Pours = new();
        /// <summary>Poured water arriving at cells, from the cell it came from (-1: poured there), litres: the last
        /// full second's, and this one's.</summary>
        public List<(int From, int To, float Litres)> Arrived = new(), Arriving = new();
        public float ArrivedSeconds = 1f;
        public float SecondClock, StepClock;
        /// <summary>Water poured that soaked in at each cell: litres, and when.</summary>
        public readonly Dictionary<int, (float Litres, double At)> Soaked = new();
        public double Clock;
        public float Evaporation;
    }

    private static readonly ConcurrentDictionary<string, MapGround> _maps = new(StringComparer.OrdinalIgnoreCase);

    // ── A frame of the world: tiles that come and go ────────────────────────────────────────────

    /// <summary>How long a frame's tiles must stay as they are before its network is built again, s: tiles
    /// arrive in bursts as somebody travels.</summary>
    public const double RebuildQuietSeconds = 3.0;

    private sealed class FrameTiles
    {
        public readonly object Gate = new();
        public readonly Dictionary<(int X, int Z), DrainageNetwork.TileInput> Tiles = new();
        public bool Dirty;
        public double ChangedAt;
        public Task<DrainageNetwork>? Building;
        public DrainageNetwork? Rebuilt;
    }

    private static readonly ConcurrentDictionary<string, FrameTiles> _frames = new(StringComparer.OrdinalIgnoreCase);
    private static double FrameClock => Environment.TickCount64 / 1000.0;

    /// <summary>A tile of a frame of the world loaded (OneWorld.WorldMaps): its ground and its drainage join the
    /// frame's network when it is next built.</summary>
    public static void TileArrived(string mapId, DrainageNetwork.TileInput tile)
    {
        var f = _frames.GetOrAdd(mapId, _ => new FrameTiles());
        lock (f.Gate)
        {
            f.Tiles[(tile.X, tile.Z)] = tile;
            f.Dirty = true;
            f.ChangedAt = FrameClock;
        }
    }

    /// <summary>A tile of a frame let go.</summary>
    public static void TileLeft(string mapId, int x, int z)
    {
        if (!_frames.TryGetValue(mapId, out var f)) return;
        lock (f.Gate)
        {
            if (!f.Tiles.Remove((x, z))) return;
            f.Dirty = true;
            f.ChangedAt = FrameClock;
        }
    }

    /// <summary>A frame's network built again since it was last asked: its voices to put in place (WorldMaps).
    /// Null when nothing changed.</summary>
    public static DrainageNetwork? TakeRebuilt(string mapId)
    {
        if (!_frames.TryGetValue(mapId, out var f)) return null;
        lock (f.Gate)
        {
            var net = f.Rebuilt;
            f.Rebuilt = null;
            return net;
        }
    }

    /// <summary>Starts a frame's network building once its tiles have been still a moment, and puts a finished one
    /// in place, keeping the map's water as it was (the ponds settled to the weather now).</summary>
    private static void PumpFrame(string mapId, FrameTiles f, float rainMmPerHour)
    {
        DrainageNetwork? done = null;
        lock (f.Gate)
        {
            if (f.Building is { IsCompleted: true } b)
            {
                if (b.IsCompletedSuccessfully) done = b.Result;
                f.Building = null;
            }
            if (f.Building == null && f.Dirty && FrameClock - f.ChangedAt >= RebuildQuietSeconds)
            {
                f.Dirty = false;
                var tiles = f.Tiles.Values.ToList();
                f.Building = Task.Run(() => new DrainageNetwork().Build(tiles));
            }
        }
        if (done == null) return;
        var old = _maps.TryGetValue(mapId, out var was) ? was : null;
        Register(mapId, done, old?.Water);
        if (old != null || rainMmPerHour > 0f) Settle(mapId, rainMmPerHour, keepWater: true);
        lock (f.Gate) f.Rebuilt = done;
    }

    /// <summary>Puts a map's drainage network in place (replacing any before it), dry.</summary>
    public static void Register(string mapId, DrainageNetwork net) => Register(mapId, net, null);

    private static void Register(string mapId, DrainageNetwork net, GroundWater? water)
    {
        var m = new MapGround(water ?? new GroundWater()) { Net = net };
        m.PondVolume = net.Ponds.Select(p => FullWhenDry(p) ? p.CapacityCubicMetres : 0f).ToArray();
        m.PondSpill = new float[net.Ponds.Count];
        m.PondsByRank = net.Ponds.OrderBy(p => p.Rank).Select(p => p.Id).ToArray();
        foreach (var v in net.Voices) foreach (int p in v.Catchment.Ponds) m.Heard.Add(p);

        _maps[mapId] = m;
    }

    /// <summary>Forgets a map's ground (unloaded).</summary>
    public static void Forget(string mapId) => _maps.TryRemove(mapId, out _);

    public static DrainageNetwork? NetworkOf(string mapId) => _maps.TryGetValue(mapId, out var m) ? m.Net : null;

    /// <summary>A map's water, for the broadcast; null for a map without ground of its own.</summary>
    public static GroundWater? WaterOf(string mapId) => _maps.TryGetValue(mapId, out var m) ? m.Water : null;

    /// <summary>
    /// Advances one map's ground water by <paramref name="dt"/> seconds of <paramref name="rainMmPerHour"/>, with
    /// the air taking <paramref name="evaporationMmPerHour"/> off standing water (RoadWaterSystem.Evaporation).
    /// </summary>
    public static void Update(string mapId, float rainMmPerHour, float evaporationMmPerHour, float dt)
    {
        if (_frames.TryGetValue(mapId, out var frame)) PumpFrame(mapId, frame, rainMmPerHour);
        if (!_maps.TryGetValue(mapId, out var m)) return;
        lock (m.Gate)
        {
            m.Water.Step(rainMmPerHour, dt);
            m.Evaporation = MathF.Max(0f, evaporationMmPerHour);
            m.Clock += dt;
            m.StepClock += dt;
            if (m.StepClock < StepSeconds) return;
            float step = MathF.Min(m.StepClock, 5f);
            m.StepClock = 0f;
            StepPonds(m, step);
            StepPoured(m, step);
            m.SecondClock += step;
            if (m.SecondClock >= 1f)
            {
                (m.Arrived, m.Arriving) = (m.Arriving, m.Arrived);
                m.Arriving.Clear();
                m.ArrivedSeconds = m.SecondClock;
                m.SecondClock = 0f;
            }
        }
    }

    /// <summary>Settles a map's water as if the weather had been like this for an hour (tests, the lab); with
    /// <paramref name="keepWater"/>, only its ponds (a network built again).</summary>
    public static void Settle(string mapId, float rainMmPerHour, bool keepWater = false)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return;
        lock (m.Gate)
        {
            if (!keepWater) m.Water.Settle(rainMmPerHour);
            for (int p = 0; p < m.PondVolume.Length; p++)
            {
                var pond = m.Net.Ponds[p];
                m.PondVolume[p] = rainMmPerHour > 0f || FullWhenDry(pond) ? pond.CapacityCubicMetres : 0f;
                m.PondSpill[p] = 0f;
            }
            if (rainMmPerHour > 0f) StepPonds(m, StepSeconds);
        }
    }

    /// <summary>The ponds, upstream first: what drains to each through the map's water, what overflows into it,
    /// what soaks away through its bed and what the air takes, and what it passes on once full.</summary>
    private static void StepPonds(MapGround m, float dt)
    {
        var net = m.Net;
        float cellArea = net.CellArea;
        var spills = new List<KeyValuePair<int, float>>();
        foreach (int id in m.PondsByRank)
        {
            var p = net.Ponds[id];
            float inflow = m.Water.RainFedLitresPerSecond(p.RainCatchment) * 1e-3f;
            foreach (int u in p.Upstream) inflow += m.PondSpill[u] * 1e-3f;
            var (_, wet) = p.LevelOf(m.PondVolume[id], cellArea);
            float seep = Seep(p) + m.Evaporation;
            float v = m.PondVolume[id] + (inflow - seep / 3.6e6f * wet) * dt;
            float spill = 0f;
            if (v > p.CapacityCubicMetres) { spill = (v - p.CapacityCubicMetres) / dt * 1e3f; v = p.CapacityCubicMetres; }
            m.PondVolume[id] = MathF.Max(0f, v);
            m.PondSpill[id] = spill;
            if (spill > 0f && m.Heard.Contains(id)) spills.Add(new(id, spill));
        }
        m.Water.SetSpills(spills);
    }

    /// <summary>What soaks away through a pond's bed, mm/h: the ground's own rate (a puddle's cracks on paving);
    /// none where the pond lies on a line that carries base flow, where the water table stands at the surface
    /// and feeds it rather than draining it.</summary>
    private static float Seep(DrainageNetwork.Pond p)
    {
        float perennial = Math.Clamp((p.TotalSquareMetres - GroundWater.PerennialFromSquareMetres)
                                     / (GroundWater.PerennialSquareMetres - GroundWater.PerennialFromSquareMetres), 0f, 1f);
        return (1f - perennial) * (p.IsPuddle ? GroundHydrology.SealedSeepMmPerHour : GroundHydrology.SoakMmPerHour);
    }

    /// <summary>Whether a pond is full in dry weather: one the water table keeps full (on a line that carries
    /// base flow).</summary>
    private static bool FullWhenDry(DrainageNetwork.Pond p) => p.TotalSquareMetres >= 0.5f * (GroundWater.PerennialFromSquareMetres + GroundWater.PerennialSquareMetres);

    // ── Water added at a point ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds <paramref name="litres"/> of water at <paramref name="at"/> (map metres), all at once or spread over
    /// <paramref name="overSeconds"/> (a hose, a burst main): it runs downhill along the drainage, wetting the
    /// ground as it goes (each metre soaks up the surface's own wetting depth over the strip it runs in), into
    /// any pond on its way, until it has all soaked in or left the map. False where the map has no ground.
    /// </summary>
    public static bool AddWater(string mapId, Vector3 at, float litres, float overSeconds = 0f)
    {
        if (!_maps.TryGetValue(mapId, out var m) || !(litres > 0f) || !float.IsFinite(litres)) return false;
        lock (m.Gate)
        {
            int c = m.Net.CellAt(at.X, at.Z);
            if (c < 0) return false;
            if (overSeconds > StepSeconds) m.Pours.Add(new Pour { Cell = c, LitresPerSecond = litres / overSeconds, SecondsLeft = overSeconds });
            else m.Parcels.Add(new Parcel { Cell = c, Litres = litres });
            return true;
        }
    }

    /// <summary>The poured water moved on: each parcel runs down its line at the speed of shallow concentrated
    /// flow (TR-55), leaving the wetting depth of what it crosses behind it.</summary>
    private static void StepPoured(MapGround m, float dt)
    {
        var net = m.Net;
        for (int k = m.Pours.Count - 1; k >= 0; k--)
        {
            var pour = m.Pours[k];
            float t = MathF.Min(dt, pour.SecondsLeft);
            m.Parcels.Add(new Parcel { Cell = pour.Cell, Litres = pour.LitresPerSecond * t });
            pour.SecondsLeft -= t;
            if (pour.SecondsLeft <= 0f) m.Pours.RemoveAt(k);
        }
        for (int k = m.Parcels.Count - 1; k >= 0; k--)
        {
            var parcel = m.Parcels[k];
            bool gone = false;
            // A parcel crosses its cells one at a time until this step's time is used.
            float time = dt;
            int guard = 0;
            while (time > 0f && !gone && guard++ < 64)
            {
                int c = parcel.Cell, n = net.Next(c);
                var surface = net.SurfaceOf(c);
                // Its cell soaks up its share as it arrives.
                if (parcel.Along == 0f)
                {
                    float soak = MathF.Min(parcel.Litres, GroundHydrology.WettingMm(surface) * PouredStripMetres * net.CellMetres);
                    parcel.Litres -= soak;
                    float before = m.Soaked.TryGetValue(c, out var s0) && m.Clock - s0.At < PouredMemorySeconds ? s0.Litres : 0f;
                    m.Soaked[c] = (before + soak, m.Clock);
                    m.Arriving.Add((parcel.From, c, parcel.Litres + soak));
                    int pond = net.PondOf(c);
                    if (pond >= 0)
                    {
                        m.PondVolume[pond] = MathF.Min(net.Ponds[pond].CapacityCubicMetres, m.PondVolume[pond] + parcel.Litres * 1e-3f);
                        gone = true;
                        break;
                    }
                }
                if (parcel.Litres <= 0.01f || n < 0) { gone = true; break; }
                float step = Vector2.Distance(new Vector2(net.Centre(c).X, net.Centre(c).Z), new Vector2(net.Centre(n).X, net.Centre(n).Z));
                float slope = MathF.Max(0.002f, (net.Height(c) - net.Height(n)) / MathF.Max(0.1f, step));
                float speed = GroundHydrology.ShallowSpeed(slope, GroundHydrology.Paved(surface));
                float need = (step - parcel.Along) / speed;
                if (need > time) { parcel.Along += speed * time; time = 0f; break; }
                time -= need;
                parcel.Along = 0f;
                parcel.From = c;
                parcel.Cell = n;
            }
            if (gone) m.Parcels.RemoveAt(k);
        }
    }

    // ── Asking ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>How wet a place of a map is now (default, dry and open, where the map has no ground).</summary>
    public static Wetness WetnessAt(string mapId, Vector3 at)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return default;
        lock (m.Gate)
        {
            var net = m.Net;
            int c = net.CellAt(at.X, at.Z);
            if (c < 0) return default;
            var surface = net.SurfaceOf(c);
            float ev = m.Water.EventMm;
            float poured = 0f;
            if (m.Soaked.TryGetValue(c, out var s) && m.Clock - s.At < PouredMemorySeconds)
                poured = s.Litres / (PouredStripMetres * net.CellMetres);
            float retention = GroundHydrology.RetentionMm(surface);
            float wetness = surface == GroundSurface.Water ? 1f : Math.Clamp((ev + poured) / MathF.Max(1e-3f, ev + poured + retention), 0f, 1f);
            float flow = FlowOut(m, c);
            float depth = DepthAt(m, c, flow);
            return new Wetness(surface, m.Water.RainMmPerHour, ev, wetness, depth, flow, poured);
        }
    }

    /// <summary>
    /// The water reaching a round patch of a map now (a fire's bed, its middle <paramref name="centre"/> and
    /// <paramref name="radius"/> across): the rain on it, what runs onto it from the cells round it that drain
    /// into it, and what poured water reached it in the last second.
    /// </summary>
    public static WaterReach WaterReaching(string mapId, Vector3 centre, float radius)
    {
        float r = MathF.Max(0.1f, radius);
        if (!_maps.TryGetValue(mapId, out var m)) return default;
        lock (m.Gate)
        {
            var net = m.Net;
            float rain = m.Water.RainMmPerHour * MathF.PI * r * r / 3600f;
            float runOn = 0f, poured = 0f;
            float reach = r + 2f * net.CellMetres;
            var inside = new HashSet<int>();
            var near = new List<int>();
            for (float z = centre.Z - reach; z <= centre.Z + reach; z += net.CellMetres)
                for (float x = centre.X - reach; x <= centre.X + reach; x += net.CellMetres)
                {
                    int c = net.CellAt(x, z);
                    if (c < 0) continue;
                    var p = net.Centre(c);
                    if (Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(centre.X, centre.Z)) <= r) inside.Add(c);
                    else near.Add(c);
                }
            foreach (int c in near)
            {
                int n = net.Next(c);
                if (n >= 0 && inside.Contains(n)) runOn += FlowOut(m, c);
            }
            // Poured water entering the patch: arriving at a cell in it from one outside it, or poured in it.
            foreach (var (from, to, litres) in m.Arrived)
                if (inside.Contains(to) && !inside.Contains(from)) poured += litres;
            poured /= MathF.Max(0.25f, m.ArrivedSeconds);
            return new WaterReach(rain, runOn, poured);
        }
    }

    /// <summary>The depth of water standing at a place (a pond, a puddle on a road), mm: what a wheel splashes
    /// into (RoadWaterSystem). Zero off every hollow that keeps water.</summary>
    public static float StandingMm(string mapId, Vector3 at)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return 0f;
        lock (m.Gate)
        {
            int c = m.Net.CellAt(at.X, at.Z);
            return c < 0 ? 0f : PondDepthMm(m, c);
        }
    }

    /// <summary>The flow of a drainage line's voice cell, or any cell, now, L/s (tests and the lab).</summary>
    public static float FlowAt(string mapId, Vector3 at)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return 0f;
        lock (m.Gate)
        {
            int c = m.Net.CellAt(at.X, at.Z);
            return c < 0 ? 0f : FlowOut(m, c);
        }
    }

    /// <summary>A pond's water now: volume m³ and overflow L/s (tests and the lab).</summary>
    public static (float CubicMetres, float SpillLitresPerSecond) PondState(string mapId, int pond)
    {
        if (!_maps.TryGetValue(mapId, out var m) || pond < 0 || pond >= m.PondVolume.Length) return default;
        lock (m.Gate) return (m.PondVolume[pond], m.PondSpill[pond]);
    }

    /// <summary>Poured water still running (tests and the lab).</summary>
    public static float PouredRunningLitres(string mapId)
    {
        if (!_maps.TryGetValue(mapId, out var m)) return 0f;
        lock (m.Gate) return m.Parcels.Sum(p => p.Litres);
    }

    /// <summary>The water leaving a cell downhill now, L/s: a line cell's catchment through the map's water and
    /// the ponds that overflow into it; elsewhere the sheet off the ground upslope.</summary>
    private static float FlowOut(MapGround m, int c)
    {
        var net = m.Net;
        var catchment = net.CatchmentOf(c);
        float q = m.Water.RainFedLitresPerSecond(catchment) + m.Water.BaseLitresPerSecond(catchment.BaseSquareMetres);
        foreach (int p in net.PondsInto(c)) q += m.PondSpill[p];
        return q;
    }

    private static float PondDepthMm(MapGround m, int c)
    {
        int pond = m.Net.PondOf(c);
        if (pond < 0) return 0f;
        var (level, _) = m.Net.Ponds[pond].LevelOf(m.PondVolume[pond], m.Net.CellArea);
        return MathF.Max(0f, level - m.Net.Height(c)) * 1000f;
    }

    /// <summary>The water on the surface at a cell, mm: standing in a hollow, running in a line (Manning in its
    /// bed), or a sheet off the slope (Manning across a metre of it).</summary>
    private static float DepthAt(MapGround m, int c, float flowLitresPerSecond)
    {
        float standing = PondDepthMm(m, c);
        if (standing > 0f || flowLitresPerSecond <= 0f) return standing;
        var net = m.Net;
        int n = net.Next(c);
        float slope = n >= 0 ? MathF.Max(0.002f, (net.Height(c) - net.Height(n)) / net.CellMetres) : 0.002f;
        float q = flowLitresPerSecond * 1e-3f;
        float nMan, width;
        if (net.IsLine(c))
        {
            var kind = net.SquareMetres(c) >= net.CreekSquareMetres ? GroundChannelKind.Creek : GroundChannelKind.Rill;
            nMan = GroundChannels.ManningN(kind);
            width = GroundChannels.WidthFor(kind, GroundChannels.ReferenceFlow(net.CatchmentOf(c)));
        }
        else
        {
            nMan = GroundHydrology.SheetManningN(net.SurfaceOf(c));
            width = net.CellMetres;
        }
        // A wide channel: q per metre = y^(5/3) S^(1/2) / n.
        float y = MathF.Pow(q / width * nMan / MathF.Sqrt(slope), 0.6f);
        return y * 1000f;
    }

    /// <summary>Tests: forgets every map.</summary>
    internal static void Reset() { _maps.Clear(); _frames.Clear(); }

    /// <summary>Tests: waits for a frame's network to be built from the tiles it has now.</summary>
    internal static DrainageNetwork? BuildFrameNow(string mapId)
    {
        if (!_frames.TryGetValue(mapId, out var f)) return null;
        lock (f.Gate) { f.ChangedAt = double.NegativeInfinity; }
        PumpFrame(mapId, f, 0f);
        Task<DrainageNetwork>? b;
        lock (f.Gate) b = f.Building;
        b?.Wait(TimeSpan.FromMinutes(2));
        PumpFrame(mapId, f, 0f);
        return NetworkOf(mapId);
    }
}
