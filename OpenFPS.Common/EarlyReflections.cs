using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A room's first answer: a copy of the sound off every surface, by image sources (mirror the source
/// through a face; the delay is the extra distance, the loss what the face absorbed). Deterministic
/// and directional, which a parametric tail is not: a ray tracer that jittered its normals per call
/// moved a wall's reflection every frame. Driven only by the map's boxes and their materials.
/// </summary>
public static class EarlyReflections
{
    /// <summary>A surface sound can come off, in the shape the acoustic scene and
    /// <see cref="Enclosure"/> use.</summary>
    public readonly record struct Solid(Vector3 Center, Vector3 Size, Quaternion Rotation, string Material, Geometry.MeshAsset? Shape = null);

    /// <summary>One arrival: where it appears to come from, how far it travelled, what survived.</summary>
    /// <param name="ImagePosition">The mirrored source, where the ear should place this arrival.</param>
    /// <param name="PathLength">Source to surface to ear, metres.</param>
    /// <param name="ExtraDelaySeconds">How much later than the direct sound it arrives.</param>
    /// <param name="SurfaceId">Stable per surface, so a wall's reflection keeps one voice from frame to
    /// frame.</param>
    public readonly record struct Arrival(Vector3 ImagePosition, Vector3 HitPoint, float PathLength,
                                          float ExtraDelaySeconds, float GainLow, float GainMid,
                                          float GainHigh, float Scattering, int SurfaceId, int Order = 1);

    /// <summary>
    /// How many surfaces a copy may come off. Indoors a second bounce is inside the fusion window and is
    /// the reverb's; outdoors the second and third orders are the flutter between facing facades, which
    /// nothing else makes.
    /// </summary>
    public const int MaxOrder = 3;

    /// <summary>
    /// How many of the loudest first-order mirrors the higher orders are built from. Images grow as K,
    /// K² and K³ and a city has thousands of faces in range; a weak mirror's third-order copy has lost
    /// three surfaces' worth, and the geometry prunes most of the 1,700 chains before any line of sight.
    /// </summary>
    public const int HigherOrderSurfaces = 8;

    /// <summary>
    /// How far the search looks, metres: a bound on the work, not on audibility, which
    /// <see cref="MinRelativeAmplitude"/> decides. Sixty metres (<see cref="Enclosure.ReverberantRangeMetres"/>,
    /// the horizon of a reverberant field) could not reach a facade a hundred metres down a street or a
    /// grandstand across a racetrack.
    /// </summary>
    public const float RangeMetres = 200f;

    /// <summary>How many arrivals to keep, loudest first: past a handful they are closer together than
    /// the ear resolves and belong in the tail.</summary>
    public const int MaxArrivals = 4;

    /// <summary>Quietest arrival worth a voice, as a fraction of the direct sound's amplitude at the same
    /// distance.</summary>
    public const float MinRelativeAmplitude = 0.05f;

    /// <summary>
    /// The distance the direct sound is judged from when deciding whether a copy is audible: as heard,
    /// never nearer than a metre. Judged by direct / pathLength, your own clap half a metre from the ear
    /// dropped a wall six metres off (0.5 / 12 = 0.04) that the engine, flat inside its reference
    /// distance, would play at -27 dB; in Marlow flat 01F (8.65 by 17.86 m) neither end wall was placed.
    /// The gains themselves are unchanged.
    /// </summary>
    public static float HeardReference(float direct) => MathF.Max(direct, 1f);

    /// <summary>
    /// The amplitude a surface sends back from the energy it absorbs: sqrt(1 - alpha). Absorption is a
    /// share of power and every gain here is a pressure; 1 - alpha took twice the decibels, carpet 3 dB
    /// a bounce too many, 9 at third order.
    /// </summary>
    public static float Keep(float absorption) => MathF.Sqrt(1f - Math.Clamp(absorption, 0f, 1f));

    /// <summary>
    /// The gain a copy is placed with, at its image, to arrive <paramref name="relative"/> of the direct
    /// sound. The engine renders both with the source's reference distance R (flat inside R, 1/r beyond),
    /// so the copy is scaled by max(L,R)/max(d,R), not L/d: R reaches 40 m for a loud source, and a
    /// gunshot's copies inside it came out 8-11 dB hot. <paramref name="direct"/> is the true distance the
    /// relative gain was taken at.
    /// </summary>
    public static float PlacedCopyGain(float relative, float pathLength, float direct, float reference)
    {
        float r = MathF.Max(0.1f, reference);
        return Math.Clamp(relative * MathF.Max(pathLength, r) / MathF.Max(direct, r), 0f, 1f);
    }

    /// <summary>
    /// How late a copy must be to be heard as a separate arrival, seconds. Inside about 50 ms the ear
    /// fuses it with the first arrival, one wider event placed where the first came from. A fused copy
    /// must not be a voice: two voices are two unrelated reads of the same sound, and a sustained source
    /// repeats ("if I stand by the megaphone, I hear it repeat softer but in the same place"). Fused
    /// arrivals are the room's (<see cref="Enclosure"/>). The search reports every arrival; rendering
    /// decides, through <see cref="IsSeparateEvent"/>.
    /// </summary>
    public const float FusionSeconds = 0.05f;

    /// <summary>Whether this arrival is late enough to be an event of its own and worth a voice
    /// (<see cref="FusionSeconds"/>).</summary>
    public static bool IsSeparateEvent(in Arrival a) => a.ExtraDelaySeconds >= FusionSeconds;

    /// <summary>
    /// Every reflection of <paramref name="source"/> that reaches <paramref name="listener"/>, strongest
    /// first. <paramref name="into"/> is cleared and filled, so a caller on the audio worker never
    /// allocates.
    /// </summary>
    /// <param name="maxOrder">How many surfaces a copy may come off, 1 to <see cref="MaxOrder"/>. More
    /// only where copies of copies are sparse (out in the open); in a room they are the reverb's.</param>
    /// <param name="separateFirst">Rank separate events ahead of fused ones when the budget cuts: for a
    /// renderer that voices only separate events, not one that renders the near surfaces (your own
    /// footsteps off the ceiling).</param>
    /// <param name="flutter">Also follow the sound back and forth between facing facades, past
    /// <see cref="MaxOrder"/> (see <see cref="FindFlutter"/>), and keep up to
    /// <see cref="MaxFlutterArrivals"/> arrivals rather than <see cref="MaxArrivals"/>. For one-off
    /// sounds out of doors.</param>
    public static void Find(Vector3 source, Vector3 listener, IReadOnlyList<Solid> solids,
                            List<Arrival> into, float speedOfSound = 343.0f,
                            int maxOrder = 1, bool separateFirst = false, bool flutter = false, int keep = 0,
                            float maxExtraPathMetres = RangeMetres)
    {
        into.Clear();
        if (solids == null || solids.Count == 0) return;
        FindIn(new Scene(solids, null), source, listener, into, speedOfSound, maxOrder, separateFirst, flutter, keep, maxExtraPathMetres);
    }

    /// <summary>
    /// The same, asked of a triangle world's acoustic solids: near surfaces and each leg found by its
    /// tree, so the cost does not grow with the square of what is near (a call in dense woods). The
    /// mirrors are each solid's box faces; a surface's identity is its owner's.
    /// </summary>
    public static void Find(Vector3 source, Vector3 listener, Geometry.TriangleWorld world,
                            List<Arrival> into, float speedOfSound = 343.0f,
                            int maxOrder = 1, bool separateFirst = false, bool flutter = false, int keep = 0,
                            float maxExtraPathMetres = RangeMetres)
    {
        into.Clear();
        if (world == null || world.InstanceCount == 0) return;
        FindIn(new Scene(null, world), source, listener, into, speedOfSound, maxOrder, separateFirst, flutter, keep, maxExtraPathMetres);
    }

    /// <summary>The surfaces: a list of boxes (known by index) or a triangle world's acoustic layer (known
    /// by owner), the near ones handed back as boxes.</summary>
    private readonly struct Scene
    {
        public readonly IReadOnlyList<Solid>? List;
        public readonly Geometry.TriangleWorld? World;
        public Scene(IReadOnlyList<Solid>? list, Geometry.TriangleWorld? world) { List = list; World = world; }

        /// <summary>Every solid whose bounds meet the box [lo, hi], as a box and its id: the candidates a
        /// caller then tests exactly.</summary>
        public void Within(Vector3 lo, Vector3 hi, List<Solid> into, List<int> ids)
        {
            into.Clear(); ids.Clear();
            if (World != null)
            {
                var refs = _refs ??= new List<Geometry.SolidRef>(256);
                refs.Clear();
                var all = new Geometry.AcceptAll();
                World.Overlapping(lo, hi, Geometry.GeometryLayers.Acoustics, ref all, refs);
                foreach (var r in refs)
                {
                    var (c, size, rot) = World.BoxOf(r);
                    if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) continue;
                    into.Add(new Solid(c, size, rot, World.SurfaceOf(r).Material, World.ShapeOf(r)));
                    ids.Add(World.OwnerOf(r));
                }
                return;
            }
            for (int i = 0; i < List!.Count; i++) { into.Add(List[i]); ids.Add(i); }
        }

        /// <summary>Whether the straight run a-b is clear of everything but the two solids named (an id
        /// of -1 names none), among <paramref name="local"/> for a list.</summary>
        public bool Clear(Vector3 a, Vector3 b, List<Solid> local, List<int> ids, int skip, int skip2)
        {
            if (World == null) return LegIsClearAmong(a, b, local, ids, skip, skip2);
            var d = b - a;
            float len = d.Length();
            if (len < 2f * LegEndSlack) return true;
            var except = new Geometry.ExceptOwners(skip, skip2);
            // A leg begins and ends on a face: a face it only touches at an end is not in its way.
            return !World.Any(a, d / len, len - LegEndSlack, Geometry.GeometryLayers.Acoustics, Geometry.RayFaces.Both, ref except,
                              tMin: LegEndSlack);
        }
    }

    /// <summary>How near a leg's ends a face is taken to be touching it rather than in its way, metres.</summary>
    private const float LegEndSlack = 1e-4f;

    private static void FindIn(Scene scene, Vector3 source, Vector3 listener, List<Arrival> into, float speedOfSound,
                               int maxOrder, bool separateFirst, bool flutter, int keep, float maxExtraPathMetres)
    {

        float direct = Vector3.Distance(source, listener);
        if (direct < 1e-3f) return;
        float heard = HeardReference(direct);
        (_mirrors ??= new List<(Solid, int, int, float)>()).Clear();
        (_footprints ??= new List<float>()).Clear();

        // A surface on a path no longer than direct + extra lies in the ellipsoid with the source and
        // listener at its foci, inside a sphere of half that length about their midpoint. In a room
        // asked for 80 ms (27 m of extra path) that is a few dozen boxes of a city's five thousand:
        // 73 ms a footstep down to about 1.
        var near = _local ??= new List<Solid>(256);
        var nearIndex = _localIndex ??= new List<int>(256);
        var candidates = _candidates ??= new List<Solid>(256);
        var candidateIds = _candidateIds ??= new List<int>(256);
        near.Clear(); nearIndex.Clear();
        Vector3 mid = (source + listener) * 0.5f;
        float half = (direct + maxExtraPathMetres) * 0.5f;
        scene.Within(mid - new Vector3(half), mid + new Vector3(half), candidates, candidateIds);
        for (int c = 0; c < candidates.Count; c++)
        {
            var s = candidates[c];
            if (s.Size.X <= 0f || s.Size.Y <= 0f || s.Size.Z <= 0f) continue;
            float reach = half + s.Size.Length() * 0.5f;
            if (Vector3.DistanceSquared(mid, s.Center) > reach * reach) continue;
            near.Add(s); nearIndex.Add(candidateIds[c]);
        }

        for (int li = 0; li < near.Count; li++)
        {
            int i = nearIndex[li];
            var s = near[li];

            var p = AcousticRegistry.GetProperties(s.Material);
            float keepLow = Keep(p.AbsorptionLow);
            float keepMid = Keep(p.AbsorptionMid);
            float keepHigh = Keep(p.AbsorptionHigh);
            if (MathF.Max(keepLow, MathF.Max(keepMid, keepHigh)) < MinRelativeAmplitude) continue;

            for (int f = 0, faces = FaceCount(s); f < faces; f++)
            {
                if (!FacePlane(s, f, out Vector3 faceCentre, out Vector3 normal, out Vector3 uAxis,
                               out Vector3 vAxis, out float halfU, out float halfV)) continue;

                float dSource = Vector3.Dot(source - faceCentre, normal);
                float dListener = Vector3.Dot(listener - faceCentre, normal);
                // Both in front of the face: behind it is inside the solid.
                if (dSource <= 0.01f || dListener <= 0.01f) continue;

                Vector3 image = source - 2f * dSource * normal;

                // The bounce point must be on the face.
                float denom = dListener + dSource;
                if (denom < 1e-4f) continue;
                Vector3 hit = image + (listener - image) * (dSource / denom);

                Vector3 local = hit - faceCentre;
                if (MathF.Abs(Vector3.Dot(local, uAxis)) > halfU) continue;
                if (MathF.Abs(Vector3.Dot(local, vAxis)) > halfV) continue;
                if (s.Shape != null && !OnFacet(s, f, hit)) continue;    // in the rectangle round a facet, not on it

                float pathLength = Vector3.Distance(source, hit) + Vector3.Distance(hit, listener);
                if (pathLength - direct > maxExtraPathMetres) continue;   // extra path, as ImageSource.MaxPathLength

                // Spreading only: air absorption and the distance model are the renderer's.
                float spread = direct / pathLength;
                float gLow = keepLow * spread, gMid = keepMid * spread, gHigh = keepHigh * spread;
                if (MathF.Max(keepLow, MathF.Max(keepMid, keepHigh)) * heard / pathLength < MinRelativeAmplitude) continue;

                if (!scene.Clear(source, hit, near, nearIndex, i, -1)) continue;
                if (!scene.Clear(hit, listener, near, nearIndex, i, -1)) continue;

                var arrival = new Arrival(
                    image, hit, pathLength,
                    (pathLength - direct) / MathF.Max(1f, speedOfSound),
                    gLow, gMid, gHigh,
                    Math.Clamp(p.Scattering, 0f, 1f),
                    SurfaceId(i, f));
                // Two faces in the same place (a lawn flush on the ground) send one copy: the smaller
                // patch's, the one a ray meets (Geometry.Ties), then the lower id's. Two would be a
                // coherent copy of the same reflection.
                float footprint = s.Size.X * s.Size.Z;
                int same = -1;
                for (int k = 0; k < into.Count; k++)
                    if (Vector3.DistanceSquared(into[k].ImagePosition, image) < SameSurfaceSq && Vector3.DistanceSquared(into[k].HitPoint, hit) < SameSurfaceSq)
                    { same = k; break; }
                var mirrors = _mirrors ??= new List<(Solid, int, int, float)>();
                var footprints = _footprints ??= new List<float>();
                if (same >= 0)
                {
                    if (!new Geometry.Ties(footprints[same], mirrors[same].Solid).Beats(footprint, i)) continue;
                    into[same] = arrival; mirrors[same] = (s, i, f, gMid); footprints[same] = footprint;
                    continue;
                }
                into.Add(arrival);
                mirrors.Add((s, i, f, gMid));
                footprints.Add(footprint);
            }
        }

        if (Math.Min(maxOrder, MaxOrder) >= 2)
            FindHigherOrders(scene, source, listener, direct, near, nearIndex, into, speedOfSound, Math.Min(maxOrder, MaxOrder), maxExtraPathMetres);
        if (flutter)
            FindFlutter(scene, source, listener, direct, into, speedOfSound);

        // Separate events first, then loudest. Loudest alone keeps the ground and the nearest wall,
        // inside the fusion window and never played, and cuts the far facade's slapback and the flutter.
        into.Sort((a, b) =>
        {
            bool sa = separateFirst && IsSeparateEvent(a), sb = separateFirst && IsSeparateEvent(b);
            if (sa != sb) return sb.CompareTo(sa);
            float ea = MathF.Max(a.GainLow, MathF.Max(a.GainMid, a.GainHigh));
            float eb = MathF.Max(b.GainLow, MathF.Max(b.GainMid, b.GainHigh));
            return eb.CompareTo(ea);
        });
        if (keep <= 0) keep = flutter ? MaxFlutterArrivals : MaxArrivals;
        if (into.Count > keep) into.RemoveRange(keep, into.Count - keep);

        // Back into surface order, so a voice slot means the same wall from tick to tick: in loudness
        // order two near-equal arrivals swap slots when the listener shifts, and each voice jumps.
        into.Sort(static (a, b) => a.SurfaceId.CompareTo(b.SurfaceId));
    }

    /// <summary>One mirror a higher-order copy can come off: a face, and what it keeps per band.</summary>
    private readonly record struct Mirror(int Solid, int Face, Vector3 Centre, Vector3 Normal,
                                          Vector3 U, Vector3 V, float HalfU, float HalfV,
                                          float KeepLow, float KeepMid, float KeepHigh, float Scattering);

    [ThreadStatic] private static List<(Mirror M, float Score)>? _mirrorScratch;
    [ThreadStatic] private static Vector3[]? _images, _hits;
    [ThreadStatic] private static int[]? _chain;
    [ThreadStatic] private static List<(Solid Box, int Solid, int Face, float Gain)>? _mirrors;
    [ThreadStatic] private static List<Solid>? _local, _candidates;
    [ThreadStatic] private static List<Geometry.SolidRef>? _refs;
    [ThreadStatic] private static List<float>? _footprints;

    /// <summary>Two copies whose images and points of reflection are within a millimetre came off the same
    /// place: squared, metres.</summary>
    private const float SameSurfaceSq = 1e-6f;
    [ThreadStatic] private static List<int>? _localIndex, _candidateIds;

    /// <summary>
    /// The copies of copies: every chain of two or three of the strongest mirrors that sends the sound
    /// to the ear. Mirror through each face in turn, then walk back from the ear through them; a crossing
    /// off its face or a blocked leg and the chain is not a path.
    /// </summary>
    private static void FindHigherOrders(Scene scene, Vector3 source, Vector3 listener, float direct,
                                         List<Solid> local, List<int> index,
                                         List<Arrival> into, float speedOfSound,
                                         int maxOrder, float maxExtraPathMetres)
    {
        // The mirrors are the faces that gave a valid first-order copy. Scoring faces by size and
        // distance picked the ground and the floor slabs inside the towers on Main Street, and not one
        // chain survived.
        var cand = _mirrorScratch ??= new List<(Mirror, float)>(64);
        cand.Clear();
        foreach (var (s, si, f, gain) in _mirrors ?? new List<(Solid, int, int, float)>())
        {
            if (!FacePlane(s, f, out var c, out var n, out var u, out var v, out float hu, out float hv)) continue;
            var p = AcousticRegistry.GetProperties(s.Material);
            cand.Add((new Mirror(si, f, c, n, u, v, hu, hv,
                                 Keep(p.AbsorptionLow), Keep(p.AbsorptionMid),
                                 Keep(p.AbsorptionHigh), Math.Clamp(p.Scattering, 0f, 1f)), -gain));
        }
        if (cand.Count < 2) return;
        cand.Sort(static (a, b) => a.Score.CompareTo(b.Score));
        int k = Math.Min(HigherOrderSurfaces, cand.Count);

        var images = _images ??= new Vector3[MaxOrder + 1];
        var hits = _hits ??= new Vector3[MaxOrder + 1];
        var chain = _chain ??= new int[MaxOrder];

        void Try(int order)
        {
            images[0] = source;
            float keepL = 1f, keepM = 1f, keepH = 1f, scatter = 0f;
            for (int j = 0; j < order; j++)
            {
                var m = cand[chain[j]].M;
                float d = Vector3.Dot(images[j] - m.Centre, m.Normal);
                if (d <= 0.01f) return;                       // mirrored from behind: no such copy
                images[j + 1] = images[j] - 2f * d * m.Normal;
                keepL *= m.KeepLow; keepM *= m.KeepMid; keepH *= m.KeepHigh;
                scatter = MathF.Max(scatter, m.Scattering);
            }
            var last = cand[chain[order - 1]].M;
            if (Vector3.Dot(listener - last.Centre, last.Normal) <= 0.01f) return;

            float pathLength = Vector3.Distance(images[order], listener);
            if (pathLength - direct > maxExtraPathMetres || pathLength <= direct) return;
            float spread = direct / pathLength;
            if (MathF.Max(keepL, MathF.Max(keepM, keepH)) * HeardReference(direct) / pathLength < MinRelativeAmplitude) return;

            // Back from the ear: where the line to each image crosses its face.
            Vector3 toward = listener;
            for (int j = order - 1; j >= 0; j--)
            {
                var m = cand[chain[j]].M;
                Vector3 from = images[j + 1];
                float denom = Vector3.Dot(toward - from, m.Normal);
                if (MathF.Abs(denom) < 1e-5f) return;
                float t = Vector3.Dot(m.Centre - from, m.Normal) / denom;
                if (t <= 0f || t >= 1f) return;
                Vector3 hit = from + (toward - from) * t;
                Vector3 local = hit - m.Centre;
                if (MathF.Abs(Vector3.Dot(local, m.U)) > m.HalfU || MathF.Abs(Vector3.Dot(local, m.V)) > m.HalfV) return;
                hits[j] = hit;
                toward = hit;
            }

            // Every leg clear of everything but the faces it runs between.
            Vector3 prev = source;
            for (int j = 0; j <= order; j++)
            {
                Vector3 next = j < order ? hits[j] : listener;
                int skipA = j > 0 ? cand[chain[j - 1]].M.Solid : -1;
                int skipB = j < order ? cand[chain[j]].M.Solid : -1;
                if (!scene.Clear(prev, next, local, index, skipA, skipB)) return;
                prev = next;
            }

            int id = FirstOrderIdSpace;
            for (int j = 0; j < order; j++)
                id = unchecked(id * 31 + SurfaceId(cand[chain[j]].M.Solid, cand[chain[j]].M.Face) + 1);
            id = FirstOrderIdSpace + (int)((uint)id % (uint)(int.MaxValue - FirstOrderIdSpace));

            into.Add(new Arrival(images[order], hits[order - 1], pathLength,
                                 (pathLength - direct) / MathF.Max(1f, speedOfSound),
                                 keepL * spread, keepM * spread, keepH * spread, scatter, id, order));
        }

        for (int a = 0; a < k; a++)
        for (int b = 0; b < k; b++)
        {
            if (b == a) continue;                              // a plane cannot mirror its own image
            chain[0] = a; chain[1] = b;
            Try(2);
            if (maxOrder < 3) continue;
            for (int c = 0; c < k; c++)
            {
                if (c == b) continue;
                chain[2] = c;
                Try(3);
            }
        }
    }

    // ── Flutter ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Most crossings of a street a copy is followed through. At a street's width a crossing
    /// apart, sixteen of them is the better part of a second on Main Street.</summary>
    public const int FlutterMaxOrder = 16;

    /// <summary>How many arrivals a flutter search keeps, loudest separate events first.</summary>
    public const int MaxFlutterArrivals = 24;

    /// <summary>How much face a cluster needs to count as a wall of the street, square metres: a
    /// shopfront or two, not a railing.</summary>
    public const float MinWallAreaSquareMetres = 150f;

    /// <summary>Furthest a flutter copy is followed, metres: a second of sound.</summary>
    public const float FlutterRangeMetres = 343f;

    /// <summary>
    /// The flutter: a sound handed back and forth across a street, each copy a street's width later and
    /// weaker and more smeared, until it is a roll. A street is two planes, each every building along
    /// one side, so the chain search (three surfaces, eight faces) cannot follow it. Vertical faces are
    /// grouped into walls; facing pairs are streets; the path back from the ear must land on some
    /// building at every crossing (a gap is where the sound leaves), each leg clear, keeping what that
    /// building keeps and scattering a little more per bounce.
    /// </summary>
    private static void FindFlutter(Scene scene, Vector3 source, Vector3 listener, float direct,
                                    List<Arrival> into, float speedOfSound)
    {
        var byNormal = _planes ??= new Dictionary<long, List<(Mirror M, float Offset)>>();
        foreach (var l in byNormal.Values) l.Clear();
        var solids = _flutterSolids ??= new List<Solid>(256);
        var solidIds = _flutterIds ??= new List<int>(256);
        // Within half the widest street (60 m) of the line from the source to the ear, plus a solid's size.
        scene.Within(Vector3.Min(source, listener) - new Vector3(60f + MaxSolidReach), Vector3.Max(source, listener) + new Vector3(60f + MaxSolidReach),
                     solids, solidIds);
        for (int si = 0; si < solids.Count; si++)
        {
            int i = solidIds[si];
            var s = solids[si];
            if (s.Size.X <= 0f || s.Size.Y <= 0f || s.Size.Z <= 0f) continue;
            float reach = 60f + s.Size.Length() * 0.5f;
            if (DistanceSquaredToSegment(s.Center, source, listener) > reach * reach) continue;
            for (int f = 0, faces = FaceCount(s); f < faces; f++)
            {
                if (s.Shape == null && (f == 2 || f == 3)) continue;        // tops and bottoms
                if (!FacePlane(s, f, out var c, out var n, out var u, out var v, out float hu, out float hv)) continue;
                if (MathF.Abs(n.Y) > 0.2f) continue;                          // walls, not floors
                if (Vector3.Dot(source - c, n) <= 0.01f || Vector3.Dot(listener - c, n) <= 0.01f) continue;
                long key = ((long)MathF.Round(n.X * 20f) & 0xFF) | (((long)MathF.Round(n.Z * 20f) & 0xFF) << 8);
                if (!byNormal.TryGetValue(key, out var list)) byNormal[key] = list = new List<(Mirror, float)>();
                var p = AcousticRegistry.GetProperties(s.Material);
                list.Add((new Mirror(i, f, c, n, u, v, hu, hv,
                                     Keep(p.AbsorptionLow), Keep(p.AbsorptionMid),
                                     Keep(p.AbsorptionHigh), Math.Clamp(p.Scattering, 0f, 1f)),
                          Vector3.Dot(listener - c, n)));
            }
        }

        // A wall is every face facing that way within 1.5 m of the nearest (glass set back from brick,
        // a door in its reveal): exactly coplanar, a copy at head height landed on nothing at every shop
        // window. Per direction, the nearest wall in front of the listener bounds their street.
        var walls = _planeList ??= new List<List<(Mirror M, float Offset)>>();
        walls.Clear();
        foreach (var l in byNormal.Values)
        {
            if (l.Count == 0) continue;
            l.Sort(static (x, y) => x.Offset.CompareTo(y.Offset));
            // The first cluster with a wall's worth of face: a railing or a bollard is nearer than the
            // buildings and is not the street.
            int i = 0;
            while (i < l.Count)
            {
                int j = i;
                float area = 0f;
                while (j < l.Count && l[j].Offset <= l[i].Offset + 1.5f)
                {
                    area += 4f * l[j].M.HalfU * l[j].M.HalfV;
                    j++;
                }
                if (area >= MinWallAreaSquareMetres)
                {
                    var wall = new List<(Mirror M, float Offset)>(j - i);
                    for (int k = i; k < j; k++) wall.Add(l[k]);
                    walls.Add(wall);
                    break;
                }
                i = j;
            }
        }
        var groups = walls;
        if (groups.Count < 2) return;

        var images = _flImages ??= new Vector3[FlutterMaxOrder + 1];
        var hitFace = _flFaces ??= new Mirror[FlutterMaxOrder];
        var hits = _flHits ??= new Vector3[FlutterMaxOrder];
        var pairList = _pairScratch ??= new List<(int A, int B, float Width)>();
        pairList.Clear();
        for (int a = 0; a < groups.Count; a++)
        for (int b = a + 1; b < groups.Count; b++)
        {
            var na0 = groups[a][0].M.Normal; var nb0 = groups[b][0].M.Normal;
            if (Vector3.Dot(na0, nb0) > -0.97f) continue;                     // not facing each other
            float w = groups[a][0].Offset + groups[b][0].Offset;              // listener to each wall
            if (w < 3f || w > 120f) continue;
            pairList.Add((a, b, w));
        }
        pairList.Sort(static (x, y) => x.Width.CompareTo(y.Width));
        if (pairList.Count == 0) return;

        // Only what stands between the two walls, along the stretch from the source to the ear, can
        // block a leg: found once per street, since testing each leg against everything in range (both
        // facades' every storey) cost 30 ms a shot on Main Street, 160 at worst.
        var legSolids = _legSolids ??= new List<Solid>();
        var localIndex = _legIndex ??= new List<int>();
        var streetSolids = _streetSolids ??= new List<Solid>();
        var streetIds = _streetIds ??= new List<int>();
        for (int pi = 0; pi < Math.Min(2, pairList.Count); pi++)
        {
            int a = pairList[pi].A, b = pairList[pi].B;
            var na = groups[a][0].M.Normal;
            float width = pairList[pi].Width;
            legSolids.Clear(); localIndex.Clear();
            {
                var wa = groups[a][0].M; var wb = groups[b][0].M;
                scene.Within(Vector3.Min(source, listener) - new Vector3(width + MaxSolidReach), Vector3.Max(source, listener) + new Vector3(width + MaxSolidReach),
                             streetSolids, streetIds);
                for (int si = 0; si < streetSolids.Count; si++)
                {
                    var sd = streetSolids[si];
                    if (Vector3.Dot(sd.Center - wa.Centre, wa.Normal) <= 0.3f) continue;
                    if (Vector3.Dot(sd.Center - wb.Centre, wb.Normal) <= 0.3f) continue;
                    float r = sd.Size.Length() * 0.5f + width;
                    if (DistanceSquaredToSegment(sd.Center, source, listener) > r * r) continue;
                    legSolids.Add(sd); localIndex.Add(streetIds[si]);
                }
            }
            FlutterTrace?.Invoke($"pair {a}/{b}: width {width:F1}, faces {groups[a].Count}/{groups[b].Count}, normal {na}");
            for (int start = 0; start < 2; start++)
            {
                var first = start == 0 ? groups[a] : groups[b];
                var second = start == 0 ? groups[b] : groups[a];
                images[0] = source;
                for (int n = 1; n <= FlutterMaxOrder; n++)
                {
                    var plane = (n % 2 == 1) ? first : second;
                    var pm = plane[0].M;
                    float d = Vector3.Dot(images[n - 1] - pm.Centre, pm.Normal);
                    images[n] = images[n - 1] - 2f * d * pm.Normal;
                    if (n <= MaxOrder) continue;                               // the chain search has these
                    float path = Vector3.Distance(images[n], listener);
                    if (path - direct > FlutterRangeMetres) break;
                    float spread = direct / path;
                    if (HeardReference(direct) / path < MinRelativeAmplitude) break;

                    // Back from the ear: which building each crossing hit.
                    Vector3 toward = listener;
                    bool ok = true;
                    float keepL = 1f, keepM = 1f, keepH = 1f, clean = 1f;
                    for (int j = n; j >= 1 && ok; j--)
                    {
                        var pl = (j % 2 == 1) ? first : second;
                        var m0 = pl[0].M;
                        Vector3 from = images[j];
                        float denom = Vector3.Dot(toward - from, m0.Normal);
                        if (MathF.Abs(denom) < 1e-5f) { ok = false; break; }
                        float t = Vector3.Dot(m0.Centre - from, m0.Normal) / denom;
                        if (t <= 0f || t >= 1f) { ok = false; break; }
                        Vector3 hit = from + (toward - from) * t;
                        bool landed = false;
                        foreach (var (m, _) in pl)
                        {
                            // In the face's own plane: a set-back shopfront catches the copy that
                            // lands on its patch of the wall line.
                            Vector3 local = hit - m.Centre;
                            local -= Vector3.Dot(local, m.Normal) * m.Normal;
                            if (MathF.Abs(Vector3.Dot(local, m.U)) > m.HalfU + 0.05f || MathF.Abs(Vector3.Dot(local, m.V)) > m.HalfV + 0.05f) continue;
                            hitFace[j - 1] = m; landed = true;
                            keepL *= m.KeepLow; keepM *= m.KeepMid; keepH *= m.KeepHigh;
                            clean *= 1f - m.Scattering * 0.5f;
                            break;
                        }
                        if (!landed) { FlutterTrace?.Invoke($"  start {start} order {n}: crossing {j} at {hit} landed on nothing"); ok = false; break; }
                        hits[j - 1] = hit;
                        toward = hit;
                    }
                    if (!ok) continue;
                    if (MathF.Max(keepL, MathF.Max(keepM, keepH)) * HeardReference(direct) / path < MinRelativeAmplitude) break;

                    Vector3 prev = source;
                    for (int j = 0; j <= n && ok; j++)
                    {
                        Vector3 next = j < n ? hits[j] : listener;
                        int skipA = j > 0 ? hitFace[j - 1].Solid : -1;
                        int skipB = j < n ? hitFace[j].Solid : -1;
                        if (!LegIsClearAmong(prev, next, legSolids, localIndex, skipA, skipB)) { ok = false; FlutterTrace?.Invoke($"  start {start} order {n}: leg {j} blocked"); }
                        prev = next;
                    }
                    if (!ok) continue;

                    // Every crossing scatters some of what is left: a mirror at the first, a wash by the tenth.
                    float scatter = Math.Clamp(1f - clean * MathF.Pow(0.85f, n), 0f, 1f);
                    int id = FirstOrderIdSpace + (int)((uint)unchecked((a * 7919 + b) * 131 + start * 37 + n) % (uint)(int.MaxValue - FirstOrderIdSpace));
                    into.Add(new Arrival(images[n], hits[n - 1], path, (path - direct) / MathF.Max(1f, speedOfSound),
                                         keepL * spread, keepM * spread, keepH * spread, scatter, id, n));
                }
            }
        }
    }

    /// <summary>Diagnostics for tests: what the flutter search made of the planes and why chains died.</summary>
    public static Action<string>? FlutterTrace;

    [ThreadStatic] private static Dictionary<long, List<(Mirror M, float Offset)>>? _planes;
    [ThreadStatic] private static List<List<(Mirror M, float Offset)>>? _planeList;
    [ThreadStatic] private static List<(int A, int B, float Width)>? _pairScratch;
    [ThreadStatic] private static List<Solid>? _legSolids, _flutterSolids, _streetSolids;
    [ThreadStatic] private static List<int>? _legIndex, _flutterIds, _streetIds;

    /// <summary>How far a solid's centre can lie from its nearest point, at most, for the bounds a search
    /// asks the tree with: half the diagonal of the biggest solid a map holds that is not ground (a
    /// 300 m slab). A solid bigger than that is only ever ground, which has no wall faces.</summary>
    private const float MaxSolidReach = 220f;

    private static float DistanceSquaredToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / MathF.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
        return Vector3.DistanceSquared(p, a + ab * t);
    }

    /// <summary>Whether the run a-b is clear of every solid in a pre-filtered list but the two skipped,
    /// named by their original index.</summary>
    private static bool LegIsClearAmong(Vector3 a, Vector3 b, List<Solid> local, List<int> index, int skip, int skip2)
    {
        for (int i = 0; i < local.Count; i++)
        {
            int k = index[i];
            if (k == skip || k == skip2) continue;
            var s = local[i];
            if (GeometryUtils.LineIntersectsOBB(a, b, s.Center, s.Size, s.Rotation)) return false;
        }
        return true;
    }
    [ThreadStatic] private static Vector3[]? _flImages, _flHits;
    [ThreadStatic] private static Mirror[]? _flFaces;

    /// <summary>First-order surface ids live below this; a chain's id is hashed above it, so the two
    /// can never name the same voice.</summary>
    private const int FirstOrderIdSpace = 1 << 28;

    /// <summary>A surface's identity for the life of a scene, box and face, so a wall's reflection keeps
    /// its voice as the listener moves.</summary>
    public static int SurfaceId(int solidIndex, int face)
        => face < 6 ? solidIndex * 6 + face : (1 << 27) | (int)((uint)unchecked(solidIndex * 4099 + face) % (1u << 27));

    /// <summary>How many faces a solid mirrors from: a box's six, or a shape's facets (docs/GEOMETRY.md 3.4).</summary>
    private static int FaceCount(in Solid s) => s.Shape?.Facets.Items.Length ?? 6;

    /// <summary>Whether a point on a facet's plane is on the facet itself, not only in the rectangle round it.</summary>
    private static bool OnFacet(in Solid s, int face, Vector3 world)
    {
        var local = Vector3.Transform(world - s.Center, Quaternion.Conjugate(s.Rotation));
        return s.Shape!.Facets.Contains(face, local, 1e-3f);
    }

    /// <summary>One face of a box in world space: centre, outward normal, in-plane axes and half extents; for a
    /// shape, one facet's plane and the rectangle round it.</summary>
    private static bool FacePlane(in Solid s, int face, out Vector3 centre, out Vector3 normal,
                                  out Vector3 uAxis, out Vector3 vAxis, out float halfU, out float halfV)
    {
        if (s.Shape is { } shape)
        {
            var f = shape.Facets.Items[face];
            halfU = f.HalfU.Length(); halfV = f.HalfV.Length();
            if (halfU <= 0f || halfV <= 0f) { centre = default; normal = default; uAxis = default; vAxis = default; return false; }
            normal = Vector3.Transform(f.Normal, s.Rotation);
            uAxis = Vector3.Transform(f.HalfU / halfU, s.Rotation);
            vAxis = Vector3.Transform(f.HalfV / halfV, s.Rotation);
            centre = s.Center + Vector3.Transform(f.RectCentre, s.Rotation);
            return true;
        }
        Vector3 h = s.Size * 0.5f;
        Vector3 ln, lu, lv;
        switch (face)
        {
            case 0: ln = new Vector3(1, 0, 0); lu = new Vector3(0, 1, 0); lv = new Vector3(0, 0, 1); halfU = h.Y; halfV = h.Z; break;
            case 1: ln = new Vector3(-1, 0, 0); lu = new Vector3(0, 1, 0); lv = new Vector3(0, 0, 1); halfU = h.Y; halfV = h.Z; break;
            case 2: ln = new Vector3(0, 1, 0); lu = new Vector3(1, 0, 0); lv = new Vector3(0, 0, 1); halfU = h.X; halfV = h.Z; break;
            case 3: ln = new Vector3(0, -1, 0); lu = new Vector3(1, 0, 0); lv = new Vector3(0, 0, 1); halfU = h.X; halfV = h.Z; break;
            case 4: ln = new Vector3(0, 0, 1); lu = new Vector3(1, 0, 0); lv = new Vector3(0, 1, 0); halfU = h.X; halfV = h.Y; break;
            default: ln = new Vector3(0, 0, -1); lu = new Vector3(1, 0, 0); lv = new Vector3(0, 1, 0); halfU = h.X; halfV = h.Y; break;
        }
        float offset = face switch { 0 => h.X, 1 => h.X, 2 => h.Y, 3 => h.Y, _ => h.Z };
        if (halfU <= 0f || halfV <= 0f || offset <= 0f)
        { centre = default; normal = default; uAxis = default; vAxis = default; return false; }

        normal = Vector3.Transform(ln, s.Rotation);
        uAxis = Vector3.Transform(lu, s.Rotation);
        vAxis = Vector3.Transform(lv, s.Rotation);
        centre = s.Center + normal * offset;
        return true;
    }
}
