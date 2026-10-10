using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// The faces a scene of boxes shows a ray tracer that only multiplies what each face it meets lets through
/// (Steam Audio's direct simulation), so that a construction (<see cref="Constructions"/>) is met as one
/// panel: each sheet in one has its two broad faces cut where the layers above and below it change, the
/// parts covered by another layer left out (they are inside the construction), and every part left
/// carries what the whole construction lets through there. Its four narrow sides stay as they are.
///
/// <para>Why not the boxes as they are: two faces in contact are in the same place, and which of them a
/// ray meets, and which it then steps past, is down to rounding. Measured with Steam Audio 4.8 (AudioLab
/// --layers): two 25 cm slabs in contact lost 4/3 of one slab, not two; a carpet on a slab let through
/// more than the slab alone, the carpet's faces met three times and the slab's once. With the faces in
/// contact gone and the same figure on the faces left, a construction is met exactly as one box is.</para>
/// </summary>
public static class LayeredFaces
{
    /// <summary>A box of the scene. <paramref name="Bonds"/> is false for what is hung or shaped (a door
    /// leaf, a round column): it is never a layer.</summary>
    public readonly record struct Solid(Vector3 Center, Vector3 Size, Quaternion Rotation, string Material,
                                        WallBuild Build = default, bool Bonds = true);

    /// <summary>A rectangle of a sheet's broad face open to the air: its corners in order round it, the way
    /// out, and what the construction lets through there (amplitude, per mixer band).</summary>
    public readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 Outward, Vector3 Gains);

    /// <summary>A sheet in a construction: the local axis its thickness runs along (its other four faces are
    /// its own), its broad faces as they are to be shown, and a hash of them.</summary>
    public sealed record Member(int Axis, Vector3 Normal, Face[] Faces, int Hash);

    /// <summary>What a scene's constructions look like: the sheets in one, by their index in the list given.</summary>
    public sealed class Plan
    {
        public Dictionary<int, Member> Members { get; } = new();
        /// <summary>Constructions found (sets of sheets in contact).</summary>
        public int Constructions { get; internal set; }
        public int Faces { get; internal set; }
        public double Milliseconds { get; internal set; }
        public static readonly Plan Empty = new();
    }

    private struct Frame
    {
        public int Index, Axis;
        public Vector3 Centre, N, U, V;
        public float Half, HalfU, HalfV;   // half the thickness, half the face's sides
        public Vector3 Min, Max;           // world bounds
    }

    /// <summary>The grid cell candidates for contact are filed by, metres.</summary>
    private const float Cell = 8f;
    /// <summary>A solid over more cells than this is checked against everything instead (the ground).</summary>
    private const int MaxCells = 4096;
    private const float Eps = 1e-4f;

    /// <summary>The constructions among <paramref name="solids"/> and the faces their sheets show.</summary>
    public static Plan Make(IReadOnlyList<Solid> solids)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var frames = new List<Frame>();
        for (int i = 0; i < solids.Count; i++)
        {
            var s = solids[i];
            if (!s.Bonds || s.Size.X <= 0f || s.Size.Y <= 0f || s.Size.Z <= 0f || !Constructions.IsSheet(s.Size)) continue;
            frames.Add(FrameOf(i, s));
        }
        var plan = new Plan();
        if (frames.Count < 2) return plan;

        // Pairs in contact, by a grid over their bounds.
        var parent = new int[frames.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        var grid = new Dictionary<(int, int, int), List<int>>();
        var big = new List<int>();
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var lo = Key(f.Min - new Vector3(Constructions.ContactMetres)); var hi = Key(f.Max + new Vector3(Constructions.ContactMetres));
            long cells = (long)(hi.Item1 - lo.Item1 + 1) * (hi.Item2 - lo.Item2 + 1) * (hi.Item3 - lo.Item3 + 1);
            if (cells > MaxCells) { big.Add(i); continue; }
            for (int x = lo.Item1; x <= hi.Item1; x++)
                for (int y = lo.Item2; y <= hi.Item2; y++)
                    for (int z = lo.Item3; z <= hi.Item3; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var l)) grid[(x, y, z)] = l = new List<int>(4);
                        l.Add(i);
                    }
        }
        var contacts = new List<(int, int)>();
        var seen = new HashSet<long>();
        void Try(int a, int b)
        {
            if (a == b) return;
            if (a > b) (a, b) = (b, a);
            if (!seen.Add((long)a * frames.Count + b)) return;
            if (!InContact(frames[a], frames[b])) return;
            contacts.Add((a, b));
            Union(parent, a, b);
        }
        foreach (var l in grid.Values)
            for (int i = 0; i < l.Count; i++)
                for (int j = i + 1; j < l.Count; j++) Try(l[i], l[j]);
        foreach (int b in big)
            for (int i = 0; i < frames.Count; i++)
                if (Overlaps(frames[b], frames[i])) Try(b, i);
        if (contacts.Count == 0) { plan.Milliseconds = clock.Elapsed.TotalMilliseconds; return plan; }

        var components = new Dictionary<int, List<int>>();
        foreach (var (a, b) in contacts)
        {
            foreach (int k in new[] { a, b })
            {
                int root = Find(parent, k);
                if (!components.TryGetValue(root, out var members)) components[root] = members = new List<int>();
                if (!members.Contains(k)) members.Add(k);
            }
        }
        var gainsOf = new Dictionary<string, Vector3>();
        foreach (var members in components.Values)
        {
            plan.Constructions++;
            foreach (int m in members)
            {
                var member = Faces(frames, members, m, solids, gainsOf);
                plan.Members[frames[m].Index] = member;
                plan.Faces += member.Faces.Length;
            }
        }
        plan.Milliseconds = clock.Elapsed.TotalMilliseconds;
        return plan;
    }

    private static Frame FrameOf(int index, in Solid s)
    {
        int k = Constructions.ThicknessAxis(s.Size);
        int ku = (k + 1) % 3, kv = (k + 2) % 3;
        var f = new Frame
        {
            Index = index, Axis = k, Centre = s.Center,
            N = Constructions.Axis(k, s.Rotation), U = Constructions.Axis(ku, s.Rotation), V = Constructions.Axis(kv, s.Rotation),
            Half = 0.5f * Comp(s.Size, k), HalfU = 0.5f * Comp(s.Size, ku), HalfV = 0.5f * Comp(s.Size, kv),
        };
        var ext = new Vector3(
            MathF.Abs(f.N.X) * f.Half + MathF.Abs(f.U.X) * f.HalfU + MathF.Abs(f.V.X) * f.HalfV,
            MathF.Abs(f.N.Y) * f.Half + MathF.Abs(f.U.Y) * f.HalfU + MathF.Abs(f.V.Y) * f.HalfV,
            MathF.Abs(f.N.Z) * f.Half + MathF.Abs(f.U.Z) * f.HalfU + MathF.Abs(f.V.Z) * f.HalfV);
        f.Min = s.Center - ext; f.Max = s.Center + ext;
        return f;
    }

    private static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static (int, int, int) Key(Vector3 p)
        => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

    private static bool Overlaps(in Frame a, in Frame b)
    {
        float e = Constructions.ContactMetres;
        return a.Min.X <= b.Max.X + e && b.Min.X <= a.Max.X + e && a.Min.Y <= b.Max.Y + e && b.Min.Y <= a.Max.Y + e
            && a.Min.Z <= b.Max.Z + e && b.Min.Z <= a.Max.Z + e;
    }

    /// <summary>Where <paramref name="o"/> lies in <paramref name="m"/>'s frame: its span along m's thickness
    /// and its face's extent along m's two sides, all from m's centre; false unless they face the same way
    /// with their sides lined up.</summary>
    private static bool Footprint(in Frame m, in Frame o, out float lo, out float hi, out float u0, out float u1, out float v0, out float v1)
    {
        lo = hi = u0 = u1 = v0 = v1 = 0f;
        if (!Constructions.Parallel(m.N, o.N)) return false;
        float uu = MathF.Abs(Vector3.Dot(o.U, m.U)), vu = MathF.Abs(Vector3.Dot(o.V, m.U));
        if (MathF.Max(uu, vu) < 0.9999f) return false;
        float uv = MathF.Abs(Vector3.Dot(o.U, m.V)), vv = MathF.Abs(Vector3.Dot(o.V, m.V));
        var d = o.Centre - m.Centre;
        float dn = Vector3.Dot(d, m.N), du = Vector3.Dot(d, m.U), dv = Vector3.Dot(d, m.V);
        float eu = uu * o.HalfU + vu * o.HalfV, ev = uv * o.HalfU + vv * o.HalfV;
        lo = dn - o.Half; hi = dn + o.Half;
        u0 = du - eu; u1 = du + eu; v0 = dv - ev; v1 = dv + ev;
        return true;
    }

    private static bool InContact(in Frame a, in Frame b)
    {
        if (!Footprint(a, b, out float lo, out float hi, out float u0, out float u1, out float v0, out float v1)) return false;
        float e = Constructions.ContactMetres;
        if (lo > a.Half + e || hi < -a.Half - e) return false;
        return MathF.Min(a.HalfU, u1) - MathF.Max(-a.HalfU, u0) > e && MathF.Min(a.HalfV, v1) - MathF.Max(-a.HalfV, v0) > e;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a); b = Find(parent, b);
        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
    }

    /// <summary>A construction's member as it sits under and over member m, in m's frame.</summary>
    private readonly record struct Near(int Frame, float Lo, float Hi, float U0, float U1, float V0, float V1);

    /// <summary>Sheet <paramref name="m"/>'s broad faces, cut where the layers over and under it change,
    /// the parts in contact with another layer left out.</summary>
    private static Member Faces(List<Frame> frames, List<int> members, int m, IReadOnlyList<Solid> solids, Dictionary<string, Vector3> gainsOf)
    {
        var f = frames[m];
        var near = new List<Near>();
        foreach (int o in members)
        {
            if (!Footprint(f, frames[o], out float lo, out float hi, out float u0, out float u1, out float v0, out float v1)) continue;
            if (MathF.Min(f.HalfU, u1) - MathF.Max(-f.HalfU, u0) <= Eps || MathF.Min(f.HalfV, v1) - MathF.Max(-f.HalfV, v0) <= Eps) continue;
            near.Add(new Near(o, lo, hi, u0, u1, v0, v1));
        }
        var us = Edges(near, f.HalfU, n => n.U0, n => n.U1);
        // Per side, the open rectangles and what they let through: per strip across u the open stretches
        // along v, each carried on into the next strip while it goes on there unchanged.
        var rects = new[] { new List<(float U0, float U1, float V0, float V1, Vector3 G)>(), new List<(float U0, float U1, float V0, float V1, Vector3 G)>() };
        var active = new[] { new Dictionary<(long, long, Vector3), int>(), new Dictionary<(long, long, Vector3), int>() };
        var next = new Dictionary<(long, long, Vector3), int>();
        var inStrip = new List<Near>();
        var inCell = new List<Near>();
        var run = new List<Near>();
        var cells = new[] { new List<(float V0, float V1, Vector3 G)>(), new List<(float V0, float V1, Vector3 G)>() };
        for (int iu = 0; iu + 1 < us.Count; iu++)
        {
            float ua = us[iu], ub = us[iu + 1], um = 0.5f * (ua + ub);
            inStrip.Clear();
            foreach (var n in near) if (n.U0 < um && n.U1 > um) inStrip.Add(n);
            var vs = Edges(inStrip, f.HalfV, n => n.V0, n => n.V1);
            cells[0].Clear(); cells[1].Clear();
            for (int iv = 0; iv + 1 < vs.Count; iv++)
            {
                float va = vs[iv], vb = vs[iv + 1], vm = 0.5f * (va + vb);
                inCell.Clear();
                foreach (var n in inStrip) if (n.V0 < vm && n.V1 > vm) inCell.Add(n);
                Run(inCell, m, run);
                for (int side = 0; side < 2; side++)
                {
                    if (!Open(run, m, frames, side == 0 ? +1 : -1)) continue;
                    var g = GainsOf(run, frames, solids, gainsOf);
                    var list = cells[side];
                    // A stretch running on from the last with the same figure is one.
                    if (list.Count > 0 && MathF.Abs(list[^1].V1 - va) <= Eps && list[^1].G == g)
                        list[^1] = (list[^1].V0, vb, g);
                    else list.Add((va, vb, g));
                }
            }
            for (int side = 0; side < 2; side++)
            {
                next.Clear();
                foreach (var (v0, v1, g) in cells[side])
                {
                    var key = (Q(v0), Q(v1), g);
                    if (active[side].TryGetValue(key, out int r) && MathF.Abs(rects[side][r].U1 - ua) <= Eps)
                        rects[side][r] = (rects[side][r].U0, ub, v0, v1, g);
                    else { r = rects[side].Count; rects[side].Add((ua, ub, v0, v1, g)); }
                    next[key] = r;
                }
                (active[side], next) = (next, active[side]);
            }
        }
        var faces = new List<Face>();
        var hash = new HashCode();
        hash.Add(f.Axis);
        for (int side = 0; side < 2; side++)
        {
            float sign = side == 0 ? 1f : -1f;
            var plane = f.Centre + f.N * (sign * f.Half);
            foreach (var (u0, u1, v0, v1, g) in rects[side])
            {
                var face = new Face(plane + f.U * u0 + f.V * v0, plane + f.U * u1 + f.V * v0,
                                    plane + f.U * u1 + f.V * v1, plane + f.U * u0 + f.V * v1, f.N * sign, g);
                faces.Add(face);
                hash.Add(face);
            }
        }
        return new Member(f.Axis, f.N, faces.ToArray(), hash.ToHashCode());
    }

    private static long Q(float v) => (long)MathF.Round(v / Eps);

    /// <summary>The edges of a face of half-width <paramref name="half"/> and of what lies over it, sorted,
    /// those closer than a tenth of a millimetre as one.</summary>
    private static List<float> Edges(List<Near> near, float half, Func<Near, float> lo, Func<Near, float> hi)
    {
        var e = new List<float> { -half, half };
        foreach (var n in near)
        {
            float a = lo(n), b = hi(n);
            if (a > -half && a < half) e.Add(a);
            if (b > -half && b < half) e.Add(b);
        }
        e.Sort();
        var outp = new List<float>(e.Count);
        foreach (float x in e) if (outp.Count == 0 || x - outp[^1] > Eps) outp.Add(x);
        return outp;
    }

    /// <summary>The layers in contact with sheet <paramref name="m"/> through one point of its face, m among
    /// them, sorted along its thickness.</summary>
    private static void Run(List<Near> here, int m, List<Near> run)
    {
        run.Clear();
        float e = Constructions.ContactMetres;
        Near self = default;
        foreach (var n in here) if (n.Frame == m) self = n;
        run.Add(self);
        float lo = self.Lo, hi = self.Hi;
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var n in here)
            {
                if (n.Frame == m || run.Contains(n)) continue;
                if (n.Lo <= hi + e && n.Hi >= lo - e)
                {
                    run.Add(n);
                    lo = MathF.Min(lo, n.Lo); hi = MathF.Max(hi, n.Hi);
                    grew = true;
                }
            }
        }
        run.Sort(static (a, b) => a.Lo != b.Lo ? a.Lo.CompareTo(b.Lo) : a.Frame.CompareTo(b.Frame));
    }

    /// <summary>Whether m's face on <paramref name="sign"/> (+1 the way its thickness axis points) is the
    /// construction's own face there: no layer runs on past it. Of two layers ending in the same place, the
    /// first in the list shows the face.</summary>
    private static bool Open(List<Near> run, int m, List<Frame> frames, int sign)
    {
        float e = Constructions.ContactMetres;
        Near self = default;
        foreach (var n in run) if (n.Frame == m) self = n;
        float edge = sign > 0 ? self.Hi : self.Lo;
        foreach (var n in run)
        {
            if (n.Frame == m) continue;
            float end = sign > 0 ? n.Hi : n.Lo;
            float past = sign * (end - edge);
            if (past > e) return false;
            if (MathF.Abs(end - edge) <= e && frames[n.Frame].Index < frames[m].Index) return false;
        }
        return true;
    }

    /// <summary>What a run of layers lets through, worked out once per set of layers.</summary>
    private static Vector3 GainsOf(List<Near> run, List<Frame> frames, IReadOnlyList<Solid> solids, Dictionary<string, Vector3> cache)
    {
        var key = new System.Text.StringBuilder();
        foreach (var n in run) key.Append(n.Frame).Append(',');
        string k = key.ToString();
        if (cache.TryGetValue(k, out var g)) return g;
        var layers = new List<WallTransmission.Layer>(run.Count);
        float covered = float.NegativeInfinity, heaviest = -1f, faceA = 0f, faceB = 0f;
        foreach (var n in run)
        {
            var f = frames[n.Frame];
            var s = solids[f.Index];
            var props = AcousticRegistry.GetProperties(string.IsNullOrEmpty(s.Material) ? "Generic" : s.Material);
            float t = MathF.Max(0f, n.Hi - MathF.Max(n.Lo, covered));
            covered = MathF.Max(covered, n.Hi);
            layers.Add(new WallTransmission.Layer(props, t, s.Build));
            float mass = props.DensityKgM3 * 2f * f.Half;
            if (mass > heaviest) { heaviest = mass; faceA = 2f * f.HalfU; faceB = 2f * f.HalfV; }
        }
        var (l, m, h) = run.Count == 1
            ? WallTransmission.BandGains(layers[0].Props, solids[frames[run[0].Frame].Index].Size, layers[0].Build)
            : WallTransmission.LayeredBandGains(layers, faceA, faceB);
        g = new Vector3(l, m, h);
        cache[k] = g;
        return g;
    }
}
