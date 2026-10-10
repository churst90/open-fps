using System.Globalization;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>Why something caught.</summary>
public enum IgnitionCause { Lit, Lightning, Radiation, FlameContact, Ember, SurfaceFireBeneath }

/// <summary>Something that happened to a fire, for the log, the lab's timeline and the tests.</summary>
public readonly record struct FireEvent(double Time, int Object, string ObjectName, string Part, string What, IgnitionCause Cause, int From, float Value);

/// <summary>The weather a fire burns in: the wind, the rain (mm/h of water), the air's temperature (°C)
/// and relative humidity (%).</summary>
public readonly record struct FireWeather(WindWeather Wind, float RainMmPerHour, float TemperatureC, float HumidityPercent)
{
    public static FireWeather Dry(WindWeather wind) => new(wind, 0f, 22f, 40f);
}

/// <summary>A thing in the world that can burn: where its ground middle is, which way it faces, what it is.</summary>
public sealed record FuelObject(string Name, Vector3 Position, float Yaw, FuelSpec Fuel);

/// <summary>A part burning now, as the server sends it and the client hears it.</summary>
public readonly record struct BurningPart(int Object, int Part, string Preset, FireShape Shape, double LitAt, Vector3 Position,
                                          float Yaw, bool Running, float Quench, float HeatKw, bool Always);

/// <summary>
/// Fire spreading through what can burn (docs/FIRE.md 12): not cell to cell on a grid, but thing to thing
/// by what each needs to catch. A burning part radiates (a point source, χ_r Q / 4πR²), and its flames,
/// tilted by the wind, touch what is in them; what is heated keeps the heat by its own law (thick wood's
/// flux-time product, thin fuel's mass and water) and catches when it has had enough. Burning parts send
/// up glowing brands that the plume lofts and the wind carries; one landing on a receptive fuel may set it
/// going, by how dry it is. A surface fire under a crown takes the crown when it is intense enough (Van
/// Wagner 1977). Lightning sets things going where it strikes. Rain and other water cool what is burning
/// and wet what is not. Each part burns as its own fire (FireSpec), with the life its preset says, and
/// the sound follows from what is burning.
///
/// Deterministic: one seeded generator, everything visited in order. Stepped about once a second.
/// </summary>
public sealed class FireSpread
{
    // ── Constants with their sources ─────────────────────────────────────────────────────────────

    /// <summary>The share of a fire's heat release radiated: about 0.3-0.4 for wood and vegetation fires
    /// (Tewarson, SFPE Handbook ch. 3-4; Drysdale). 0.35.</summary>
    public const float RadiantFraction = 0.35f;
    /// <summary>A flame's emissive power, kW/m²: the flame at 1200 K and black, σT⁴ ≈ 118 (Butler and Cohen
    /// 1998). What a surface inside the flames gets, radiation and convection together, ESTIMATE 100.</summary>
    public const float FlameContactFlux = 100f;
    /// <summary>The most a point source can deliver outside its flames: half of the emissive power, a
    /// flame filling half the view.</summary>
    public const float RadiationCap = 50f;
    /// <summary>Heat a surface loses back once nothing heats it: what it had gathered falls with this time
    /// constant, s. ESTIMATE.</summary>
    public const float DoseCoolingSeconds = 90f;
    /// <summary>The heat flux the flames feed back to their own fuel, kW/m²: 20-40 for wood (Drysdale ch. 5).
    /// Water must take this away to put it out.</summary>
    public const float FeedbackFlux = 30f;
    /// <summary>Heat a kilogram of water takes from a fire, kJ: warming to 100 °C and boiling, 4.18 × 80 +
    /// 2257.</summary>
    public const float WaterCoolingKjPerKg = 2590f;
    /// <summary>The depth of flame over which rain falling through it is mostly boiled off, m (the share
    /// reaching the fuel is e^(−L / this)): drops of 1-2 mm at 6 m/s cross a metre of flame in a sixth of a
    /// second. ESTIMATE.</summary>
    public const float RainEvaporationMetres = 3f;

    // ── State ────────────────────────────────────────────────────────────────────────────────────

    private sealed class Part
    {
        public int Object, Index;
        public FuelPart Fuel = null!;
        public FireSpec Spec = null!;
        public Vector3 Centre;          // world, its ground-plan middle at its base
        public float Radius;            // its plan's equivalent radius
        public float Moisture;
        public float Dose;
        public double LitAt = double.NaN;
        public bool Always, Out, Burnt;
        public double OutAt = double.NaN, WetSince = double.NaN;
        public float Quench, Water;     // water: kg/m²s brought by hand now
        public float RainReaching;      // kg/m²s of rain getting through its flames to its fuel, last step
        public float WaterUntil;
        public float HeatKw, Flame, Tilt;
        public Vector2 Lean;            // the flame's lean, the way the wind goes
        public float FluxNow, BestFlux;
        public int FromNow = -1;
        public bool ContactNow;
        public float SurfaceIntensity;  // kW/m for a surface fuel burning
        public float Flare;
        public bool Burning => !double.IsNaN(LitAt) && !Out && !Burnt;
    }

    private readonly List<FuelObject> _objects = new();
    private readonly List<Part> _parts = new();
    private readonly Dictionary<long, List<int>> _grid = new();
    private const float CellMetres = 8f;
    private readonly List<FireEvent> _events = new();
    private uint _rng;

    private struct Brand
    {
        public double LandsAt;
        public Vector2 At;
        public float Alive, Weight;
        public int From;
    }
    private readonly List<Brand> _brands = new();
    /// <summary>The most brands in the air at once; past it the next are drawn heavier, as more real ones each.</summary>
    public int MaxBrands = 4000;
    /// <summary>The most brands drawn a second from every part together.</summary>
    public int MaxLaunches = 400;

    // The wind, read once a step for each 8 m cell and 2 m of height: the gust field costs, and a forest
    // burning asks it thousands of times a second.
    private readonly Dictionary<long, Vector2> _wind = new();
    private double _windAt = double.NaN;

    private Vector2 WindAt(in FireWeather w, float x, float height, float z, double now)
    {
        if (now != _windAt) { _wind.Clear(); _windAt = now; }
        int cx = (int)MathF.Floor(x / CellMetres), cz = (int)MathF.Floor(z / CellMetres), ch = Math.Clamp((int)(height / 2f), 0, 255);
        long key = ((long)(cx & 0xFFFFFF) << 32) ^ ((long)(cz & 0xFFFFFF) << 8) ^ ch;
        if (_wind.TryGetValue(key, out var v)) return v;
        v = WindField.VelocityAt(w.Wind, (cx + 0.5f) * CellMetres, MathF.Max(0.5f, 2f * ch + 1f), (cz + 0.5f) * CellMetres, now);
        _wind[key] = v;
        return v;
    }

    public FireSpread(int seed) { _rng = (uint)seed * 2654435761u | 1u; }

    public IReadOnlyList<FireEvent> Events => _events;
    public IReadOnlyList<FuelObject> Objects => _objects;
    public int BrandsInAir => _brands.Count;
    public long BrandsLaunched { get; private set; }

    /// <summary>Adds a thing that can burn; its id.</summary>
    public int Add(FuelObject thing, double now = double.NaN)
    {
        int id = _objects.Count;
        _objects.Add(thing);
        var rot = Matrix3x2.CreateRotation(-thing.Yaw);
        for (int i = 0; i < thing.Fuel.Parts.Length; i++)
        {
            var f = thing.Fuel.Parts[i];
            var off = Vector2.Transform(f.Offset, rot);
            var p = new Part
            {
                Object = id, Index = i, Fuel = f,
                Spec = SpecOf(f),
                Centre = thing.Position + new Vector3(off.X, f.Base, off.Y),
                Radius = 0.5f * f.Footprint.EquivalentDiameter,
                Moisture = f.Moisture,
            };
            int pi = _parts.Count;
            _parts.Add(p);
            foreach (long cell in CellsOf(p.Centre, p.Radius)) Cell(cell).Add(pi);
        }
        if (thing.Fuel.AlwaysBurning && thing.Fuel.Parts.Length > 0)
        {
            var first = _parts[_parts.Count - thing.Fuel.Parts.Length];
            first.Always = true;
            first.LitAt = double.NegativeInfinity;
        }
        return id;
    }

    private static FireSpec SpecOf(FuelPart f)
    {
        if (f.Preset.StartsWith("fire:", StringComparison.OrdinalIgnoreCase)) return FireSpec.ByName(f.Preset);
        return FireSpec.ByName(f.Preset).WithShape(f.Footprint);
    }

    // ── Lighting and water ───────────────────────────────────────────────────────────────────────

    /// <summary>Sets a thing going, as a person does: its most receptive part, or the one named.</summary>
    public bool Light(int obj, double now, string? part = null)
    {
        Part? best = null;
        foreach (var p in PartsOf(obj))
        {
            if (p.Burning) continue;
            if (part != null) { if (p.Fuel.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) { best = p; break; } continue; }
            if (best == null || p.Fuel.Receptivity > best.Fuel.Receptivity) best = p;
        }
        if (best == null) return false;
        Ignite(best, now, IgnitionCause.Lit, -1, 0f);
        return true;
    }

    /// <summary>
    /// A cloud-to-ground flash coming down over <paramref name="point"/>: it attaches to whatever its
    /// leader reaches first (the rolling sphere, radius 10 I^0.65 m for a peak current of I kA: IEC 62305;
    /// Golde), and a flash with a long continuing current (a third of them or so: Latham and Williams 2001)
    /// may set the fuel at its foot going, by how dry and receptive it is. Returns the thing struck, or -1
    /// for the ground.
    /// </summary>
    public int Strike(Vector3 point, double now, float peakKiloAmps = 30f, float continuingCurrentChance = 0.33f, bool force = false)
    {
        float rs = 10f * MathF.Pow(MathF.Max(1f, peakKiloAmps), 0.65f);
        int struck = -1;
        float best = rs + point.Y;
        for (int i = 0; i < _objects.Count; i++)
        {
            var o = _objects[i];
            float x = Vector2.Distance(new Vector2(o.Position.X, o.Position.Z), new Vector2(point.X, point.Z));
            if (x >= rs) continue;
            float top = o.Position.Y;
            foreach (var f in o.Fuel.Parts) top = MathF.Max(top, o.Position.Y + f.Top);
            float z = top + MathF.Sqrt(rs * rs - x * x);
            if (z > best) { best = z; struck = i; }
        }
        if (struck < 0) { _events.Add(new FireEvent(now, -1, "ground", "", "lightning struck the ground", IgnitionCause.Lightning, -1, 0f)); return -1; }
        _events.Add(new FireEvent(now, struck, _objects[struck].Name, "", "lightning struck it", IgnitionCause.Lightning, -1, 0f));
        // The current runs down it to the ground: what catches is the fuel it meets most ready to.
        Part? at = null;
        float chance = 0f;
        foreach (var p in PartsOf(struck))
        {
            if (p.Burning) continue;
            float c = p.Fuel.Receptivity * Dryness(p);
            if (at == null || c > chance) { at = p; chance = c; }
        }
        if (at == null) return struck;
        if (force || Random() < continuingCurrentChance * MathF.Min(1f, 2f * chance))
            Ignite(at, now, IgnitionCause.Lightning, -1, 0f);
        return struck;
    }

    /// <summary>
    /// Water put on a thing by hand (a hose, a bucket, water running over it), kg a second over its parts
    /// for <paramref name="seconds"/>: the hook a water substance delivers through. On a burning part it
    /// cools the fuel; on one not burning it wets it; on oil it flashes and throws the fire (docs/FIRE.md 12.7).
    /// </summary>
    public void AddWater(int obj, float kgPerSecond, float seconds, double now)
    {
        var parts = PartsOf(obj).ToList();
        if (parts.Count == 0) return;
        foreach (var p in parts)
        {
            float area = MathF.Max(0.05f, p.Fuel.Footprint.Area);
            p.Water = kgPerSecond / parts.Count / area;
            p.WaterUntil = (float)(now + seconds);
            if (p.Burning && p.Fuel.WaterFlares)
            {
                p.Flare = 3f;
                _events.Add(new FireEvent(now, obj, _objects[obj].Name, p.Fuel.Name, "water flashed to steam under the burning oil and threw it", IgnitionCause.Lit, -1, kgPerSecond));
            }
        }
    }

    /// <summary>
    /// Water arriving at a point on the ground (water running over the terrain, a bucket thrown, a hose's
    /// stream landing), kg a second for <paramref name="seconds"/>: it goes to whatever thing's plan the
    /// point is in, else to the nearest within half a metre. The thing it reached, or -1 for none. The
    /// interface a water substance calls; it needs nothing of it (docs/FIRE.md 12.7).
    /// </summary>
    public int AddWaterAt(Vector2 point, float kgPerSecond, float seconds, double now)
    {
        int best = -1;
        float bestD = 0.5f;
        foreach (int i in Near(new Vector3(point.X, 0f, point.Y), 0.5f))
        {
            var p = _parts[i];
            if (InPlan(p, point)) { best = p.Object; break; }
            var local = Local(p, new Vector3(point.X, 0f, point.Y));
            float d = p.Fuel.Footprint.Outside(local.X, local.Y);
            if (d < bestD) { bestD = d; best = p.Object; }
        }
        if (best >= 0) AddWater(best, kgPerSecond, seconds, now);
        return best;
    }

    /// <summary>
    /// The water reaching a thing's fuel now, kg per square metre a second, and the share of its burning it
    /// takes (0 to 1), for whoever wants to know what the water did: the rain through its flames and what
    /// was brought by hand, its largest part's.
    /// </summary>
    public (float KgPerSquareMetreSecond, float Quench) WaterOn(int obj, double now)
    {
        float water = 0f, quench = 0f;
        foreach (var p in PartsOf(obj))
        {
            float hand = now < p.WaterUntil ? p.Water : 0f;
            water = MathF.Max(water, hand + p.RainReaching);
            quench = MathF.Max(quench, p.Quench);
        }
        return (water, quench);
    }

    /// <summary>Puts a thing out at once (/spawn fire out): its flames go and its char cools.</summary>
    public void PutOut(int obj, double now)
    {
        foreach (var p in PartsOf(obj))
            if (p.Burning) { p.Out = true; p.OutAt = now; _events.Add(new FireEvent(now, obj, _objects[obj].Name, p.Fuel.Name, "put out", IgnitionCause.Lit, -1, 0f)); }
    }

    // ── The step ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Moves everything on by <paramref name="dt"/> seconds to <paramref name="now"/> (the shared clock).</summary>
    public void Step(double now, float dt, in FireWeather weather)
    {
        if (dt <= 0f) return;
        Moisten(dt, weather);
        // What is burning now, how hard, how tall and which way its flames lean.
        foreach (var p in _parts)
        {
            p.FluxNow = 0f;
            p.BestFlux = 0f;
            p.FromNow = -1;
            p.ContactNow = false;
            if (!p.Burning) { p.HeatKw = 0f; continue; }
            float life = p.Always ? 1f : FireSpec.LifeShare(p.Spec, now - p.LitAt);
            if (!p.Always && now - p.LitAt >= p.Spec.BurntOutAfter)
            {
                p.Burnt = true;
                p.HeatKw = 0f;
                _events.Add(new FireEvent(now, p.Object, _objects[p.Object].Name, p.Fuel.Name, "burnt out", IgnitionCause.Lit, -1, 0f));
                continue;
            }
            Douse(p, now, dt, weather, life);
            if (p.Out) { p.HeatKw = 0f; continue; }
            p.Flare *= MathF.Exp(-dt / 5f);
            p.HeatKw = p.Spec.HeatReleaseKw * life * (1f - p.Quench) * (1f + p.Flare);
            p.Flame = p.Spec.FlameHeightMetres * MathF.Pow(MathF.Max(0.01f, p.HeatKw / p.Spec.HeatReleaseKw), 0.4f);
            Lean(p, now, weather);
            p.SurfaceIntensity = p.Fuel.SurfaceLoad > 0f ? SurfaceIntensity(p, now, weather) : 0f;
        }
        // Heat from each burning part to the things near it.
        for (int si = 0; si < _parts.Count; si++)
        {
            var s = _parts[si];
            if (!s.Burning || s.HeatKw <= 0f) continue;
            float reach = MathF.Sqrt(RadiantFraction * s.HeatKw / (4f * MathF.PI * 2f)) + s.Flame + 2f * s.Radius;
            foreach (int ti in Near(s.Centre, reach))
            {
                if (ti == si) continue;
                var t = _parts[ti];
                if (t.Burning || t.Burnt) continue;
                var (rad, contact) = Flux(s, t);
                float q = MathF.Max(rad, contact);
                t.FluxNow += q;
                if (q > t.BestFlux) { t.BestFlux = q; t.FromNow = s.Object; t.ContactNow = contact > rad; }
            }
        }
        // A crown over a surface fire intense enough (Van Wagner 1977): asked from each surface fire burning,
        // of the crowns over it, so a forest's unburnt crowns cost nothing.
        for (int si = 0; si < _parts.Count; si++)
        {
            var s = _parts[si];
            if (!s.Burning || s.SurfaceIntensity <= 0f) continue;
            // The front has to be there and fully going first: its growth time.
            if (now - s.LitAt < s.Spec.GrowthSeconds) continue;
            foreach (int ci in Near(s.Centre, s.Radius + 1f))
            {
                var c = _parts[ci];
                if (c.Fuel.CrownBaseMetres <= 0f || c.Burning || c.Burnt) continue;
                if (Vector2.Distance(new(s.Centre.X, s.Centre.Z), new(c.Centre.X, c.Centre.Z)) > c.Radius + s.Radius) continue;
                if (s.SurfaceIntensity >= VanWagnerIntensity(c.Fuel.CrownBaseMetres, c.Moisture))
                    Ignite(c, now, IgnitionCause.SurfaceFireBeneath, s.Object, s.SurfaceIntensity);
            }
        }
        // What the heat does: it gathers, by each fuel's law, and what has had enough catches.
        foreach (var t in _parts)
        {
            if (t.Burning || t.Burnt) continue;
            // Several flames on it at once do not make it hotter than being inside one.
            t.FluxNow = MathF.Min(t.FluxNow, FlameContactFlux);
            float q = t.FluxNow - t.Fuel.CriticalFluxKw;
            if (q > 0f) t.Dose += (t.Fuel.Heating == FuelHeating.Thick ? q * q : q) * dt;
            else t.Dose *= MathF.Exp(-dt / DoseCoolingSeconds);
            if (t.Dose >= DoseNeeded(t))
                Ignite(t, now, t.ContactNow ? IgnitionCause.FlameContact : IgnitionCause.Radiation, t.FromNow, t.FluxNow);
        }
        Brands(now, dt, weather);
    }

    /// <summary>The heat flux from a burning part to another, kW/m²: its flames' radiation, or the flames
    /// themselves if they reach it.</summary>
    private (float Radiation, float Contact) Flux(Part s, Part t)
    {
        // The flame: its base's own shape, carried up and along an axis leaning with the wind, as long as the flame.
        var basePt = s.Centre;
        var axis = new Vector3(s.Lean.X * MathF.Sin(s.Tilt), MathF.Cos(s.Tilt), s.Lean.Y * MathF.Sin(s.Tilt));
        var mid = basePt + axis * (0.5f * s.Flame);
        // Radiation from the flame's middle to the nearest point of the other part.
        var near = Nearest(t, mid);
        float r = MathF.Max(0.1f, Vector3.Distance(near, mid));
        float rad = MathF.Min(RadiationCap, RadiantFraction * s.HeatKw / (4f * MathF.PI * r * r));
        // Contact: at heights up the flame, how far the other part is outside the flame's section there (the
        // base's shape moved along the lean), within a quarter of the flame's radius counting as in it.
        float bottom = t.Centre.Y, top = t.Centre.Y + MathF.Max(0.05f, t.Fuel.Top - t.Fuel.Base);
        float edge = 0.25f * MathF.Max(0.3f, s.Radius);
        // Every point of the flame is within half its length and its base's half-diagonal of its middle:
        // further than that, nothing touches.
        float halfDiagonal = 0.5f * MathF.Sqrt(s.Fuel.Footprint.Width * s.Fuel.Footprint.Width + s.Fuel.Footprint.Depth * s.Fuel.Footprint.Depth);
        if (r > 0.5f * s.Flame + halfDiagonal + 1.5f * edge) return (rad, 0f);
        const int Samples = 6;
        int touching = 0;
        float best = 0f;
        for (int k = 0; k < Samples; k++)
        {
            var c = basePt + axis * (s.Flame * (k + 0.5f) / Samples);
            if (c.Y < bottom - edge || c.Y > top + edge) continue;
            var at = Nearest(t, c);
            var local = Local(s, at - (c - basePt));
            float outside = s.Fuel.Footprint.Outside(local.X, local.Y);
            float share = Math.Clamp(1f - outside / edge, 0f, 1f);
            if (share > 0f) { touching++; best = MathF.Max(best, share); }
        }
        // A part catches as a whole only as far as the flames cover it: knee-high flames at the foot of a
        // twelve-metre trunk scorch it and do not set it burning.
        float flameBottom = MathF.Min(basePt.Y, basePt.Y + axis.Y * s.Flame), flameTop = MathF.Max(basePt.Y, basePt.Y + axis.Y * s.Flame) + edge;
        float covered = Math.Clamp((MathF.Min(flameTop, top) - MathF.Max(flameBottom, bottom)) / (top - bottom), 0f, 1f);
        float contact = touching > 0 ? FlameContactFlux * best * covered : 0f;
        return (rad, contact);
    }

    /// <summary>A world point in a part's own plan frame, from its middle.</summary>
    private Vector2 Local(Part p, Vector3 w)
        => Vector2.Transform(new Vector2(w.X - p.Centre.X, w.Z - p.Centre.Z), Matrix3x2.CreateRotation(_objects[p.Object].Yaw));

    /// <summary>The nearest point of a part (its footprint, from its bottom to its top) to a point.</summary>
    private Vector3 Nearest(Part t, Vector3 p)
    {
        var local = Local(t, p);
        var c = t.Fuel.Footprint.Closest(local.X, local.Y);
        var world = Vector2.Transform(c, Matrix3x2.CreateRotation(-_objects[t.Object].Yaw));
        float bottom = t.Centre.Y, top = t.Centre.Y + MathF.Max(0.05f, t.Fuel.Top - t.Fuel.Base);
        return new Vector3(t.Centre.X + world.X, Math.Clamp(p.Y, bottom, top), t.Centre.Z + world.Y);
    }

    /// <summary>
    /// The flame's lean in the wind: cos θ = 1 for u* ≤ 1, else u*^-1/2, u* the wind at mid-flame over
    /// the plume's own buoyant velocity (g Q / ρ c_p T D)^1/3: the AGA correlation for wind-blown pool fires
    /// (SFPE Handbook, Beyler), its velocity scale taken from Q* (an adaptation: ESTIMATE).
    /// </summary>
    private void Lean(Part p, double now, in FireWeather weather)
    {
        float d = MathF.Max(0.3f, 2f * p.Radius);
        float ub = MathF.Pow(9.81f * p.HeatKw / (1.2f * 1.0f * 293f * d), 1f / 3f);
        var v = WindAt(weather, p.Centre.X, MathF.Max(0.5f, p.Centre.Y + 0.5f * p.Flame), p.Centre.Z, now);
        float u = v.Length();
        p.Lean = u > 1e-3f ? v / u : Vector2.UnitX;
        float star = u / MathF.Max(0.1f, ub);
        p.Tilt = star <= 1f ? 0f : MathF.Acos(1f / MathF.Sqrt(star));
    }

    // ── Moisture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The moisture dead fuel settles to in still air, as a share: Simard's (1968) equilibrium moisture
    /// content from the temperature and relative humidity (the NFDRS form, in °F and %).
    /// </summary>
    public static float EquilibriumMoisture(float temperatureC, float humidityPercent)
    {
        float t = temperatureC * 1.8f + 32f, h = Math.Clamp(humidityPercent, 0f, 100f);
        float emc = h < 10f ? 0.03229f + 0.281073f * h - 0.000578f * h * t
                  : h < 50f ? 2.22749f + 0.160107f * h - 0.01478f * t
                  : 21.0606f + 0.005565f * h * h - 0.00035f * h * t - 0.483199f * h;
        return MathF.Max(0.01f, emc / 100f);
    }

    /// <summary>The fibre saturation point: what rain wets dead fuel toward, as a share (about 0.3-0.35 for
    /// wood and litter; free water beyond it runs off a fine fuel). Rain wets a fuel about four times as fast
    /// as dry air dries it [ESTIMATE].</summary>
    public const float RainSoaked = 0.35f;

    private void Moisten(float dt, in FireWeather w)
    {
        float emc = EquilibriumMoisture(w.TemperatureC, w.HumidityPercent);
        bool raining = w.RainMmPerHour > 0.1f;
        foreach (var p in _parts)
        {
            if (p.Fuel.Live || p.Burning) continue;
            float tau = MathF.Max(0.1f, p.Fuel.TimelagHours) * 3600f;
            float target = emc;
            if (raining)
            {
                // Heavier rain wets quicker; a big fuel takes far longer to soak through.
                tau *= 0.25f * MathF.Min(1f, 2f / w.RainMmPerHour) + 0.05f;
                target = RainSoaked;
            }
            p.Moisture += (target - p.Moisture) * (1f - MathF.Exp(-dt / tau));
        }
    }

    /// <summary>How ready to catch a fuel is from its water: 1 bone dry to 0 at its moisture of extinction,
    /// squared (Rothermel's damping falls about so; Schroeder's 1969 probability of ignition by a brand
    /// falls to nothing near 30 % for fine dead fuel).</summary>
    private static float Dryness(Part p)
    {
        float x = Math.Clamp(1f - p.Moisture / MathF.Max(0.05f, p.Fuel.ExtinctionMoisture), 0f, 1f);
        return x * x;
    }

    /// <summary>
    /// How much heat it must have gathered to catch. Thick: its flux-time product, times the heat of
    /// preignition wet over dry, (250 + 1116 M) / 250 (Rothermel 1972). Thin: warming its mass about 280 K
    /// (1.4 kJ/kgK) and boiling its water off (2.59 MJ/kg).
    /// </summary>
    private static float DoseNeeded(Part p) => p.Fuel.Heating == FuelHeating.Thick
        ? p.Fuel.DryDose * (250f + 1116f * p.Moisture) / 250f
        : p.Fuel.DryDose * (400f + p.Moisture * WaterCoolingKjPerKg);

    /// <summary>Van Wagner's (1977) critical surface fire intensity for a crown to catch, kW/m:
    /// (0.010 CBH (460 + 25.9 FMC))^1.5, CBH the crown's base in m, FMC its foliar moisture in %.</summary>
    public static float VanWagnerIntensity(float crownBaseMetres, float foliarMoisture)
        => MathF.Pow(0.010f * crownBaseMetres * (460f + 25.9f * foliarMoisture * 100f), 1.5f);

    /// <summary>
    /// A surface fire's rate of spread, m/s, after Rothermel (1972): the calm, dry rate times (1 + φ_w),
    /// φ_w = C (U ft/min)^B with C and B from the fine fuel's surface-to-volume ratio σ, times the moisture
    /// damping η_M = 1 − 2.59 r + 5.11 r² − 3.52 r³ (r = M / M_x). The packing term is taken as optimal.
    /// The calm, dry rate of needle litter, about 0.5 m/min, is an ESTIMATE (field: 0.3-1 m/min).
    /// </summary>
    public static float RateOfSpread(float surfaceToVolumePerMetre, float midflameWind, float moisture, float extinction, float calmMetresPerMinute = 0.5f)
    {
        float sigma = surfaceToVolumePerMetre / 3.281f;        // 1/ft
        float c = 7.47f * MathF.Exp(-0.133f * MathF.Pow(sigma, 0.55f));
        float b = 0.02526f * MathF.Pow(sigma, 0.54f);
        float u = MathF.Max(0f, midflameWind) * 196.85f;      // ft/min
        float phi = c * MathF.Pow(u, b);
        float r = Math.Clamp(moisture / MathF.Max(0.05f, extinction), 0f, 1f);
        float eta = MathF.Max(0f, 1f - 2.59f * r + 5.11f * r * r - 3.52f * r * r * r);
        return calmMetresPerMinute / 60f * (1f + phi) * eta;
    }

    /// <summary>A surface fuel's fire intensity now, kW/m: Byram's I = H w r, H 18 MJ/kg.</summary>
    private float SurfaceIntensity(Part p, double now, in FireWeather weather)
    {
        // The wind at mid-flame, under whatever stands over it: half the wind at 2 m (an open stand's
        // reduction, Andrews 2012 ESTIMATE for a crown overhead).
        float u = 0.5f * WindAt(weather, p.Centre.X, 2f, p.Centre.Z, now).Length();
        float ros = RateOfSpread(p.Fuel.SurfaceToVolume, u, p.Moisture, p.Fuel.ExtinctionMoisture);
        return 18000f * p.Fuel.SurfaceLoad * ros;
    }

    // ── Water on a fire ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rain and water by hand on a burning part: what reaches its fuel (rain falls through the flames and
    /// a tall flame boils most of it off first) takes 2.59 MJ a kilogram; set against the heat the flames
    /// feed back to the fuel (<see cref="FeedbackFlux"/>), that share of its burning goes. Held at most of
    /// it for half a minute, it is out (docs/FIRE.md 12.7).
    /// </summary>
    private void Douse(Part p, double now, float dt, in FireWeather w, float life)
    {
        float flame = p.Spec.FlameHeightMetres * MathF.Pow(MathF.Max(0.01f, life), 0.4f);
        float rain = w.RainMmPerHour / 3600f * MathF.Exp(-flame / RainEvaporationMetres);
        p.RainReaching = rain;
        float hand = now < p.WaterUntil ? p.Water : 0f;
        if (p.Fuel.WaterFlares) hand = 0f;
        float cooling = (rain + hand) * WaterCoolingKjPerKg;
        float target = Math.Clamp(cooling / FeedbackFlux, 0f, 1f);
        p.Quench += (target - p.Quench) * (1f - MathF.Exp(-dt / 10f));
        if (p.Quench > 0.85f)
        {
            if (double.IsNaN(p.WetSince)) p.WetSince = now;
            if (now - p.WetSince >= 30.0)
            {
                p.Out = true;
                p.OutAt = now;
                p.Dose = 0f;
                p.Moisture = MathF.Max(p.Moisture, p.Fuel.Live ? p.Fuel.Moisture : RainSoaked);
                _events.Add(new FireEvent(now, p.Object, _objects[p.Object].Name, p.Fuel.Name, "put out by water", IgnitionCause.Lit, -1, cooling));
            }
        }
        else p.WetSince = double.NaN;
    }

    // ── Brands ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Glowing brands: sent up by each burning part as it burns (<see cref="FuelPart.BrandsPerMJ"/>), lofted
    /// by its plume until the plume's speed, 1.1 Q^1/3 z^-1/3 m/s (McCaffrey 1979, plume region), falls to
    /// the brand's own falling speed (2.5-7 m/s, Tohidi et al. 2015), carried by the wind at their height
    /// while they rise and fall, glowing for a time that grows with their size. One landing on a part with
    /// receptivity may set it going by how dry it is. ESTIMATES: the loft's spread, the glow times, the
    /// sideways scatter (docs/FIRE.md 12.4).
    /// </summary>
    private void Brands(double now, float dt, in FireWeather weather)
    {
        // At most eight drawn a second from a part, and at most MaxLaunches from them all: past that, each
        // drawn brand stands for as many real ones as there are, so their number is kept and their cost is not.
        float wanted = 0f;
        foreach (var s in _parts)
            if (s.Burning && s.HeatKw > 0f && s.Fuel.BrandsPerMJ > 0f)
                wanted += MathF.Min(8f, s.Fuel.BrandsPerMJ * s.HeatKw * 1e-3f * dt * (1f + s.Flare));
        float room = MathF.Max(0f, MaxBrands - _brands.Count);
        float share = wanted <= 0f ? 1f : MathF.Min(1f, MathF.Min(MaxLaunches * dt, room) / wanted);
        foreach (var s in _parts)
        {
            if (!s.Burning || s.HeatKw <= 0f || s.Fuel.BrandsPerMJ <= 0f) continue;
            float expected = s.Fuel.BrandsPerMJ * s.HeatKw * 1e-3f * dt * (1f + s.Flare);
            if (expected <= 0f) continue;
            float drawn = MathF.Max(1e-6f, MathF.Min(8f, expected) * share);
            int n = Poisson(drawn);
            float weight = expected / drawn;
            for (int k = 0; k < n; k++) Launch(s, now, weather, weight);
        }
        // Landing.
        for (int i = _brands.Count - 1; i >= 0; i--)
        {
            var b = _brands[i];
            if (b.LandsAt > now) continue;
            _brands.RemoveAt(i);
            Land(b, now);
        }
    }

    private void Launch(Part s, double now, in FireWeather weather, float weight)
    {
        BrandsLaunched++;
        float vt = 2.5f + 4.5f * Random();
        float zMax = MathF.Min(400f, MathF.Pow(1.1f * MathF.Pow(s.HeatKw, 1f / 3f) / vt, 3f));
        float top = s.Centre.Y + s.Flame;
        // Most brands go only part of the way up.
        float loft = MathF.Max(0f, zMax - s.Flame) * Random() * Random();
        float height = top + loft;
        float plume = 1.1f * MathF.Pow(s.HeatKw, 1f / 3f) * MathF.Pow(MathF.Max(1f, 0.5f * (s.Flame + height - s.Centre.Y)), -1f / 3f);
        float rise = loft / MathF.Max(1f, plume - vt);
        float fall = (height - 0f) / vt;
        float t = rise + fall;
        var v = WindAt(weather, s.Centre.X, MathF.Max(1f, 0.5f * height), s.Centre.Z, now);
        float drift = v.Length() * t;
        var along = v.LengthSquared() > 1e-6f ? Vector2.Normalize(v) : Vector2.UnitX;
        var across = new Vector2(-along.Y, along.X);
        float sideways = Gaussian() * (0.25f * drift + 0.5f);
        float back = Gaussian() * (0.15f * drift + 0.3f);
        // Off the flames' edge.
        float a = MathF.Tau * Random();
        var start = new Vector2(s.Centre.X, s.Centre.Z) + s.Radius * new Vector2(MathF.Cos(a), MathF.Sin(a));
        var at = start + along * (drift + back) + across * sideways;
        // A brand of 2.5 m/s (small) glows for about 10 s, one of 7 m/s about 80 s: (v/2.5)² × 10.
        float glow = 10f * (vt / 2.5f) * (vt / 2.5f);
        _brands.Add(new Brand { LandsAt = now + t, At = at, Alive = MathF.Exp(-t / glow), Weight = weight, From = s.Object });
    }

    private void Land(in Brand b, double now)
    {
        if (b.Alive < 0.01f) return;
        foreach (int ti in Near(new Vector3(b.At.X, 0f, b.At.Y), 0.5f))
        {
            var t = _parts[ti];
            if (t.Burning || t.Burnt || t.Object == b.From || t.Fuel.Receptivity <= 0f) continue;
            if (!InPlan(t, b.At)) continue;
            float p = b.Alive * t.Fuel.Receptivity * Dryness(t);
            float chance = 1f - MathF.Pow(1f - MathF.Min(0.999f, p), b.Weight);
            if (Random() < chance) { Ignite(t, now, IgnitionCause.Ember, b.From, chance); return; }
        }
    }

    private bool InPlan(Part t, Vector2 at)
    {
        var o = _objects[t.Object];
        var rel = at - new Vector2(t.Centre.X, t.Centre.Z);
        var local = Vector2.Transform(rel, Matrix3x2.CreateRotation(o.Yaw));
        return t.Fuel.Footprint.Contains(local.X, local.Y);
    }

    // ── Catching ─────────────────────────────────────────────────────────────────────────────────

    private void Ignite(Part p, double now, IgnitionCause cause, int from, float value)
    {
        if (p.Burning) return;
        p.LitAt = now;
        p.Out = false;
        p.Burnt = false;
        p.Quench = 0f;
        p.Dose = 0f;
        p.WetSince = double.NaN;
        _events.Add(new FireEvent(now, p.Object, _objects[p.Object].Name, p.Fuel.Name, "caught", cause, from, value));
    }

    // ── What the server sends ────────────────────────────────────────────────────────────────────

    /// <summary>Every part burning, or out but still cooling (for <paramref name="coolingSeconds"/>).</summary>
    public IEnumerable<BurningPart> Burning(double now, float coolingSeconds = 180f)
    {
        foreach (var p in _parts)
        {
            if (double.IsNaN(p.LitAt) || p.Burnt) continue;
            if (p.Out && now - p.OutAt > coolingSeconds) continue;
            var o = _objects[p.Object];
            // Its bed: a fire somebody keeps is where it was put; a raised part (a crown) its middle; a
            // part on the ground the middle of its fuel (FireSpec.BedHeightMetres).
            float bed = p.Always ? 0f : p.Fuel.Base > 0f ? 0.5f * (p.Fuel.Top - p.Fuel.Base) : p.Spec.BedHeightMetres;
            var pos = p.Centre + new Vector3(0f, bed, 0f);
            yield return new BurningPart(p.Object, p.Index, p.Fuel.Preset, p.Fuel.Footprint, p.LitAt, pos, o.Yaw, !p.Out, p.Quench, p.HeatKw, p.Always);
        }
    }

    /// <summary>The sound key of a burning part, its lighting moved onto the clock it is heard by
    /// (<paramref name="clockOffset"/> = that clock less this one).</summary>
    public static string KeyOf(in BurningPart b, double clockOffset = 0)
    {
        if (b.Always || b.Preset.StartsWith("fire:", StringComparison.OrdinalIgnoreCase)) return b.Preset;
        // The preset's own shape is said by the preset.
        bool own;
        try { own = ModelLibrary.Fire(b.Preset).Outline.SameAs(b.Shape); } catch (Exception) { own = false; }
        return FireSpec.KeyFor(b.Preset, Math.Round(b.LitAt + clockOffset, 1), own ? null : b.Shape);
    }

    /// <summary>A thing's parts, for the lab and the tests: name, burning, moisture, heat gathered over
    /// what it needs, heat release now.</summary>
    public IEnumerable<(string Part, bool Burning, float Moisture, float Readiness, float HeatKw)> State(int obj)
    {
        foreach (var p in PartsOf(obj)) yield return (p.Fuel.Name, p.Burning, p.Moisture, p.Dose / MathF.Max(1e-6f, DoseNeeded(p)), p.HeatKw);
    }

    /// <summary>Whether any part of a thing has ever caught.</summary>
    public bool HasCaught(int obj) => PartsOf(obj).Any(p => !double.IsNaN(p.LitAt));

    /// <summary>When a thing's named part (or any part) caught, or NaN.</summary>
    public double CaughtAt(int obj, string? part = null)
    {
        double t = double.NaN;
        foreach (var p in PartsOf(obj))
            if ((part == null || p.Fuel.Name == part) && !double.IsNaN(p.LitAt) && !double.IsNegativeInfinity(p.LitAt))
                t = double.IsNaN(t) ? p.LitAt : Math.Min(t, p.LitAt);
        return t;
    }

    /// <summary>A line of the timeline.</summary>
    public static string Describe(in FireEvent e, double t0, IReadOnlyList<FuelObject> objects)
    {
        string from = e.From >= 0 && e.From < objects.Count ? objects[e.From].Name : "";
        string why = e.What != "caught" ? "" : e.Cause switch
        {
            IgnitionCause.Lit => " (lit)",
            IgnitionCause.Lightning => " (lightning)",
            IgnitionCause.Radiation => $" (radiant heat from {from}, {e.Value:F0} kW/m² at the end)",
            IgnitionCause.FlameContact => $" (flames from {from} reached it, {e.Value:F0} kW/m²)",
            IgnitionCause.Ember => $" (a brand from {from}, chance {e.Value:P0})",
            IgnitionCause.SurfaceFireBeneath => $" (the litter fire under it at {e.Value:F0} kW/m)",
            _ => "",
        };
        var t = TimeSpan.FromSeconds(Math.Max(0, e.Time - t0));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}  {e.ObjectName}{(e.Part.Length > 0 ? " " + e.Part : "")} {e.What}{why}");
    }

    // ── Grid and chance ──────────────────────────────────────────────────────────────────────────

    private IEnumerable<Part> PartsOf(int obj)
    {
        foreach (var p in _parts) if (p.Object == obj) yield return p;
    }

    private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

    private List<int> Cell(long key)
    {
        if (!_grid.TryGetValue(key, out var list)) { list = new List<int>(); _grid[key] = list; }
        return list;
    }

    private static IEnumerable<long> CellsOf(Vector3 c, float r)
    {
        int x0 = (int)MathF.Floor((c.X - r) / CellMetres), x1 = (int)MathF.Floor((c.X + r) / CellMetres);
        int z0 = (int)MathF.Floor((c.Z - r) / CellMetres), z1 = (int)MathF.Floor((c.Z + r) / CellMetres);
        for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) yield return Key(x, z);
    }

    private readonly List<int> _near = new();
    private readonly HashSet<int> _seen = new();

    /// <summary>The parts whose cells come within <paramref name="r"/> of a point, each once, in order.</summary>
    private List<int> Near(Vector3 c, float r)
    {
        _near.Clear();
        _seen.Clear();
        foreach (long k in CellsOf(c, r))
            if (_grid.TryGetValue(k, out var list))
                foreach (int i in list) if (_seen.Add(i)) _near.Add(i);
        return _near;
    }

    private float Random()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng >> 8) * (1f / 16777216f);
    }

    private float Gaussian()
    {
        float u = MathF.Max(1e-7f, Random()), v = Random();
        return MathF.Sqrt(-2f * MathF.Log(u)) * MathF.Cos(MathF.Tau * v);
    }

    private int Poisson(float mean)
    {
        float l = MathF.Exp(-mean), p = 1f;
        int k = 0;
        do { k++; p *= Random(); } while (p > l && k < 64);
        return k - 1;
    }
}
