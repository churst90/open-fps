using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common.Systems;

/// <summary>
/// The gaps in a room's walls: every part of its five faces above the floor that nothing closes in, as
/// rectangles, each with the place beyond it. Each face is cut into cells along the covering walls'
/// edges (so a doorway's jambs are exact) and the map's resolution (so a gap into two rooms splits where
/// they meet); open cells looking into the same place merge into one opening. A door's leaf stands shut
/// in its doorway as a wall. The geometry decides which faces are open, except a face named as a wall
/// with no geometry on it at all: the author's word is all there is.
/// </summary>
public static class FaceOpenings
{
    /// <summary>A solid box of the scene, in the world.</summary>
    public readonly record struct Box(Vector3 Centre, Vector3 Size, Quaternion Rotation);

    /// <summary>A region's box, in the world.</summary>
    public readonly record struct Place(int Id, Vector3 Centre, Vector3 Size, Quaternion Rotation)
    {
        public float Volume => Size.X * Size.Y * Size.Z;
    }

    /// <summary>
    /// One gap. <paramref name="Rotation"/> is its frame: local X across it, Y up it, Z through it (out of
    /// the room). <paramref name="Centre"/> is in the middle of what it passes through: the wall it is cut
    /// in, or the face when nothing round it is a wall, and as far as the region's drawn face where that
    /// is not on the wall. <paramref name="Depth"/> is how deep that is.
    /// </summary>
    public readonly record struct Gap(int Face, int Beyond, Vector3 Centre, Quaternion Rotation,
                                      float Width, float Height, float Depth)
    {
        public float Area => Width * Height;
        public Vector3 Normal => Vector3.Transform(Vector3.UnitZ, Rotation);
    }

    /// <summary>The narrowest gap that is an opening, metres: half a wavelength of the mixer's middle band
    /// (<see cref="Diffraction.MidBandHz"/>). Narrower is closed to the low and middle bands, and in a map
    /// it is a misfit between two boxes.</summary>
    public static readonly float MinimumGapMetres = 343f / Diffraction.MidBandHz * 0.5f;

    /// <summary>How far from a room's face a wall may stand and still be that face's wall, metres. A
    /// region is drawn to the inside of a room; its walls are just beyond that (MapManager's survey
    /// reaches the same distance for the walls it measures).</summary>
    public const float WallReachMetres = 0.6f;

    /// <summary>The faces, in the order the codebase uses (floor, ceiling, north, south, east, west):
    /// the axis each is across, and which side of the box it is on.</summary>
    private static readonly (int Axis, float Side)[] Faces =
    {
        (1, -1f), (1, +1f), (2, +1f), (2, -1f), (0, +1f), (0, -1f),
    };

    /// <summary>
    /// The gaps in one room's faces. <paramref name="solids"/> need only be the boxes near the room;
    /// <paramref name="places"/> every region whose box could lie beyond one of its faces. A point
    /// belongs to the smallest region that holds it (SpatialService.GetRegionAt), so part of a face inside
    /// a smaller region is that region's boundary. <paramref name="resolution"/> is the map's voxel size:
    /// what lies beyond a gap is looked for two of it past the wall. <paramref name="outside"/> is what lies
    /// beyond when no region does.
    /// </summary>
    public static List<Gap> Find(Place room, int[]? materials, IReadOnlyList<Box> solids, IReadOnlyList<Place> places,
                                 float resolution, int outside)
    {
        var gaps = new List<Gap>();
        if (room.Size.X <= 0f || room.Size.Y <= 0f || room.Size.Z <= 0f) return gaps;
        resolution = MathF.Max(0.1f, resolution);
        float reach = 2f * resolution;

        var q = Quaternion.Normalize(room.Rotation == default ? Quaternion.Identity : room.Rotation);
        var inv = Quaternion.Inverse(q);
        Vector3 half = room.Size * 0.5f;

        var local = new (Vector3 C, Vector3 H)[solids.Count];
        for (int i = 0; i < solids.Count; i++)
        {
            var s = solids[i];
            var rot = inv * (s.Rotation == default ? Quaternion.Identity : s.Rotation);
            local[i] = (Vector3.Transform(s.Centre - room.Centre, inv), AxisAlignedHalfExtents(s.Size * 0.5f, rot));
        }

        // ── Where the room's surfaces really are ─────────────────────────────────────────────────
        // A region is drawn by hand, a little off its room: from the drawn box every room had a strip
        // of "gap" where the box overhung its walls. So each face moves to the inner surface of the wall
        // covering most of it. A named wall never built ends where the walls round it end (a roofless
        // demo room with "Concrete" for a ceiling ends at the wall tops).
        var lo = -half;
        var hi = half;
        var unbuilt = new bool[6];
        for (int f = 0; f < 6; f++)
        {
            var (a, side) = Faces[f];
            int u = U(a), v = V(a);
            float plane = side * C(half, a), best = 0f, inner = 0f;
            bool built = false;
            foreach (var (c, h) in local)
            {
                if (!IsWall(h, a, u, v)) continue;
                float bLo = C(c, a) - C(h, a), bHi = C(c, a) + C(h, a);
                float off = plane < bLo ? bLo - plane : plane > bHi ? plane - bHi : 0f;
                if (off > WallReachMetres) continue;
                float area = Overlap(C(c, u), C(h, u), -C(half, u), C(half, u)) * Overlap(C(c, v), C(h, v), -C(half, v), C(half, v));
                if (area <= 1e-4f) continue;
                built = true;
                // Reaching further in than a wall's reach, it stands in the room, not on this face.
                float near = MathF.Min(side * (bLo - plane), side * (bHi - plane));
                if (near < -WallReachMetres || area <= best) continue;
                best = area; inner = near;
            }
            if (!built) { unbuilt[f] = !RoomAcoustics.FaceIsOpen(materials, f); continue; }
            if (side > 0) Set(ref hi, a, C(half, a) + inner); else Set(ref lo, a, -C(half, a) - inner);
        }
        for (int f = 0; f < 6; f++)
        {
            if (!unbuilt[f]) continue;
            var (a, side) = Faces[f];
            // The furthest the walls of the faces across this one reach towards it, inside the box.
            float reachTo = float.NegativeInfinity;
            for (int g = 0; g < 6; g++)
            {
                var (b, sideG) = Faces[g];
                if (b == a || unbuilt[g]) continue;
                float planeG = sideG * C(half, b);
                foreach (var (c, h) in local)
                {
                    if (!IsWall(h, b, U(b), V(b))) continue;
                    float bLo = C(c, b) - C(h, b), bHi = C(c, b) + C(h, b);
                    float off = planeG < bLo ? bLo - planeG : planeG > bHi ? planeG - bHi : 0f;
                    if (off > WallReachMetres) continue;
                    if (Overlap(C(c, U(b)), C(h, U(b)), -C(half, U(b)), C(half, U(b))) <= 1e-4f) continue;
                    if (Overlap(C(c, V(b)), C(h, V(b)), -C(half, V(b)), C(half, V(b))) <= 1e-4f) continue;
                    reachTo = MathF.Max(reachTo, side * C(c, a) + C(h, a));
                }
            }
            if (float.IsNegativeInfinity(reachTo)) continue;
            float edge = MathF.Min(reachTo, C(half, a));
            if (side > 0) Set(ref hi, a, MathF.Max(edge, C(lo, a) + 0.01f)); else Set(ref lo, a, MathF.Min(-edge, C(hi, a) - 0.01f));
        }

        var index = new PlaceIndex(places);
        var us = new List<float>();
        var vs = new List<float>();
        var covering = new List<Cover>();
        for (int f = 1; f < 6; f++)                          // 0 is the floor: that is the ground
        {
            if (unbuilt[f]) continue;                        // the author's wall, never built
            var (a, side) = Faces[f];
            int u = U(a), v = V(a);
            float uLo = C(lo, u), uHi = C(hi, u), vLo = C(lo, v), vHi = C(hi, v);
            float plane = side > 0 ? C(hi, a) : C(lo, a);
            if (uHi - uLo < MinimumGapMetres || vHi - vLo < MinimumGapMetres) continue;

            // ── What covers it ───────────────────────────────────────────────────────────────────
            // Anything near and across the face covers it, but only a wall is cut through: a tunnel's
            // side wall and road cover slivers of its open end and are not a wall the end is cut in.
            covering.Clear();
            float deepest = 0f;
            foreach (var (c, h) in local)
            {
                float bLo = C(c, a) - C(h, a), bHi = C(c, a) + C(h, a);
                float off = plane < bLo ? bLo - plane : plane > bHi ? plane - bHi : 0f;
                if (off > WallReachMetres) continue;
                float u0 = MathF.Max(uLo, C(c, u) - C(h, u)), u1 = MathF.Min(uHi, C(c, u) + C(h, u));
                float v0 = MathF.Max(vLo, C(c, v) - C(h, v)), v1 = MathF.Min(vHi, C(c, v) + C(h, v));
                if (u1 - u0 <= 1e-4f || v1 - v0 <= 1e-4f) continue;
                bool wall = IsWall(h, a, u, v);
                float o0 = side * (bLo - plane), o1 = side * (bHi - plane);
                float outIn = MathF.Max(0f, MathF.Min(o0, o1)), outOut = MathF.Max(outIn, MathF.Max(o0, o1));
                covering.Add(new Cover(u0, u1, v0, v1, wall, outIn, outOut));
                if (wall) deepest = MathF.Max(deepest, MathF.Min(outOut, 10f));
            }

            // ── The cells ─────────────────────────────────────────────────────────────────────────
            Lines(us, uLo, uHi, resolution, covering, true);
            Lines(vs, vLo, vHi, resolution, covering, false);
            int nu = us.Count - 1, nv = vs.Count - 1;
            if (nu <= 0 || nv <= 0) continue;
            var label = new int[nu, nv];
            const int Closed = int.MinValue;
            foreach (var cover in covering)
            {
                int i0 = First(us, cover.U0), i1 = First(us, cover.U1);
                int j0 = First(vs, cover.V0), j1 = First(vs, cover.V1);
                for (int i = i0; i < i1; i++)
                for (int j = j0; j < j1; j++)
                    label[i, j] = Closed;
            }

            Vector3 n = Vector3.Normalize(Vector3.Transform(Axis(a) * side, q));
            for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                if (label[i, j] == Closed) continue;
                float cu = (us[i] + us[i + 1]) * 0.5f, cv = (vs[j] + vs[j + 1]) * 0.5f;
                var p = Vector3.Zero;
                Set(ref p, a, plane);
                Set(ref p, u, cu);
                Set(ref p, v, cv);
                Vector3 onFace = room.Centre + Vector3.Transform(p, q);
                // Only where the face is this room's own boundary: just inside, the smallest place is this.
                var inside = Vector3.Zero;
                float depthIn = MathF.Min(side * plane, C(half, a)) - MathF.Min(0.05f, C(half, a));
                Set(ref inside, a, side * depthIn);
                Set(ref inside, u, Math.Clamp(cu, -C(half, u) + 0.01f, C(half, u) - 0.01f));
                Set(ref inside, v, Math.Clamp(cv, -C(half, v) + 0.01f, C(half, v) - 0.01f));
                if (index.SmallestAt(room.Centre + Vector3.Transform(inside, q), room.Id) != room.Id) { label[i, j] = Closed; continue; }
                // What is beyond: the first place met going out, as far as two voxels past the deepest wall.
                int beyond = outside;
                for (float t = 0.5f * resolution; t <= deepest + reach + 1e-3f; t += 0.5f * resolution)
                {
                    int r = index.SmallestAt(onFace + n * t, int.MinValue);
                    if (r != int.MinValue && r != room.Id) { beyond = r; break; }
                }
                label[i, j] = beyond == room.Id || beyond == 0 ? Closed : beyond;
            }

            // ── Rectangles ────────────────────────────────────────────────────────────────────────
            var done = new bool[nu, nv];
            Vector3 across = Vector3.Normalize(Vector3.Transform(Axis(u), q));
            Vector3 up = Vector3.Normalize(Vector3.Transform(Axis(v), q));
            if (Vector3.Dot(Vector3.Cross(across, up), n) < 0f) across = -across;
            var frame = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                across.X, across.Y, across.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                n.X, n.Y, n.Z, 0f,
                0f, 0f, 0f, 1f)));
            for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int l = label[i, j];
                if (l == Closed || done[i, j]) continue;
                int i2 = i;
                while (i2 + 1 < nu && label[i2 + 1, j] == l && !done[i2 + 1, j]) i2++;
                int j2 = j;
                while (j2 + 1 < nv)
                {
                    bool row = true;
                    for (int k = i; k <= i2 && row; k++) row = label[k, j2 + 1] == l && !done[k, j2 + 1];
                    if (!row) break;
                    j2++;
                }
                for (int k = i; k <= i2; k++)
                for (int m = j; m <= j2; m++)
                    done[k, m] = true;

                float g0 = us[i], g1 = us[i2 + 1], h0 = vs[j], h1 = vs[j2 + 1];
                float w = g1 - g0, ht = h1 - h0;
                if (w < MinimumGapMetres || ht < MinimumGapMetres) continue;
                // Cut through a wall when walls close it on two opposite sides (jambs, sill and lintel),
                // as deep as they are; otherwise it is open face, with no depth.
                bool left = false, right = false, below = false, above = false;
                float dIn = float.MaxValue, dOut = 0f;
                foreach (var cv in covering)
                {
                    if (!cv.Wall) continue;
                    bool spansV = cv.V1 > h0 + 1e-3f && cv.V0 < h1 - 1e-3f;
                    bool spansU = cv.U1 > g0 + 1e-3f && cv.U0 < g1 - 1e-3f;
                    bool touches = false;
                    if (spansV && MathF.Abs(cv.U1 - g0) < 1e-3f) { left = true; touches = true; }
                    if (spansV && MathF.Abs(cv.U0 - g1) < 1e-3f) { right = true; touches = true; }
                    if (spansU && MathF.Abs(cv.V1 - h0) < 1e-3f) { below = true; touches = true; }
                    if (spansU && MathF.Abs(cv.V0 - h1) < 1e-3f) { above = true; touches = true; }
                    if (touches) { dIn = MathF.Min(dIn, cv.OutIn); dOut = MathF.Max(dOut, cv.OutOut); }
                }
                bool cut = (left && right) || (below && above);
                if (!cut) dIn = dOut = 0f;
                // Through to the drawn face too: stopping short would leave the same place on both sides.
                float drawn = C(half, a) - side * plane;
                dIn = MathF.Min(dIn, drawn);
                dOut = MathF.Max(dOut, drawn);
                var p = Vector3.Zero;
                Set(ref p, a, plane + side * (dIn + dOut) * 0.5f);
                Set(ref p, u, (g0 + g1) * 0.5f);
                Set(ref p, v, (h0 + h1) * 0.5f);
                gaps.Add(new Gap(f, l, room.Centre + Vector3.Transform(p, q), frame, w, ht, dOut - dIn));
            }
        }
        return gaps;
    }

    /// <summary>What one box covers of a face, in the face's own coordinates; whether it is a wall (thinner
    /// through the face than across it); and how far out from the face it starts and ends.</summary>
    private readonly record struct Cover(float U0, float U1, float V0, float V1, bool Wall, float OutIn, float OutOut);

    /// <summary>The cell edges along one axis of a face: its two edges, every covering box's edges on
    /// it, and the map's resolution between.</summary>
    private static void Lines(List<float> into, float lo, float hi, float resolution, List<Cover> covering, bool alongU)
    {
        into.Clear();
        into.Add(lo); into.Add(hi);
        int steps = (int)MathF.Floor((hi - lo) / resolution);
        for (int k = 1; k <= steps; k++) { float x = lo + k * resolution; if (x < hi) into.Add(x); }
        foreach (var r in covering) { into.Add(alongU ? r.U0 : r.V0); into.Add(alongU ? r.U1 : r.V1); }
        into.Sort();
        int w = 1;
        for (int k = 1; k < into.Count; k++)
            if (into[k] - into[w - 1] > 1e-4f) into[w++] = into[k];
        into.RemoveRange(w, into.Count - w);
    }

    /// <summary>The index of the first line at or past a coordinate.</summary>
    private static int First(List<float> lines, float x)
    {
        int lo = 0, hi = lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid] < x - 1e-4f) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>The places, for asking which is the smallest that holds a point.</summary>
    private sealed class PlaceIndex
    {
        private readonly int[] _id;
        private readonly Vector3[] _min, _max, _centre, _half;
        private readonly Quaternion[] _inverse;
        private readonly bool[] _turned;
        private readonly float[] _volume;

        public PlaceIndex(IReadOnlyList<Place> places)
        {
            int n = places.Count;
            _id = new int[n]; _min = new Vector3[n]; _max = new Vector3[n]; _centre = new Vector3[n]; _half = new Vector3[n];
            _inverse = new Quaternion[n]; _turned = new bool[n]; _volume = new float[n];
            for (int i = 0; i < n; i++)
            {
                var p = places[i];
                var q = p.Rotation == default ? Quaternion.Identity : Quaternion.Normalize(p.Rotation);
                _id[i] = p.Id; _centre[i] = p.Centre; _half[i] = p.Size * 0.5f; _volume[i] = p.Volume;
                _turned[i] = !q.IsIdentity;
                _inverse[i] = Quaternion.Inverse(q);
                var h = AxisAlignedHalfExtents(_half[i], q);
                _min[i] = p.Centre - h; _max[i] = p.Centre + h;
            }
        }

        /// <summary>The smallest place that holds a point, or <paramref name="none"/>.</summary>
        public int SmallestAt(Vector3 p, int none)
        {
            int best = none;
            float bestVolume = float.MaxValue;
            for (int i = 0; i < _id.Length; i++)
            {
                if (p.X < _min[i].X || p.X > _max[i].X || p.Y < _min[i].Y || p.Y > _max[i].Y || p.Z < _min[i].Z || p.Z > _max[i].Z) continue;
                if (_turned[i])
                {
                    var l = Vector3.Transform(p - _centre[i], _inverse[i]);
                    if (MathF.Abs(l.X) > _half[i].X || MathF.Abs(l.Y) > _half[i].Y || MathF.Abs(l.Z) > _half[i].Z) continue;
                }
                if (_volume[i] < bestVolume) { bestVolume = _volume[i]; best = _id[i]; }
            }
            return best;
        }
    }

    /// <summary>Half-extents of the axis-aligned box that contains a rotated one.</summary>
    public static Vector3 AxisAlignedHalfExtents(Vector3 half, Quaternion rotation)
    {
        var x = Vector3.Transform(new Vector3(half.X, 0f, 0f), rotation);
        var y = Vector3.Transform(new Vector3(0f, half.Y, 0f), rotation);
        var z = Vector3.Transform(new Vector3(0f, 0f, half.Z), rotation);
        return new Vector3(
            MathF.Abs(x.X) + MathF.Abs(y.X) + MathF.Abs(z.X),
            MathF.Abs(x.Y) + MathF.Abs(y.Y) + MathF.Abs(z.Y),
            MathF.Abs(x.Z) + MathF.Abs(y.Z) + MathF.Abs(z.Z));
    }

    /// <summary>The two axes of a face across <paramref name="a"/>: across it, and up it (along the room
    /// in a ceiling).</summary>
    private static int U(int a) => a == 0 ? 2 : 0;
    private static int V(int a) => a == 1 ? 2 : 1;

    /// <summary>A box is a wall of a face when it is thinner through the face than it is across it.</summary>
    private static bool IsWall(Vector3 h, int a, int u, int v) => C(h, a) <= MathF.Min(C(h, u), C(h, v));

    /// <summary>How much of [lo, hi] a box of this centre and half-extent covers.</summary>
    private static float Overlap(float centre, float half, float lo, float hi)
        => MathF.Max(0f, MathF.Min(centre + half, hi) - MathF.Max(centre - half, lo));

    private static Vector3 Axis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
    private static float C(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    private static void Set(ref Vector3 v, int axis, float value)
    {
        if (axis == 0) v.X = value; else if (axis == 1) v.Y = value; else v.Z = value;
    }
}
