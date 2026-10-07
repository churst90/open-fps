using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>
/// Where the rain is heard from: the surfaces round the listener, sampled from the sky down.
///
/// <para>Rain is every surface in the open struck at once. The survey drops a column out of the sky
/// over a polar grid round the listener (rings to <see cref="RingEdges"/>'s last edge, eight sectors
/// each) and asks what each column meets first; that, with its share of the ground and its distance,
/// is all the rain voices need (RainSynth renders the drops; RainPlate rings what is thin).</para>
///
/// <para>The columns are gathered into patches, one voice each: near and far in each compass quarter,
/// and the roof overhead. A patch is placed where its surfaces are, weighted as heard (area over
/// distance squared), so one heard from indoors comes through the walls and windows like anything
/// outside.</para>
///
/// <para>The roof overhead: when the first thing straight up from the ear is what the rain lands on,
/// you hear the sheet ringing from below (a shelter's steel drums, a concrete slab is silent). Anything
/// between (a suspended ceiling, a floor) takes what a wall takes (WallTransmission.BandGains). A tree's
/// crown is not a roof.</para>
///
/// <para>Nothing is per map: surface kinds from the materials (RainSurfaces), sizes from the geometry,
/// a car's panels from its body (VehicleBody), a crown from its FoliageSpec.</para>
/// </summary>
// TODO: wind-driven rain on walls and windows (drops fall straight down), gutters, downpipes, run-off.
public sealed class RainSurvey
{
    /// <summary>The survey's rings, m from the listener. The last edge is how far rain is heard
    /// from as anything but the far hiss its own voices already carry.</summary>
    public static readonly float[] RingEdges = { 0f, 1.2f, 2.5f, 4.5f, 7f, 11f, 17f, 26f, 40f };

    /// <summary>Rings inside this count belong to the near patches.</summary>
    public const int NearRings = 4;

    /// <summary>The slot of the roof overhead; the near patches are 1-4 (east, north, west, south)
    /// and the far ones 5-8. Under the open sky, the listener's own head and shoulders.</summary>
    public const int OverheadSlot = 0;

    /// <summary>The ring numbers the listener's head and shoulders go in as (past every ring of the
    /// ground's).</summary>
    public const int HeadRing = 100, ShouldersRing = 101;

    /// <summary>How high above the ear a column starts, m: above any roof on a map.</summary>
    public const float SkyMetres = 250f;

    /// <summary>A roof overhead whose rain would be quieter than this under heavy rain is not voiced,
    /// dB: a concrete slab. Physics, not a cut: the plate law gives a 15 cm slab about 25 dB.</summary>
    public const float SilentRoofDb = 25f;

    /// <summary>What lies between the ear and the roof over it lets less through than this (an
    /// amplitude, mid band) and the roof is not voiced: a floor slab, not a ceiling tile.</summary>
    public const float OpaqueGain = 0.003f;

    public sealed class Result
    {
        public readonly RainPatch?[] Patches = new RainPatch?[RainFeeds.Slots];
        public readonly Vector3[] Centres = new Vector3[RainFeeds.Slots];
        /// <summary>The patch is heard in plain view (or round an edge, already paid for in its areas) and
        /// takes no path but the air; one wholly behind something takes the path to it.</summary>
        public readonly bool[] Direct = new bool[RainFeeds.Slots];
        /// <summary>What the voice of the roof overhead is filtered by, per band (amplitude).</summary>
        public (float Low, float Mid, float High) OverheadEq = (1f, 1f, 1f);
        /// <summary>The roof over the ear (its entity id), zero for the open sky.</summary>
        public int OverheadEntity;
        public string OverheadMaterial = "";
        public bool OverheadIsVehicle;
        public int Columns, Candidates;
        /// <summary>The ground and the things within NearDrops.NearRings: where drops are placed one by one.</summary>
        public readonly List<NearCell> Near = new();
        /// <summary>Every column, when the survey was asked to keep them (the lab's trace).</summary>
        public List<string>? Trace;
        public double Milliseconds;

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"{Columns} columns over {Candidates} things in {Milliseconds:F2} ms; ");
            sb.Append(OverheadEntity != 0 ? $"under {OverheadMaterial} (entity {OverheadEntity}{(OverheadIsVehicle ? ", a vehicle" : "")})" : "open sky");
            for (int s = 0; s < Patches.Length; s++)
            {
                var p = Patches[s];
                if (p == null) continue;
                sb.Append($"\n    {SlotName(s),-10} at {p.ReferenceDistance,5:F1} m{(Direct[s] ? "" : ", behind")}:");
                foreach (var l in p.Layers)
                    sb.Append($" {l.Kind}/{l.Material}{(l.FromBelow ? " from below" : "")} {l.TotalArea:F1} m² G {l.ViewFactor:F2};");
            }
            return sb.ToString();
        }
    }

    /// <summary>A cell of the survey near the ear: the sector of a ring a surface fills, how much of it
    /// (m²), at what height, in which patch, and whether it is heard from below (the roof over you).</summary>
    public readonly record struct NearCell(RainLayer Surface, float Area, float Inner, float Outer, float Angle,
                                           float Width, float Top, int Slot, bool FromBelow, int Ring);

    public static string SlotName(int slot) => slot switch
    {
        0 => "overhead",
        1 => "near east", 2 => "near north", 3 => "near west", 4 => "near south",
        5 => "far east", 6 => "far north", 7 => "far west", 8 => "far south",
        _ => "?",
    };

    /// <summary>A thing a column can meet, made ready for many columns.</summary>
    private struct Thing
    {
        public int Id;
        public Vector3 Centre, Half;
        public Quaternion ToLocal;
        public bool Sphere;
        public float Radius;
        public string Material;
        public RainSurfaceKind Kind;
        public float Skin, BayA, BayB;
        public float LeafAreaIndex;
        public bool Vehicle;
        public VehicleBody? Body;
        public bool Solid;
        public Vector3 Size;
        public WallBuild Build;
    }

    private readonly List<Thing> _things = new();
    private readonly List<int> _ids = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<EntitySnapshot> _foliage = new();
    private object? _foliageFor;
    private int _foliageCount = -1;
    private readonly Dictionary<string, RainLayer>[] _layers = MakeLayerMaps();
    private readonly double[] _wSum = new double[RainFeeds.Slots];
    private readonly bool[] _seenAny = new bool[RainFeeds.Slots];
    private readonly List<ColumnRecord> _columns = new();

    private readonly record struct ColumnRecord(int Slot, int Ring, int Thing, float Area, float Distance, Vector3 At,
                                                bool FromBelow, int Under, float UnderTop, bool Seen, float Angle, bool Plain);
    private readonly Vector3[] _wPos = new Vector3[RainFeeds.Slots];

    private static Dictionary<string, RainLayer>[] MakeLayerMaps()
    {
        var d = new Dictionary<string, RainLayer>[RainFeeds.Slots];
        for (int i = 0; i < d.Length; i++) d[i] = new Dictionary<string, RainLayer>(StringComparer.Ordinal);
        return d;
    }

    /// <summary>Keep a line per column in the result, for the lab.</summary>
    public bool TraceColumns;

    /// <summary>Surveys the rain round <paramref name="ear"/>. <paramref name="ownEntityId"/> is never a
    /// roof (it is the listener's own body). <paramref name="ridingEntityId"/> is not read: the vehicle
    /// you sit in is a roof like any other thing.</summary>
    public Result Run(WorldSnapshot world, Vector3 ear, int ownEntityId, int ridingEntityId)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = new Result();
        float reach = RingEdges[^1];
        Gather(world, ear, reach + 2f, ownEntityId);
        result.Candidates = _things.Count;
        foreach (var m in _layers) m.Clear();
        Array.Clear(_seenAny);
        Array.Clear(_wSum);
        Array.Clear(_wPos);

        // ── The roof over the ear ──────────────────────────────────────────────────────────────
        var overhead = Column(ear.X, ear.Z, ear.Y, out _, out _);
        int roof = 0;
        bool roofed = false;
        if (overhead.Hit && overhead.Top > ear.Y + 0.05f && _things[overhead.Index].Kind != RainSurfaceKind.Canopy
            && _things[overhead.Index].Solid)
        {
            var roofThing = _things[overhead.Index];
            roof = roofThing.Id;
            // What is straight up from the ear first: the roof itself, or something between.
            int ceiling = Upward(ear);
            var eq = (Low: 1f, Mid: 1f, High: 1f);
            if (ceiling >= 0 && _things[ceiling].Id != roof)
            {
                var c = _things[ceiling];
                eq = WallTransmission.BandGains(AcousticRegistry.GetProperties(Known(c.Material)), c.Size, c.Build);
            }
            // A roof of two leaves (a box-section canopy, a cavity roof): the top leaf rings and the
            // rest of the build is a wall between it and the room.
            if (!roofThing.Vehicle && roofThing.Build.LeafMetres > 0f)
            {
                var whole = WallTransmission.BandGains(AcousticRegistry.GetProperties(Known(roofThing.Material)), roofThing.Size, roofThing.Build);
                var leafSize = new Vector3(roofThing.Size.X, roofThing.Size.Y, roofThing.Size.Z);
                SetThickness(ref leafSize, roofThing.Build.LeafMetres);
                var leaf = WallTransmission.BandGains(AcousticRegistry.GetProperties(Known(roofThing.Material)), leafSize, WallBuild.Solid);
                eq = (eq.Low * Ratio(whole.Low, leaf.Low), eq.Mid * Ratio(whole.Mid, leaf.Mid), eq.High * Ratio(whole.High, leaf.High));
            }
            if (eq.Mid >= OpaqueGain)
            {
                roofed = true;
                result.OverheadEntity = roof;
                result.OverheadMaterial = roofThing.Material;
                result.OverheadIsVehicle = roofThing.Vehicle;
                // Under a car's roof there is its headliner, a lining of foam and fabric.
                if (roofThing.Vehicle)
                    result.OverheadEq = (eq.Low * RainSurfaces.HeadlinerGains.Low, eq.Mid * RainSurfaces.HeadlinerGains.Mid, eq.High * RainSurfaces.HeadlinerGains.High);
                if (!roofThing.Vehicle) result.OverheadEq = eq;
            }
        }

        // ── The columns ────────────────────────────────────────────────────────────────────────
        _columns.Clear();
        int columns = 0;
        for (int ring = 0; ring < RingEdges.Length - 1; ring++)
        {
            float r1 = RingEdges[ring], r2 = RingEdges[ring + 1];
            int sectors = ring == 0 ? 4 : 8;
            float rc = MathF.Sqrt(0.5f * (r1 * r1 + r2 * r2));
            float area = MathF.PI * (r2 * r2 - r1 * r1) / sectors;
            for (int s = 0; s < sectors; s++)
            {
                // Sector centres on the compass points for the inner ring, between them beyond.
                float angle = ring == 0 ? s * MathF.PI / 2f : (s + 0.5f) * MathF.PI / 4f;
                float x = ear.X + rc * MathF.Cos(angle), z = ear.Z + rc * MathF.Sin(angle);
                var hit = Column(x, z, ear.Y, out var under, out bool hasUnder);
                columns++;
                if (!hit.Hit) continue;
                var thing = _things[hit.Index];
                if (thing.Kind == RainSurfaceKind.None) continue;
                var at = new Vector3(x, hit.Top, z);
                float d = MathF.Max(0.5f, Vector3.Distance(at, ear));
                bool overRoof = roofed && thing.Id == roof;
                int slot = overRoof ? OverheadSlot : SlotFor(angle, ring);
                float through = overRoof ? 1f : Through(ear, at + new Vector3(0f, 0.05f, 0f), hit.Index);
                bool seen = through > 0f;
                if (TraceColumns)
                    (result.Trace ??= new()).Add($"ring {ring} at {angle * 180f / MathF.PI,5:F1}°: {thing.Material} (id {thing.Id}) top {hit.Top:F2} m, {d:F1} m away, " +
                                                 $"{SlotName(slot)}, {(overRoof ? "the roof over the ear" : seen ? $"heard, {10f * MathF.Log10(MathF.Max(1e-9f, through)):F1} dB" : "behind something")}");
                _columns.Add(new ColumnRecord(slot, ring, hit.Index, seen ? area * through : area, d, at, overRoof,
                                              hasUnder ? under : -1, hasUnder ? _underTop : 0f, seen, angle, through >= 0.99f));
                if (seen) _seenAny[slot] = true;
            }
        }
        // A patch is heard from the part of it in view or round an edge (the street past a shelter's
        // glass). Only a patch wholly behind something (outside, to a listener indoors) is heard by the
        // path to it.
        foreach (var c in _columns)
        {
            if (c.Slot != OverheadSlot && _seenAny[c.Slot] && !c.Seen) continue;
            _underTop = c.UnderTop;
            // Near and heard straight (or the roof over the ear): its big drops are played one by one
            // (NearDrops), and the patch keeps the rest.
            bool near = c.Ring < NearDrops.NearRings && (c.FromBelow || c.Plain);
            _near = near ? result : null;
            _nearAngle = c.Angle;
            AddColumn(c.Slot, c.Ring, _things[c.Thing], c.Area, c.Distance, c.At, ear, c.FromBelow,
                      c.Under >= 0 ? c.Under : (int?)null, c.At.Y);
        }
        result.Columns = columns;

        // ── The listener ───────────────────────────────────────────────────────────────────────
        // Nothing over the ear but the sky: the rain lands on the listener too (RainSurfaces.HeadSquareMetres).
        if (!overhead.Hit || overhead.Top <= ear.Y + 0.05f) Body(result, ear);

        // ── Into patches ───────────────────────────────────────────────────────────────────────
        for (int slot = 0; slot < RainFeeds.Slots; slot++)
        {
            var map = _layers[slot];
            if (map.Count == 0 || _wSum[slot] <= 0) continue;
            var layers = new RainLayer[map.Count];
            map.Values.CopyTo(layers, 0);
            foreach (var l in layers) _ = l.Key;     // worked out here, not on the voice's thread
            var centre = _wPos[slot] / (float)_wSum[slot];
            float dist = MathF.Max(1f, Vector3.Distance(centre, ear));
            // A roof overhead quieter than anything could hear is not a voice.
            if (slot == OverheadSlot && roofed)
            {
                float p2 = 0f;
                foreach (var l in layers)
                    if (l.Kind == RainSurfaceKind.Plate) p2 += l.Plate.MeanSquarePressure(Rainfall.HeavyRate, l.ViewFactor);
                float db = 10f * MathF.Log10(MathF.Max(1e-20f, p2) / 4e-10f) + 20f * MathF.Log10(MathF.Max(1e-4f, result.OverheadEq.Mid));
                if (db < SilentRoofDb) { result.OverheadEntity = 0; continue; }
            }
            result.Patches[slot] = new RainPatch { Layers = layers, ReferenceDistance = dist };
            result.Centres[slot] = centre;
            result.Direct[slot] = slot == OverheadSlot || _seenAny[slot];
        }
        result.Milliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return result;
    }

    private static float Ratio(float a, float b) => b > 1e-6f ? Math.Clamp(a / b, 0f, 1f) : 1f;

    private static string Known(string material) => AcousticRegistry.IsKnown(material) ? material : "Generic";

    private static void SetThickness(ref Vector3 size, float t)
    {
        float x = MathF.Abs(size.X), y = MathF.Abs(size.Y), z = MathF.Abs(size.Z);
        if (x <= y && x <= z) size.X = t; else if (y <= x && y <= z) size.Y = t; else size.Z = t;
    }

    /// <summary>The ring patch a column belongs to: its compass quarter, near or far.</summary>
    public static int SlotFor(float angle, int ring)
    {
        int quarter = (int)MathF.Round(angle / (MathF.PI / 2f)) & 3;   // 0 east, 1 north, 2 west, 3 south
        return (ring < NearRings ? 1 : 5) + quarter;
    }

    private void AddColumn(int slot, int ring, Thing thing, float area, float d, Vector3 at, Vector3 ear,
                           bool fromBelow, int? under, float top)
    {
        float w = area / (d * d);
        // How squarely the surface faces the ear: its normal is up (RainLayer.Aim).
        float aim = (ear.Y - top) / d;
        bool discrete = _near != null;
        _wSum[slot] += w;
        _wPos[slot] += at * w;
        if (thing.Vehicle && thing.Body != null)
        {
            var body = thing.Body;
            float spanA = body.PanelSpansM.Length > 0 ? body.PanelSpansM[0] : 0.35f;
            float spanB = body.PanelSpansM.Length > 1 ? body.PanelSpansM[1] : spanA;
            float glass = RainSurfaces.CarGlassShareOfTop;
            var steel = Layer(slot, new RainLayer
            {
                Kind = RainSurfaceKind.Plate, Material = body.PanelMaterial, FromBelow = fromBelow, ModulusGPa = 200f,
                Plate = new RainPlate(body.PanelMaterial, body.PanelThicknessM, spanA, spanB, body.PanelLoss),
            });
            steel.Add(ring, area * (1f - glass), d, aim, discrete);
            Near(steel, area * (1f - glass), ring, top, slot, fromBelow);
            var pane = Layer(slot, new RainLayer
            {
                Kind = RainSurfaceKind.Plate, Material = "Glass", FromBelow = fromBelow, ModulusGPa = 70f,
                Plate = new RainPlate("Glass", RainSurfaces.CarGlassMetres, 0.9f * thing.Size.X, 0.5f, RainSurfaces.CarGlassLoss),
            });
            pane.Add(ring, area * glass, d, aim, discrete);
            Near(pane, area * glass, ring, top, slot, fromBelow);
            return;
        }
        var layer = new RainLayer { Kind = thing.Kind, Material = thing.Material, ModulusGPa = AcousticRegistry.GetProperties(Known(thing.Material)).YoungsModulusGPa };
        switch (thing.Kind)
        {
            case RainSurfaceKind.Plate:
                layer.Plate = new RainPlate(thing.Material, thing.Skin, thing.BayA, thing.BayB);
                layer.FromBelow = fromBelow;
                break;
            case RainSurfaceKind.Hard when fromBelow:
                // A slab over the ear, heard from below: the same plate law, at its own thickness.
                layer.Kind = RainSurfaceKind.Plate;
                layer.Plate = new RainPlate(thing.Material, thing.Skin, thing.BayA, thing.BayB);
                layer.FromBelow = true;
                break;
            case RainSurfaceKind.Soft:
                layer.Stretch = RainSurfaces.ContactStretch(AcousticRegistry.GetProperties(Known(thing.Material)));
                break;
            case RainSurfaceKind.Canopy:
            {
                layer.Stretch = RainSurfaces.ContactStretch(AcousticRegistry.GetProperties(Known(thing.Material)));
                layer.LeafAreaIndex = thing.LeafAreaIndex;
                if (under is int u)
                {
                    var below = _things[u];
                    var kind = below.Kind == RainSurfaceKind.Plate || below.Kind == RainSurfaceKind.Canopy
                        ? RainSurfaceKind.Hard : below.Kind;
                    layer.UnderKind = kind == RainSurfaceKind.None ? RainSurfaceKind.Soft : kind;
                    layer.UnderStretch = RainSurfaces.ContactStretch(AcousticRegistry.GetProperties(Known(below.Material)));
                    float bottom = thing.Sphere ? thing.Centre.Y - thing.Radius : thing.Centre.Y - thing.Half.Y;
                    layer.DripFallMetres = MathF.Max(0.5f, MathF.Round(2f * (bottom - _underTop)) / 2f);
                    // Leaves face every way: the aim is the drips', on the ground under the crown.
                    var ground = new Vector3(at.X, _underTop, at.Z);
                    aim = (ear.Y - _underTop) / MathF.Max(0.5f, Vector3.Distance(ground, ear));
                }
                else layer.DripFallMetres = MathF.Max(0.5f, MathF.Round(2f * (thing.Sphere ? thing.Radius : thing.Half.Y)) / 2f);
                break;
            }
        }
        var merged = Layer(slot, layer);
        merged.Add(ring, area, d, aim, discrete);
        Near(merged, area, ring, top, slot, fromBelow);
    }

    private Result? _near;
    private float _nearAngle;

    /// <summary>The head and shoulders as a soft surface in the overhead slot, all of it near: its
    /// biggest drops are placed one by one, the rest is the slot's patch.</summary>
    private void Body(Result result, Vector3 ear)
    {
        var props = AcousticRegistry.GetProperties(Known(RainSurfaces.BodyMaterial));
        var layer = Layer(OverheadSlot, new RainLayer
        {
            Kind = RainSurfaceKind.Soft, Material = RainSurfaces.BodyMaterial, ModulusGPa = props.YoungsModulusGPa,
            Stretch = RainSurfaces.ContactStretch(props),
        });
        Part(HeadRing, RainSurfaces.HeadSquareMetres, 0f, RainSurfaces.HeadRadiusMetres, ear.Y + RainSurfaces.CrownAboveEarMetres);
        Part(ShouldersRing, RainSurfaces.ShouldersSquareMetres, RainSurfaces.ShouldersInnerMetres, RainSurfaces.ShouldersOuterMetres,
             ear.Y - RainSurfaces.ShouldersBelowEarMetres);

        void Part(int ring, float area, float inner, float outer, float top)
        {
            float rc = MathF.Sqrt(0.5f * (inner * inner + outer * outer));
            float d = MathF.Sqrt(rc * rc + (top - ear.Y) * (top - ear.Y));
            layer.Add(ring, area, d, (ear.Y - top) / d, discrete: true);
            float w = area / (d * d);
            _wSum[OverheadSlot] += w;
            _wPos[OverheadSlot] += new Vector3(ear.X, top, ear.Z) * w;
            result.Near.Add(new NearCell(layer, area, inner, outer, 0f, MathF.Tau, top, OverheadSlot, false, ring));
        }
    }

    private void Near(RainLayer layer, float area, int ring, float top, int slot, bool fromBelow)
    {
        if (_near == null || area <= 0f) return;
        int sectors = ring == 0 ? 4 : 8;
        _near.Near.Add(new NearCell(layer, area, RingEdges[ring], RingEdges[ring + 1], _nearAngle,
                                    MathF.Tau / sectors, top, slot, fromBelow, ring));
    }

    private RainLayer Layer(int slot, RainLayer proto)
    {
        string key = proto.Key;
        if (_layers[slot].TryGetValue(key, out var have)) return have;
        _layers[slot][key] = proto;
        return proto;
    }

    // ── What a column meets ─────────────────────────────────────────────────────────────────────

    private readonly struct ColumnHit
    {
        public readonly bool Hit;
        public readonly int Index;
        public readonly float Top;
        public ColumnHit(int index, float top) { Hit = true; Index = index; Top = top; }
    }

    private float _underTop;

    /// <summary>The first thing a raindrop falling at (x, z) meets, and the thing under that if the
    /// first is a crown of leaves.</summary>
    private ColumnHit Column(float x, float z, float earY, out int under, out bool hasUnder)
    {
        float startY = earY + SkyMetres;
        var start = new Vector3(x, startY, z);
        int best = -1, second = -1;
        float bestTop = float.NegativeInfinity, secondTop = float.NegativeInfinity;
        for (int i = 0; i < _things.Count; i++)
        {
            float top = TopOf(_things[i], start);
            if (float.IsNegativeInfinity(top)) continue;
            if (top > bestTop) { second = best; secondTop = bestTop; best = i; bestTop = top; }
            else if (top > secondTop) { second = i; secondTop = top; }
        }
        under = -1; hasUnder = false;
        if (best < 0) return default;
        if (_things[best].Kind == RainSurfaceKind.Canopy && second >= 0)
        {
            // What the drips land on is the first thing under the crown that is not more leaves.
            int u = second;
            float uTop = secondTop;
            if (_things[u].Kind == RainSurfaceKind.Canopy)
            {
                u = -1; uTop = float.NegativeInfinity;
                for (int i = 0; i < _things.Count; i++)
                {
                    if (_things[i].Kind == RainSurfaceKind.Canopy) continue;
                    float top = TopOf(_things[i], start);
                    if (top > uTop) { u = i; uTop = top; }
                }
            }
            if (u >= 0) { under = u; hasUnder = true; _underTop = uTop; }
        }
        return new ColumnHit(best, bestTop);
    }

    /// <summary>The height a vertical line from <paramref name="start"/> down first meets the thing
    /// at, or −∞.</summary>
    private static float TopOf(in Thing t, Vector3 start)
    {
        if (t.Sphere)
        {
            float dx = start.X - t.Centre.X, dz = start.Z - t.Centre.Z;
            float h2 = t.Radius * t.Radius - dx * dx - dz * dz;
            return h2 > 0f ? t.Centre.Y + MathF.Sqrt(h2) : float.NegativeInfinity;
        }
        var ls = Vector3.Transform(start - t.Centre, t.ToLocal);
        var ld = Vector3.Transform(-Vector3.UnitY, t.ToLocal);
        if (!Slab(ls, ld, t.Half, out float dist)) return float.NegativeInfinity;
        return start.Y - dist;
    }

    /// <summary>The distance along a ray to where it enters a box centred on the origin.</summary>
    private static bool Slab(Vector3 s, Vector3 d, Vector3 half, out float dist)
    {
        float tmin = 0f, tmax = float.MaxValue;
        dist = 0f;
        for (int axis = 0; axis < 3; axis++)
        {
            float so = axis == 0 ? s.X : axis == 1 ? s.Y : s.Z;
            float dd = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            float h = axis == 0 ? half.X : axis == 1 ? half.Y : half.Z;
            if (MathF.Abs(dd) < 1e-7f)
            {
                if (so < -h || so > h) return false;
                continue;
            }
            float t1 = (-h - so) / dd, t2 = (h - so) / dd;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tmin) tmin = t1;
            if (t2 < tmax) tmax = t2;
            if (tmin > tmax) return false;
        }
        dist = tmin;
        return true;
    }

    /// <summary>A column behind something is heard round its edges if that costs no more than this
    /// at 1 kHz, dB (Maekawa); past it, it is behind the thing.</summary>
    public const float MaxEdgeLossDb = 20f;

    /// <summary>
    /// What reaches the ear from a point, as a share of its energy: 1 in plain view. Behind one thing
    /// standing across the line (a shelter's glass end, a van, a garden wall), round its top or either
    /// end, whichever is clear on both legs, at Maekawa's loss for the detour at 1 kHz,
    /// 10 log10(3 + 20 N) − 4.77 dB with N = 2δ/λ (VehicleShadow's law). 0 when no way round is clear,
    /// more than one thing is in the way, or the way round costs more than <see cref="MaxEdgeLossDb"/>.
    /// </summary>
    private float Through(Vector3 ear, Vector3 to, int except)
    {
        int blocker = -1;
        Vector3 entry = default, exit = default;
        var dir = to - ear;
        if (dir.LengthSquared() < 1e-6f) return 1f;
        for (int i = 0; i < _things.Count; i++)
        {
            if (i == except || !Crosses(i, ear, to, out var en, out var ex)) continue;
            if (blocker >= 0) return 0f;
            blocker = i; entry = en; exit = ex;
        }
        if (blocker < 0) return 1f;
        var t = _things[blocker];
        // Only a line through it side to side has an edge to get round; one down through a roof does not.
        if (MathF.Abs(entry.Y) >= t.Half.Y * 0.999f || MathF.Abs(exit.Y) >= t.Half.Y * 0.999f) return 0f;
        var rotation = Quaternion.Inverse(t.ToLocal);
        var mid = 0.5f * (entry + exit);
        bool alongX = t.Half.X >= t.Half.Z;
        const float Past = 0.15f;
        Span<Vector3> edges = stackalloc Vector3[3];
        edges[0] = new Vector3(mid.X, t.Half.Y + Past, mid.Z);
        edges[1] = alongX ? new Vector3(t.Half.X + Past, mid.Y, mid.Z) : new Vector3(mid.X, mid.Y, t.Half.Z + Past);
        edges[2] = alongX ? new Vector3(-t.Half.X - Past, mid.Y, mid.Z) : new Vector3(mid.X, mid.Y, -t.Half.Z - Past);
        float direct = dir.Length(), best = float.MaxValue;
        foreach (var e in edges)
        {
            var w = t.Centre + Vector3.Transform(e, rotation);
            if (!Clear(ear, w, blocker, except) || !Clear(w, to, blocker, except)) continue;
            best = MathF.Min(best, Vector3.Distance(ear, w) + Vector3.Distance(w, to) - direct);
        }
        if (best == float.MaxValue) return 0f;
        float n = 2f * MathF.Max(0.01f, best) * 1000f / WallTransmission.SoundSpeed;
        float lossDb = 10f * MathF.Log10(3f + 20f * n) - 4.77f;
        return lossDb > MaxEdgeLossDb ? 0f : MathF.Pow(10f, -lossDb / 10f);
    }

    /// <summary>Whether the straight line from a to b crosses solid thing <paramref name="i"/>, and where
    /// it goes in and out, in the thing's own frame. The ear inside a thing (a car you sit in) does not
    /// count as crossing it.</summary>
    private bool Crosses(int i, Vector3 a, Vector3 b, out Vector3 entry, out Vector3 exit)
    {
        entry = exit = default;
        var t = _things[i];
        if (!t.Solid || t.Sphere) return false;
        var la = Vector3.Transform(a - t.Centre, t.ToLocal);
        if (MathF.Abs(la.X) < t.Half.X && MathF.Abs(la.Y) < t.Half.Y && MathF.Abs(la.Z) < t.Half.Z) return false;
        var lb = Vector3.Transform(b - t.Centre, t.ToLocal);
        var ld = lb - la;
        if (!Slab(la, ld, t.Half, out float at) || at >= 1f) return false;
        if (!Slab(lb, -ld, t.Half, out float back)) return false;
        entry = la + ld * at;
        exit = lb - ld * back;
        return true;
    }

    /// <summary>Whether nothing solid but the two named things stands across a straight line.</summary>
    private bool Clear(Vector3 a, Vector3 b, int exceptA, int exceptB)
    {
        for (int i = 0; i < _things.Count; i++)
            if (i != exceptA && i != exceptB && Crosses(i, a, b, out _, out _)) return false;
        return true;
    }

    /// <summary>The first solid thing straight up from the ear, by index, or −1.</summary>
    private int Upward(Vector3 ear)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < _things.Count; i++)
        {
            var t = _things[i];
            if (!t.Solid || t.Sphere) continue;
            var ls = Vector3.Transform(ear - t.Centre, t.ToLocal);
            var ld = Vector3.Transform(Vector3.UnitY, t.ToLocal);
            if (Slab(ls, ld, t.Half, out float d) && d < bestD && d > 0f) { bestD = d; best = i; }
        }
        return best;
    }

    // ── What there is to meet ───────────────────────────────────────────────────────────────────

    private void Gather(WorldSnapshot world, Vector3 ear, float radius, int ownEntityId)
    {
        _things.Clear();
        if (world.StaticGrid != null)
        {
            _ids.Clear(); _seen.Clear();
            world.StaticGrid.CollectInRadius(ear, radius, _ids, _seen);
            foreach (int id in _ids)
                if (world.Entities.TryGetValue(id, out var e)) AddStatic(e);
        }
        else
        {
            foreach (var e in world.Entities.Values)
                if (e.Definition.Type == EntityType.StaticObject && e.Definition.Collider.IsSolid
                    && Vector3.DistanceSquared(e.Transform.Position, ear) < (radius + 50f) * (radius + 50f))
                    AddStatic(e);
        }
        // Leaves are not solid, so not in the grid: kept in a list of their own, found once a map.
        if (!ReferenceEquals(_foliageFor, world.AcousticMap) || _foliageCount != world.Entities.Count)
        {
            _foliageFor = world.AcousticMap;
            _foliageCount = world.Entities.Count;
            _foliage.Clear();
            foreach (var e in world.Entities.Values)
                if (e.Definition.Type == EntityType.StaticObject && !e.Definition.Collider.IsSolid
                    && string.Equals(e.Definition.Material.Material, "Foliage", StringComparison.OrdinalIgnoreCase))
                    _foliage.Add(e);
        }
        foreach (var e in _foliage)
            if (Vector3.DistanceSquared(e.Transform.Position, ear) < (radius + 15f) * (radius + 15f)) AddFoliage(e);
        // Vehicles, parked or driving, the one you ride in included.
        foreach (var e in world.DynamicEntities)
        {
            if (e.Id == ownEntityId) continue;
            string sound = e.Definition.SoundEmitter.SoundId ?? "";
            if (!sound.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) continue;
            if (Vector3.DistanceSquared(e.Transform.Position, ear) > (radius + 10f) * (radius + 10f)) continue;
            AddVehicle(e, sound[7..]);
        }
    }

    private void AddStatic(EntitySnapshot e)
    {
        var def = e.Definition;
        if (!def.Collider.IsSolid || def.Collider.Shape != ColliderShape.Box) return;
        var size = def.Collider.Size;
        if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return;
        string material = def.Material.Material ?? "Generic";
        float thickness = MathF.Min(size.X, MathF.Min(size.Y, size.Z));
        float skin = def.Acoustics.LeafMetres > 0f ? def.Acoustics.LeafMetres : thickness;
        float mid = size.X + size.Y + size.Z - thickness - MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        float big = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        _things.Add(new Thing
        {
            Id = e.Id,
            Centre = e.Transform.Position,
            Half = size * 0.5f,
            ToLocal = Quaternion.Inverse(e.Transform.Rotation),
            Material = material,
            Kind = RainSurfaces.KindOf(material, skin),
            Skin = skin,
            BayA = MathF.Min(RainSurfaces.BuiltBayMetres, big),
            BayB = MathF.Min(RainSurfaces.BuiltBayMetres, mid),
            Solid = true,
            Size = size,
            Build = new WallBuild(def.Acoustics.LeafMetres, def.Acoustics.StudSpacingMetres),
        });
    }

    private void AddFoliage(EntitySnapshot e)
    {
        var def = e.Definition;
        string sound = def.SoundEmitter.SoundId ?? "";
        if (sound.StartsWith("foliage:", StringComparison.OrdinalIgnoreCase))
        {
            // A tree: its crown is its spec's, round the point the crown is placed at.
            FoliageSpec spec;
            try { spec = FoliageSpec.ByName(sound[8..]); } catch { return; }
            _things.Add(new Thing
            {
                Id = e.Id, Centre = e.Transform.Position, Sphere = true, Radius = spec.CrownRadiusMetres,
                Material = "Foliage", Kind = RainSurfaceKind.Canopy, LeafAreaIndex = spec.LeafAreaIndex,
                ToLocal = Quaternion.Identity,
            });
            return;
        }
        var size = def.Collider.Size;
        if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return;
        _things.Add(new Thing
        {
            Id = e.Id, Centre = e.Transform.Position, Half = size * 0.5f,
            ToLocal = Quaternion.Inverse(e.Transform.Rotation),
            Material = "Foliage", Kind = RainSurfaceKind.Canopy, LeafAreaIndex = RainSurfaces.DefaultLeafAreaIndex,
            Size = size,
        });
    }

    private void AddVehicle(EntitySnapshot e, string key)
    {
        var size = e.Definition.Collider.Size;
        if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return;
        VehicleBody body;
        try { body = MachineRegistry.VehicleFor(key).Body; } catch { return; }
        var rot = e.Transform.Rotation;
        // A vehicle rests on the ground: its box stands up from where it is.
        var centre = e.Transform.Position + Vector3.Transform(new Vector3(0f, size.Y * 0.5f, 0f), rot);
        _things.Add(new Thing
        {
            Id = e.Id, Centre = centre, Half = size * 0.5f, ToLocal = Quaternion.Inverse(rot),
            Material = body.PanelMaterial, Kind = RainSurfaceKind.Plate, Vehicle = true, Body = body,
            Solid = true, Size = size, Skin = body.PanelThicknessM,
        });
    }
}

/// <summary>
/// The rain voices: the survey run as the listener moves, its patches fed to their voices, and each
/// voice placed where its patch is, by the level it measures, through the path to it.
/// </summary>
public sealed class RainField
{
    /// <summary>Voice ids: one per slot, counting down from here.</summary>
    public const int VoiceBase = -2_400_000;

    /// <summary>Surveyed again once the ear has moved this far, m, or this long has gone, s.</summary>
    public const float SurveyMetres = 1.5f, SurveySeconds = 2f;

    /// <summary>A ring patch's path is asked again this often, s, one patch a frame.</summary>
    public const float PathSeconds = 0.4f;

    /// <summary>The rain is let go this long after it stops, s, so a voice is not torn down and built
    /// again by a rate that flickers through zero.</summary>
    public const float StopAfterSeconds = 3f;

    private readonly AudioEngineFacade _audio;
    private readonly SpatialAcoustics? _acoustics;
    private readonly RainSurvey _survey = new();
    private RainSurvey.Result? _last;
    private Vector3 _surveyedAt = new(float.NaN);
    private double _surveyedWhen = double.NegativeInfinity;
    private readonly bool[] _on = new bool[RainFeeds.Slots];
    private readonly AcousticPathData?[] _paths = new AcousticPathData?[RainFeeds.Slots];
    private readonly double[] _pathAt = new double[RainFeeds.Slots];
    private int _nextPath;
    private double _dryFrom = double.NaN;
    private Precipitation _falling;
    private System.Threading.Tasks.Task<RainSurvey.Result>? _pending, _running;
    private int _surveys;
    private bool _surveyFailed;

    /// <summary>For the lab: play only this slot's voices and near drops (the roof over the ear alone); -1 all.</summary>
    internal int OnlySlot = -1;

    /// <summary>The last survey, for the log and the lab.</summary>
    public RainSurvey.Result? LastSurvey => _last;

    /// <summary>The rain voices on <paramref name="audio"/>, with paths through <paramref name="acoustics"/>
    /// when given; <paramref name="seed"/> seeds the near drops, from the clock when null (the game).</summary>
    public RainField(AudioEngineFacade audio, SpatialAcoustics? acoustics, int? seed = null)
    {
        _audio = audio;
        _acoustics = acoustics;
        _nearDrops = new NearDrops(seed ?? Environment.TickCount);
    }

    /// <summary>The survey runs on the caller's thread, so its answer is taken up on the next update every
    /// time (the emitter-stream replay). The game runs it in the background and takes it up when it lands.</summary>
    internal bool SurveyInPlace { get; init; }

    private float _humidity = 0.5f, _temperature = 20f, _pressure = 1013.25f, _airMultiplier = 1f;

    public void Update(WorldSnapshot world, Vector3 ear, double now, int listenerRegion, int ownEntityId, int ridingEntityId)
    {
        (_humidity, _temperature, _pressure, _airMultiplier) = (world.Humidity, world.Temperature, world.AirPressure, world.AirAbsorptionMultiplier);
        _rideVelocity = ridingEntityId >= 0 && world.Entities.TryGetValue(ridingEntityId, out var ride) ? ride.Velocity : (Vector3?)null;
        _ear = ear;
        var falling = world.Precipitation.Falling ? world.Precipitation
                    : new Precipitation(PrecipitationKind.Rain, world.RainRateMmPerHour);
        float rate = falling.RateMmPerHour;
        for (int s = 0; s < RainFeeds.Slots; s++) RainFeeds.Feed[s].Falling = falling;
        _falling = falling;
        if (!falling.Falling)
        {
            if (double.IsNaN(_dryFrom)) _dryFrom = now;
            if (now - _dryFrom > StopAfterSeconds) Stop();
            return;
        }
        _dryFrom = double.NaN;

        // The survey runs off the game thread: on the city (1,500 boxes in reach) it takes tens of
        // milliseconds, every sound in the world standing still. Safe because a snapshot never changes.
        if (_pending is { IsCompleted: true } done)
        {
            _pending = null;
            if (done.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
            {
                _last = done.Result;
                _lastAt = _surveyedAt;
                for (int s = 0; s < RainFeeds.Slots; s++) RainFeeds.Feed[s].Patch = _last.Patches[s];
                if (_surveys++ % 30 == 0)
                    Serilog.Log.Information("[RAIN] {Rate:F1} mm/h ({Class}): {Survey}", rate, Rainfall.Category(rate), _last.Describe());
            }
            else if (!_surveyFailed)
            {
                _surveyFailed = true;
                Serilog.Log.Warning("[RAIN] the survey failed: {Error}", done.Exception?.GetBaseException().Message);
            }
        }
        // One survey at a time, even one whose answer was thrown away: they share their scratch.
        if (_pending == null && (_running == null || _running.IsCompleted)
            && (_last == null || !(Vector3.DistanceSquared(ear, _surveyedAt) < SurveyMetres * SurveyMetres)
                || now - _surveyedWhen > SurveySeconds))
        {
            var (w, at, own, riding) = (world, ear, ownEntityId, ridingEntityId);
            _pending = _running = SurveyInPlace
                ? System.Threading.Tasks.Task.FromResult(_survey.Run(w, at, own, riding))
                : System.Threading.Tasks.Task.Run(() => _survey.Run(w, at, own, riding));
            _surveyedAt = ear;
            _surveyedWhen = now;
        }
        if (_last == null) return;

        PlayNear(ear, now, listenerRegion);

        // One ring patch's path a frame: the walls and windows between the ear and that patch.
        if (_acoustics != null)
        {
            for (int tries = 0; tries < RainFeeds.Slots; tries++)
            {
                int s = _nextPath;
                _nextPath = (_nextPath + 1) % RainFeeds.Slots;
                if (s == RainSurvey.OverheadSlot || _last.Patches[s] == null || _last.Direct[s]) continue;
                if (now - _pathAt[s] < PathSeconds) continue;
                try
                {
                    _paths[s] = _acoustics.CalculateAcousticPath(world, -1, ear, _last.Centres[s] + new Vector3(0f, 0.3f, 0f));
                }
                catch { _paths[s] = null; }
                _pathAt[s] = now;
                break;
            }
        }

        for (int s = 0; s < RainFeeds.Slots; s++)
        {
            int id = VoiceBase - s;
            var patch = OnlySlot < 0 || OnlySlot == s ? _last.Patches[s] : null;
            if (patch == null)
            {
                if (_on[s]) { _audio.StopSound(id); _on[s] = false; }
                StopParts(s);
                continue;
            }
            var e = Emitter(s, patch, _last, listenerRegion);
            if (RainFeeds.PartsFor(s) > 1)
            {
                PlayParts(s, e, patch, ear);
                continue;
            }
            if (s == RainSurvey.OverheadSlot) OnYourRoof(ref e, e.Position);
            if (_on[s] && _audio.IsPlaying(id)) _audio.UpdateSpatialAttributes(e);
            else { _audio.PlayPhysicalSoundDirect(e); _on[s] = true; }
        }
    }

    // ── A patch heard from several places: the roof over the ear, the near quarters ──────────────

    /// <summary>Voice ids for a slot's parts after the first (which is the slot's own voice): eight a slot.</summary>
    public const int PartBase = VoiceBase - 100;
    public static int PartVoiceId(int slot, int part) => part == 0 ? VoiceBase - slot : PartBase - slot * 8 - part;

    private readonly int[] _partsOn = new int[RainFeeds.Slots];
    private readonly Vector3[] _partAt = new Vector3[8];
    private readonly float[] _partShares = new float[8];

    /// <summary>How far round the point over the ear the roof's parts are placed, m: about a third of the
    /// side of the roof heard, so each part sits over its own quarter; 0.3 m at least (a car's roof) and
    /// 2.5 at most (beyond, a big roof's rain is the ring patches').</summary>
    public static float RoofReach(RainPatch patch)
    {
        float area = 0f;
        foreach (var layer in patch.Layers) area += layer.TotalArea;
        return Math.Clamp(0.35f * MathF.Sqrt(MathF.Max(0f, area)), 0.3f, 2.5f);
    }

    /// <summary>Where roof part <paramref name="part"/> of <paramref name="parts"/> is, round the point
    /// over the ear: on the diagonals, so no part is straight ahead, behind or to a side.</summary>
    public static Vector3 RoofPartAt(Vector3 middle, float reach, int part, int parts)
    {
        float a = MathF.Tau * (part + 0.5f) / Math.Max(1, parts);
        return middle + new Vector3(MathF.Cos(a) * reach, 0f, MathF.Sin(a) * reach);
    }

    /// <summary>Where part <paramref name="part"/> of <paramref name="parts"/> of a near quarter is: the
    /// patch's middle turned about the ear to the middle of that part's share, so two parts are heard
    /// 22.5° either side (none from straight ahead or behind, where both ears hear the same).</summary>
    public static Vector3 QuarterPartAt(Vector3 ear, Vector3 middle, int part, int parts)
    {
        float turn = (MathF.PI / 2f) * ((part + 0.5f) / Math.Max(1, parts) - 0.5f);
        float dx = middle.X - ear.X, dz = middle.Z - ear.Z;
        float c = MathF.Cos(turn), s = MathF.Sin(turn);
        return new Vector3(ear.X + dx * c - dz * s, middle.Y, ear.Z + dx * s + dz * c);
    }

    /// <summary>
    /// A patch as RainFeeds.PartsFor(slot) voices, each rendering its share of the area with its own drops
    /// (RainVoiceState) from its own part: the roof round the point over the ear, a near quarter across
    /// its quarter. All are placed by the whole patch's level, with the gain that keeps them together as
    /// loud as the patch from its middle (ExtendedSources.Balance).
    /// </summary>
    private void PlayParts(int slot, SpatialEmitter whole, RainPatch patch, Vector3 ear)
    {
        int parts = Math.Min(RainFeeds.PartsFor(slot), _partAt.Length);
        float reach = RoofReach(patch);
        for (int k = 0; k < parts; k++)
        {
            _partAt[k] = slot == RainSurvey.OverheadSlot ? RoofPartAt(whole.Position, reach, k, parts)
                                                         : QuarterPartAt(ear, whole.Position, k, parts);
            _partShares[k] = 1f / parts;
        }
        float balance = OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Balance(
            ear, whole.Position, _partAt.AsSpan(0, parts), _partShares.AsSpan(0, parts), whole.MinDistance, whole.Range);
        for (int k = 0; k < parts; k++)
        {
            var e = whole;
            e.EntityId = PartVoiceId(slot, k);
            e.PhysicalKey = RainFeeds.Key(slot, k);
            e.Position = _partAt[k];
            // Heard round an edge, the parts keep their spread about where the patch is heard from.
            e.ApparentPosition = whole.ApparentPosition + (_partAt[k] - whole.Position);
            e.Volume *= balance;
            if (slot == RainSurvey.OverheadSlot) OnYourRoof(ref e, _partAt[k]);
            bool on = k == 0 ? _on[slot] : k < _partsOn[slot];
            if (on && _audio.IsPlaying(e.EntityId)) _audio.UpdateSpatialAttributes(e);
            else _audio.PlayPhysicalSoundDirect(e);
        }
        _on[slot] = true;
        _partsOn[slot] = parts;
    }

    /// <summary>The vehicle you are sitting in (its velocity), or null on foot; and the ear this frame.</summary>
    private Vector3? _rideVelocity;
    private Vector3 _ear;
    /// <summary>Where the ear was when the survey in use was asked for.</summary>
    private Vector3 _lastAt;

    /// <summary>For the lab: false leaves the roof in the world, to tell what riding with the head does.</summary>
    internal static bool RoofRidesWithHead = true;

    /// <summary>
    /// The roof of the vehicle you ride in rides with your head: each part is kept where it was against
    /// the head at the survey, and moves with the vehicle. Left in the world, at 100 km/h the roof fell a
    /// metre or two behind between surveys and was Doppler-shifted about eight per cent flat.
    /// OPENFPS_CABIN_PATHS=0 leaves it in the world, as before 2026-10-06.
    /// </summary>
    private void OnYourRoof(ref SpatialEmitter e, Vector3 at)
    {
        if (!OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.Enabled || !RoofRidesWithHead
            || _rideVelocity is not { } v || !e.InsideListenersVehicle) return;
        var offset = at - _lastAt;
        e.FollowsListener = true;
        e.ListenerOffset = offset;
        e.Position = _ear + offset;
        e.ApparentPosition = e.Position;
        e.Velocity = v;
    }

    private void StopParts(int slot)
    {
        for (int k = 1; k < _partsOn[slot]; k++) _audio.StopSound(PartVoiceId(slot, k));
        _partsOn[slot] = 0;
    }

    /// <summary>The emitter for one slot: where its patch is, the level its voice measured, the path.</summary>
    internal SpatialEmitter Emitter(int slot, RainPatch patch, RainSurvey.Result survey, int listenerRegion)
    {
        float level = RainFeeds.Feed[slot].LevelDb;
        if (float.IsNaN(level)) level = 40f;
        float extent = patch.ReferenceDistance;
        var (gain, reference) = Loudness.Place(level, extent);
        var centre = survey.Centres[slot];
        var e = new SpatialEmitter
        {
            EntityId = VoiceBase - slot,
            SoundId = "rain",
            IsSynth = true,
            PhysicalKey = RainFeeds.Key(slot),
            EngineKey = "",
            Mode = PlaybackMode.LoopOne,
            Type = EmitterType.WorldLocked,
            Position = centre,
            ApparentPosition = centre,
            Volume = gain * PhysicalVoiceState.HeadroomGain(RainVoiceState.HeadroomDb),
            EarLevelDb = level,
            MinDistance = reference,
            ExtentMetres = extent,
            Range = MathF.Max(60f, Loudness.AudibleRange(level)),
            Pitch = 1f,
            EngineRunning = true,
            CarriesPath = true,
            ApertureFactor = 1f,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f,
            EffectiveDistance = extent,
            TargetRegionId = listenerRegion,
        };
        if (slot == RainSurvey.OverheadSlot)
        {
            (e.EqLow, e.EqMid, e.EqHigh) = survey.OverheadEq;
            e.InsideListenersVehicle = survey.OverheadIsVehicle;
        }
        else if (survey.Direct[slot])
        {
            // In view: only the air between, at the patch's distance (ISO 9613-1, as every path).
            (e.AirLowDb, e.AirMidDb, e.AirHighDb) = AudioPhysics.AirLossDb(extent, _humidity, _temperature, _pressure, _airMultiplier);
        }
        else if (_paths[slot] is AcousticPathData p)
        {
            e.ApparentPosition = p.ApparentPosition;
            e.Occlusion = p.Occlusion;
            e.EqLow = p.EqLow; e.EqMid = p.EqMid; e.EqHigh = p.EqHigh;
            e.AirLowDb = p.AirLowDb; e.AirMidDb = p.AirMidDb; e.AirHighDb = p.AirHighDb;
            e.ApertureFactor = p.ApertureFactor;
            e.TransmissionBleed = p.TransmissionBleed;
            e.EffectiveDistance = MathF.Max(extent, p.EffectiveDistance);
            e.TargetRegionId = p.RegionId;
        }
        return e;
    }

    // ── Near drops, one by one ──────────────────────────────────────────────────────────────────

    private readonly NearDrops _nearDrops;
    private readonly DropBank _bank = new();
    private readonly List<NearDrops.Impact> _impacts = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private double _plannedTo = double.NaN;
    private int _nearVoice;

    /// <summary>Near-drop voice ids: a pool below the patches'.</summary>
    public const int NearVoiceBase = VoiceBase - 1000, NearVoicePool = 48;

    /// <summary>Drop sounds rendered for the first time in one frame, at most: each is a few
    /// milliseconds of the synthesiser, and a new surface or a new kind asks for a dozen at once.</summary>
    private const int MakePerFrame = 2;

    /// <summary>The impacts close by in the time since the last frame, each played where it lands
    /// (NearDrops, DropBank), placed in the loudness frame of the rain it is part of (DropBank.Placement).</summary>
    private void PlayNear(Vector3 ear, double now, int listenerRegion)
    {
        if (_last == null) return;
        if (double.IsNaN(_plannedTo) || now - _plannedTo > 0.5) _plannedTo = now;
        float dt = (float)(now - _plannedTo);
        if (dt <= 0f) return;
        _impacts.Clear();
        _nearDrops.Plan(_last, _falling, ear, _plannedTo, dt, _impacts, _bank);
        _plannedTo = now;
        int made = 0;
        foreach (var impact in _impacts)
        {
            if (OnlySlot >= 0 && impact.Slot != OnlySlot) continue;
            int variant = (_nearVoice + (int)(impact.DiameterMm * 97f)) % DropBank.Variants;
            var sound = _bank.Get(impact, variant, mayMake: made < MakePerFrame);
            if (sound == null) continue;
            if (_registered.Add(sound.Id))
            {
                made++;
                if (!_audio.RegisterSynthesisedSound(sound.Id, TransientSynth.ToPcm16(sound.Pcm), DropBank.Rate)) continue;
            }
            float dist = MathF.Max(0.1f, Vector3.Distance(impact.Position, ear));
            float aim = (ear.Y - impact.Position.Y) / dist;
            float level = DropBank.LevelDb(sound, impact, aim);
            // In the loudness frame of the rain on that surface, not on its own (DropBank.Placement).
            float field = impact.Slot >= 0 && impact.Slot < RainFeeds.Slots ? RainFeeds.Feed[impact.Slot].LevelDb : float.NaN;
            var (gain, reference) = DropBank.Placement(level, field);
            var e = new SpatialEmitter
            {
                EntityId = NearVoiceBase - (_nearVoice++ % NearVoicePool),
                SoundId = sound.Id,
                Mode = PlaybackMode.Single,
                Type = EmitterType.WorldLocked,
                Position = impact.Position,
                ApparentPosition = impact.Position,
                Volume = gain,
                EarLevelDb = level,
                MinDistance = reference,
                Range = MathF.Max(10f, Loudness.AudibleRange(level)),
                Pitch = 1f,
                IsEvent = true,
                CarriesPath = true,
                ApertureFactor = 1f,
                EqLow = 1f, EqMid = 1f, EqHigh = 1f,
                EffectiveDistance = dist,
                TargetRegionId = listenerRegion,
                DelayMs = (float)Math.Max(0.0, (impact.At - now) * 1000.0),
            };
            if (impact.FromBelow && impact.Slot == RainSurvey.OverheadSlot)
            {
                (e.EqLow, e.EqMid, e.EqHigh) = _last.OverheadEq;
                e.InsideListenersVehicle = _last.OverheadIsVehicle;
            }
            _audio.Submit(e);
        }
    }

    /// <summary>Lets every rain voice go: the rain stopped, or the world was left.</summary>
    public void Stop()
    {
        for (int s = 0; s < RainFeeds.Slots; s++)
        {
            if (_on[s]) _audio.StopSound(VoiceBase - s);
            _on[s] = false;
            StopParts(s);
            RainFeeds.Feed[s].Patch = null;
            _paths[s] = null;
        }
        _last = null;
        _plannedTo = double.NaN;
        // A survey still running is of the world being left: its answer is not wanted.
        _pending = null;
    }
}
