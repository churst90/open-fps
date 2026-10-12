using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>What a face of a body's shell is, which is the surface slot of its triangles
/// (<see cref="MeshAsset.TriangleSurface"/>): whoever places the mesh gives each its material.</summary>
public enum BodyFace : byte
{
    /// <summary>A fixed panel: the bonnet, the boot, a wing, a bulkhead below a window.</summary>
    Panel,
    /// <summary>The side of the cabin below its waist: the doors.</summary>
    Door,
    /// <summary>The windscreen, the back window and the side windows.</summary>
    Glass,
    /// <summary>The top of the cabin.</summary>
    Roof,
    /// <summary>The underside.</summary>
    Floor,
}

/// <summary>
/// One length of a body, front to back, in the body's own frame (x right, y up from the road, z forward):
/// it runs from <paramref name="FromZ"/> to <paramref name="ToZ"/> and stands as high as
/// <paramref name="Top"/>. A cabin has glass above the waist and doors below it; anything else is panels.
/// </summary>
public readonly record struct BodySection(float FromZ, float ToZ, float Top, bool Cabin);

/// <summary>
/// A body as its builder is given it (docs/GEOMETRY.md, stage 6): how wide it is, how far its underside is
/// off the road, where its waist is, and its lengths from the back to the front, each touching the next.
/// Nothing here knows what kind of vehicle it is.
/// </summary>
/// <param name="Width">Across the body, metres.</param>
/// <param name="Underside">The underside's height above the road, metres.</param>
/// <param name="Waist">The height where a cabin's doors end and its glass begins, metres.</param>
/// <param name="Sections">Its lengths, rear first; each begins where the one before it ended.</param>
public sealed record BodyShellSpec(float Width, float Underside, float Waist, IReadOnlyList<BodySection> Sections);

/// <summary>
/// A body's shell as one closed mesh (docs/GEOMETRY.md, stage 6; docs/PHYSICS_BOUNDARY.md, stage 4): the
/// outside of the body, every face marked with what it is, and the points on the outside of it where the
/// body's own sources come out. One shape for whoever reads it: the acoustic scene instances it, so the body
/// stands between a listener and what is behind it and a listener inside hears the street through its glass
/// and doors; the physics' contacts meet it.
///
/// The shell is the union of its sections' boxes, and its faces are cut on one grid (the body's two sides, the
/// sections' ends, and every height a face begins or ends at), so every edge is shared by exactly two
/// triangles and the solid is closed whatever the sections' heights.
/// </summary>
public sealed class BodyShell
{
    /// <summary>How far outside the shell a source is put, metres: clear of the face it came out through, so a
    /// ray from it to a listener outside does not start in the face.</summary>
    public const float SourceClearance = 0.05f;

    public BodyShellSpec Spec { get; }

    /// <summary>The outside of the body: closed, its triangles wound counter-clockwise seen from outside, each
    /// one's surface slot its <see cref="BodyFace"/>.</summary>
    public MeshAsset Mesh { get; }

    /// <summary>Where each source given to <see cref="Build"/> comes out, in its order: where it was if that
    /// is outside the shell already, else the nearest point outside (<see cref="Outside"/>).</summary>
    public IReadOnlyList<Vector3> Sources { get; }

    private BodyShell(BodyShellSpec spec, MeshAsset mesh, Vector3[] sources)
    {
        Spec = spec; Mesh = mesh; Sources = sources;
    }

    /// <summary>
    /// The shell of <paramref name="spec"/>, with each of <paramref name="sources"/> (where a part that makes
    /// sound sits, in the body's frame) brought to the outside of it.
    /// </summary>
    public static BodyShell Build(BodyShellSpec spec, IReadOnlyList<Vector3>? sources = null)
    {
        Check(spec);
        var s = spec.Sections;
        float w = spec.Width * 0.5f;

        // The grid: the heights any face begins or ends at, and the sections' ends.
        var levels = new List<float> { spec.Underside };
        foreach (var sec in s)
        {
            if (!levels.Contains(sec.Top)) levels.Add(sec.Top);
            if (sec.Cabin && spec.Waist > spec.Underside && spec.Waist < sec.Top && !levels.Contains(spec.Waist)) levels.Add(spec.Waist);
        }
        levels.Sort();

        var vertices = new List<Vector3>();
        var index = new Dictionary<(int Side, int Level, int End), int>();
        var indices = new List<int>();
        var surfaces = new List<byte>();
        float EndZ(int end) => end < s.Count ? s[end].FromZ : s[^1].ToZ;
        int V(int side, int level, int end)
        {
            if (index.TryGetValue((side, level, end), out int i)) return i;
            i = vertices.Count;
            vertices.Add(new Vector3(side == 0 ? -w : w, levels[level], EndZ(end)));
            index[(side, level, end)] = i;
            return i;
        }
        // A quad a-b-c-d round its edge, wound so its normal is out along `outward`.
        void Quad(int a, int b, int c, int d, Vector3 outward, BodyFace face)
        {
            var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (Vector3.Dot(n, outward) < 0f) (b, d) = (d, b);
            indices.Add(a); indices.Add(b); indices.Add(c);
            indices.Add(a); indices.Add(c); indices.Add(d);
            surfaces.Add((byte)face); surfaces.Add((byte)face);
        }
        int LevelOf(float y) => levels.IndexOf(y);

        for (int i = 0; i < s.Count; i++)
        {
            var sec = s[i];
            int top = LevelOf(sec.Top);
            // Underneath and on top.
            Quad(V(0, 0, i), V(1, 0, i), V(1, 0, i + 1), V(0, 0, i + 1), -Vector3.UnitY, BodyFace.Floor);
            Quad(V(0, top, i), V(1, top, i), V(1, top, i + 1), V(0, top, i + 1), Vector3.UnitY, sec.Cabin ? BodyFace.Roof : BodyFace.Panel);
            // The two sides, band by band: a cabin's are doors to the waist and glass above it.
            for (int l = 0; l < top; l++)
            {
                var face = !sec.Cabin ? BodyFace.Panel : levels[l] >= spec.Waist ? BodyFace.Glass : BodyFace.Door;
                Quad(V(0, l, i), V(0, l + 1, i), V(0, l + 1, i + 1), V(0, l, i + 1), -Vector3.UnitX, face);
                Quad(V(1, l, i), V(1, l + 1, i), V(1, l + 1, i + 1), V(1, l, i + 1), Vector3.UnitX, face);
            }
            // Its two ends, where it stands above its neighbour or has none: a cabin's are glass above the waist
            // (the windscreen, the back window) and a panel below it.
            for (int end = 0; end < 2; end++)
            {
                int other = end == 0 ? i - 1 : i + 1;
                int from = other >= 0 && other < s.Count ? Math.Min(top, LevelOf(s[other].Top)) : 0;
                int at = end == 0 ? i : i + 1;
                for (int l = from; l < top; l++)
                {
                    var face = sec.Cabin && levels[l] >= spec.Waist ? BodyFace.Glass : BodyFace.Panel;
                    Quad(V(0, l, at), V(1, l, at), V(1, l + 1, at), V(0, l + 1, at), end == 0 ? -Vector3.UnitZ : Vector3.UnitZ, face);
                }
            }
        }

        var mesh = new MeshAsset(vertices.ToArray(), indices.ToArray(), surfaces.ToArray(), closed: true, convex: s.Count == 1);
        var shell = new BodyShell(spec, mesh, Array.Empty<Vector3>());
        var placed = new Vector3[sources?.Count ?? 0];
        for (int i = 0; i < placed.Length; i++) placed[i] = shell.Outside(sources![i]);
        return new BodyShell(spec, mesh, placed);
    }

    /// <summary>Whether a point in the body's frame is inside the shell (on a face counts as inside).</summary>
    public bool Contains(Vector3 p) => SectionAt(p) >= 0;

    private int SectionAt(Vector3 p)
    {
        if (MathF.Abs(p.X) > Spec.Width * 0.5f || p.Y < Spec.Underside) return -1;
        var s = Spec.Sections;
        for (int i = 0; i < s.Count; i++)
            if (p.Z >= s[i].FromZ && p.Z <= s[i].ToZ && p.Y <= s[i].Top) return i;
        return -1;
    }

    /// <summary>
    /// Where a source at <paramref name="p"/> comes out of the shell: <paramref name="p"/> itself when it is
    /// outside already (a tyre on the road under the body), else the nearest point outside (a tailpipe out of
    /// the back, a side pipe out of the side, a stack out of the top, an intake at the foot of the windscreen)
    /// and <see cref="SourceClearance"/> on along the way it came out.
    /// </summary>
    public Vector3 Outside(Vector3 p)
    {
        if (SectionAt(p) < 0) return p;
        var s = Spec.Sections;
        float w = Spec.Width * 0.5f;
        // Everything outside the shell is one of these: behind it, ahead of it, under it, to either side, or
        // above one of its lengths. The nearest point of each to p, and the way straight out of the shell there.
        // On a tie the first in this order is taken, so every machine picks the same one.
        var nearest = new Vector3(p.X, p.Y, s[0].FromZ);
        var normal = -Vector3.UnitZ;
        float best = Vector3.DistanceSquared(p, nearest);
        void Try(Vector3 q, Vector3 n)
        {
            float d = Vector3.DistanceSquared(p, q);
            if (d < best) { best = d; nearest = q; normal = n; }
        }
        Try(new Vector3(p.X, p.Y, s[^1].ToZ), Vector3.UnitZ);
        Try(new Vector3(p.X, Spec.Underside, p.Z), -Vector3.UnitY);
        Try(new Vector3(w, p.Y, p.Z), Vector3.UnitX);
        Try(new Vector3(-w, p.Y, p.Z), -Vector3.UnitX);
        // Above a length: straight up through its top, straight along over a lower neighbour's, or to the corner
        // where a higher length's end meets a lower one's top (the foot of a windscreen).
        foreach (var sec in s)
            Try(new Vector3(p.X, MathF.Max(p.Y, sec.Top), Math.Clamp(p.Z, sec.FromZ, sec.ToZ)), Vector3.UnitY);
        // Clear of the shell along the way it came; a part that sits in a face goes out along the face's normal.
        var way = nearest - p;
        var along = way.LengthSquared() > 1e-12f ? Vector3.Normalize(way) : normal;
        return nearest + along * SourceClearance;
    }

    private static void Check(BodyShellSpec spec)
    {
        if (!(spec.Width > 0f)) throw new ArgumentException("a body has a width", nameof(spec));
        if (spec.Sections.Count == 0) throw new ArgumentException("a body has at least one length", nameof(spec));
        for (int i = 0; i < spec.Sections.Count; i++)
        {
            var sec = spec.Sections[i];
            if (!(sec.ToZ > sec.FromZ)) throw new ArgumentException($"length {i} runs from {sec.FromZ} to {sec.ToZ}", nameof(spec));
            if (!(sec.Top > spec.Underside)) throw new ArgumentException($"length {i} is no higher than the underside", nameof(spec));
            if (i > 0 && sec.FromZ != spec.Sections[i - 1].ToZ)
                throw new ArgumentException($"length {i} begins at {sec.FromZ}, where the one before it ended at {spec.Sections[i - 1].ToZ}", nameof(spec));
        }
    }
}
