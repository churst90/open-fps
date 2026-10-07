using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// How much of the sound leaving a place comes back to it: whether there is a reverberant field here
/// at all, measured from the geometry, never read off a decay time. Steam Audio's estimator fits a
/// curve to whatever energy its rays bring home: a walled yard with no ceiling fitted 1.0 s where the
/// same walls roofed fitted 0.6 s (AudioLab --sim-reverbfield), and the speedway's front straight got a
/// 1.1 s tail at -22 dB in open air. See docs/COMMON_NOTES.md, Reverberation.
/// </summary>
public static class Enclosure
{
    /// <summary>A box of scene geometry, in the shape the acoustic scene is built from.</summary>
    public readonly record struct Solid(Vector3 Center, Vector3 Size, Quaternion Rotation, string Material);

    /// <summary>Directions sampled, on a Fibonacci sphere: no random numbers, or a listener standing still
    /// would hear the room breathe.</summary>
    public const int Rays = 192;

    /// <summary>
    /// How far a surface can be and still be part of the room, metres: 60 m is about 350 ms for the
    /// round trip, the outer edge of what fuses into a tail rather than arriving as a separate slap
    /// (which the discrete reflections are for).
    /// </summary>
    public const float ReverberantRangeMetres = 60f;

    /// <summary>
    /// The boxes in reach, gathered once instead of rejected once per ray: 384 casts over a 4,165-box
    /// city cost 32 ms against 7.5 on the 390-box block, for the same room. In reach is the casts' own
    /// bounding-sphere test at twice the range (two bounces), so the same boxes are hit in the same
    /// order; generous, because a box wrongly dropped is a wall that stops existing.
    /// </summary>
    private static List<Solid> Nearby(Vector3 listener, IReadOnlyList<Solid> solids)
    {
        var near = _nearby ??= new List<Solid>(256);
        near.Clear();
        const float reach = 2f * ReverberantRangeMetres;
        for (int i = 0; i < solids.Count; i++)
        {
            var s = solids[i];
            float r = reach + s.Size.Length() * 0.5f;
            if (Vector3.DistanceSquared(listener, s.Center) <= r * r) near.Add(s);
        }
        return near;
    }

    /// <summary>Per thread: the survey runs on the acoustic worker and on any thread a spike calls it from.</summary>
    [ThreadStatic] private static List<Solid>? _nearby;

    /// <summary>
    /// The fraction of emitted energy still here after it has left and come back: 0 for an open field,
    /// 1 inside a mirror-walled box. Two bounces, because one cannot tell a plane from a room: bare open
    /// concrete scored 48 % on one bounce (the battle spike's street), but off flat ground the second
    /// ray goes up and is gone, while in a street it crosses to the opposite facade.
    /// </summary>
    public static float Measure(Vector3 listener, IReadOnlyList<Solid> solids)
    {
        if (solids == null || solids.Count == 0) return 0f;
        solids = Nearby(listener, solids);
        if (solids.Count == 0) return 0f;

        float returned = 0f;
        for (int k = 0; k < Rays; k++)
        {
            Vector3 dir = SphereDirection(k, Rays);
            if (!Cast(listener, dir, solids, out float d1, out Vector3 n1, out float keep1)) continue;

            // On by the mirror direction: what makes the ground, facing away from everything, differ
            // from a wall facing another. Lifted off so the same box is not struck at zero distance.
            Vector3 hit = listener + dir * d1;
            Vector3 onward = Vector3.Reflect(dir, n1);
            if (!Cast(hit + onward * 0.01f, onward, solids, out _, out _, out float keep2)) continue;

            returned += keep1 * keep2;
        }
        return Math.Clamp(returned / Rays, 0f, 1f);
    }

    /// <summary>
    /// What a survey casts its rays against: the nearest surface a ray ENTERS (from outside it) within
    /// <see cref="ReverberantRangeMetres"/>, which way it faces and what it is made of. The box list the
    /// acoustic scene was built from, or the triangle world (docs/GEOMETRY.md stage 1).
    /// </summary>
    public interface ISurroundings
    {
        bool Nearest(Vector3 origin, Vector3 direction, out float distance, out Vector3 normal, out string material);
    }

    /// <summary>The boxes, each tested along the ray (after cheap rejects on its bounding sphere).</summary>
    private readonly struct BoxSurroundings : ISurroundings
    {
        private readonly IReadOnlyList<Solid> _solids;
        public BoxSurroundings(IReadOnlyList<Solid> solids) => _solids = solids;

        public bool Nearest(Vector3 origin, Vector3 direction, out float distance, out Vector3 normal, out string material)
        {
            distance = float.MaxValue; normal = Vector3.Zero; material = "";
            var solids = _solids;
            for (int i = 0; i < solids.Count; i++)
            {
                var s = solids[i];
                // Cheap rejects on the bounding sphere. Off the ray's line is the one that matters in a
                // city: every floor and sofa of a tower 60 m away is in range of every ray, and range
                // alone let the survey on Main Street drift to 55 ms.
                float radiusSq = s.Size.LengthSquared() * 0.25f;
                Vector3 toCentre = s.Center - origin;
                float along = Vector3.Dot(toCentre, direction);
                if (along < 0f && along * along > radiusSq) continue;                  // wholly behind
                float centreSq = toCentre.LengthSquared();
                if (centreSq - along * along > radiusSq) continue;                     // off the line
                float reach = ReverberantRangeMetres + MathF.Sqrt(radiusSq);
                if (centreSq > reach * reach) continue;                                // out of range
                if (!GeometryUtils.RayHitsOBB(origin, direction, ReverberantRangeMetres,
                                              s.Center, s.Size, s.Rotation, out float t, out Vector3 n)) continue;
                if (t >= distance) continue;
                distance = t; normal = n; material = s.Material;
            }
            return distance != float.MaxValue;
        }
    }

    /// <summary>The triangle world's acoustic layer: the nearest face a ray enters by within range. A ray
    /// starting inside a solid passes out of it unseen, as with boxes.</summary>
    public readonly struct TriangleSurroundings : ISurroundings
    {
        private readonly Geometry.TriangleWorld _world;
        private readonly Geometry.GeometryLayers _layers;
        public TriangleSurroundings(Geometry.TriangleWorld world, Geometry.GeometryLayers layers = Geometry.GeometryLayers.Acoustics)
        { _world = world; _layers = layers; }

        public bool Nearest(Vector3 origin, Vector3 direction, out float distance, out Vector3 normal, out string material)
        {
            var all = new Geometry.AcceptAll();
            if (!_world.Closest(origin, direction, ReverberantRangeMetres, _layers, Geometry.RayFaces.Front, ref all, out var hit, 1e-6f))
            {
                distance = float.MaxValue; normal = Vector3.Zero; material = "";
                return false;
            }
            distance = hit.T; normal = hit.Normal; material = _world.SurfaceOf(hit).Material;
            return true;
        }
    }

    /// <summary>Nearest surface along a ray within <see cref="ReverberantRangeMetres"/>: how far, which
    /// way it faces, and the fraction of energy it does not absorb.</summary>
    private static bool Cast(Vector3 origin, Vector3 direction, IReadOnlyList<Solid> solids,
                             out float distance, out Vector3 normal, out float keep)
    {
        var boxes = new BoxSurroundings(solids);
        return Cast(origin, direction, ref boxes, out distance, out normal, out keep, out _);
    }

    private static bool Cast<S>(Vector3 origin, Vector3 direction, ref S surroundings,
                                out float distance, out Vector3 normal, out float keep,
                                out MaterialProperties props) where S : ISurroundings
    {
        keep = 0f;
        if (!surroundings.Nearest(origin, direction, out distance, out normal, out string material))
        {
            distance = float.MaxValue; normal = Vector3.Zero;
            props = AcousticRegistry.GetProperties("Generic");
            return false;
        }
        props = AcousticRegistry.GetProperties(material);
        float absorption = Math.Clamp((props.AbsorptionLow + props.AbsorptionMid + props.AbsorptionHigh) / 3f, 0f, 1f);
        keep = 1f - absorption;
        return true;
    }

    /// <summary>The surroundings as one measurement: how enclosed, how far sound travels between
    /// surfaces, and what they take out of it per band.</summary>
    /// <param name="Enclosure">Fraction of emitted energy still here after leaving and coming back.</param>
    /// <param name="OpenFraction">Fraction of directions with nothing in them: an opening is a perfect
    /// absorber.</param>
    /// <param name="MeanFreePathMetres">Mean distance to the first surface over the directions that found
    /// one (4V/S, measured).</param>
    public readonly record struct Survey(float Enclosure, float OpenFraction, float MeanFreePathMetres,
                                         float AbsorptionLow, float AbsorptionMid, float AbsorptionHigh,
                                         Vector3 ReturnDirection, float Anisotropy,
                                         float SurfaceAreaSquareMetres)
    {
        public Survey(float enclosure, float openFraction, float meanFreePathMetres,
                      float absorptionLow, float absorptionMid, float absorptionHigh)
            : this(enclosure, openFraction, meanFreePathMetres, absorptionLow, absorptionMid, absorptionHigh,
                   Vector3.Zero, 0f, 13.5f * meanFreePathMetres * meanFreePathMetres) { }
    }

    /// <summary>
    /// Which way the returned energy came from, unit length in world space (zero when nothing came
    /// back), and in <paramref name="anisotropy"/> how much from one way: near 0 in a sealed uniform
    /// room, near 1 for one hard wall in a field. Cody, facing the hard half of a half-carpeted hall: "I
    /// should hear a wash of reverb coming ONLY from that half of the room".
    /// </summary>
    public static Vector3 ReturnCentroid(Vector3 weightedSum, float returned, out float anisotropy)
    {
        float len = weightedSum.Length();
        anisotropy = returned > 1e-6f ? Math.Clamp(len / returned, 0f, 1f) : 0f;
        return len > 1e-6f ? weightedSum / len : Vector3.Zero;
    }

    /// <summary>
    /// The same sphere of rays as <see cref="Measure"/>, reporting everything it saw: the mean free path
    /// and absorption a decay time is made of, counting the directions that meet nothing as perfect
    /// absorbers, which is why a room with no ceiling is a courtyard and not a reverberation chamber.
    /// </summary>
    public static Survey Look(Vector3 listener, IReadOnlyList<Solid> solids)
    {
        if (solids == null || solids.Count == 0)
            return new Survey(0f, 1f, ReverberantRangeMetres, 1f, 1f, 1f);
        var scene = solids;
        solids = Nearby(listener, solids);
        if (solids.Count == 0)
            return new Survey(0f, 1f, ReverberantRangeMetres, 1f, 1f, 1f);
        var boxes = new BoxSurroundings(solids);
        return Look(listener, ref boxes, scene);
    }

    /// <summary>The same survey against the triangle world's BVH (docs/GEOMETRY.md 3.4): 14 to 21 times
    /// faster.</summary>
    public static Survey Look(Vector3 listener, Geometry.TriangleWorld world)
    {
        if (world == null || world.InstanceCount == 0)
            return new Survey(0f, 1f, ReverberantRangeMetres, 1f, 1f, 1f);
        var surroundings = new TriangleSurroundings(world);
        return Look(listener, ref surroundings, world);
    }

    /// <summary>The survey against any surroundings; <paramref name="scene"/> keys the openness cache.</summary>
    public static Survey Look<S>(Vector3 listener, ref S surroundings, object scene) where S : ISurroundings
    {
        // Where the room ends: a ray whose path is much more open halfway along than where the listener
        // stands has left this place and counts as escaped. Without it a bus shelter's rays crossed the
        // road to the building opposite and read 612 m² of surface for a 65 m² box, a two-second tail.
        // Distance-keyed rules also cut a big room's own far wall (docs/COMMON_NOTES.md, Where a room
        // ends). Known limit: a small room onto a big enclosed hall is surveyed as the hall.
        var casts = _casts ??= new RayHit[Rays];
        int misses = 0;
        for (int k = 0; k < Rays; k++)
        {
            Vector3 dir = SphereDirection(k, Rays);
            bool hit = Cast(listener, dir, ref surroundings, out float d, out Vector3 n, out float keep, out var pr);
            casts[k] = new RayHit(hit, d, n, keep, pr);
            if (!hit) misses++;
        }
        float ownOpenness = misses / (float)Rays;
        for (int k = 0; k < Rays && ownOpenness < AlreadyOutside; k++)
        {
            if (!casts[k].Hit || casts[k].Distance < BoundaryMinMetres) continue;
            Vector3 mid = listener + SphereDirection(k, Rays) * (casts[k].Distance * 0.5f);
            if (Openness(mid, scene, ref surroundings) - ownOpenness > BoundaryJump) casts[k] = casts[k] with { Hit = false };
        }

        float returned = 0f;
        Vector3 returnedFrom = Vector3.Zero;
        int hits = 0;
        float pathSum = 0f;
        float aLow = 0f, aMid = 0f, aHigh = 0f;
        // Volume and surface area from one point inside, exact for a convex room: each ray's cone has
        // volume d³dω/3, and a patch of wall subtends dS·cosθ/d², so ∮(d²/cosθ)dω is the whole of S.
        double volSum = 0, areaSum = 0;

        for (int k = 0; k < Rays; k++)
        {
            Vector3 dir = SphereDirection(k, Rays);
            var c = casts[k];
            float d1 = c.Distance, keep1 = c.Keep;
            Vector3 n1 = c.Normal;
            var props = c.Props;
            if (!c.Hit)
            {
                // An opening absorbs all that reaches it and has no distance between surfaces: counting
                // the horizon put a ten-metre room's mean free path at 21 m and a courtyard's decay at two
                // seconds where it has a third of one.
                aLow += 1f; aMid += 1f; aHigh += 1f;
                continue;
            }

            hits++;
            pathSum += d1;
            volSum += (double)d1 * d1 * d1;
            // Grazing rays make d²/cosθ diverge; floored at a twentieth, within a few per cent of 4V/S
            // for every room shape tried (EnclosureTests).
            areaSum += (double)d1 * d1 / MathF.Max(MathF.Abs(Vector3.Dot(dir, n1)), 0.05f);
            aLow += Math.Clamp(props.AbsorptionLow, 0f, 1f);
            aMid += Math.Clamp(props.AbsorptionMid, 0f, 1f);
            aHigh += Math.Clamp(props.AbsorptionHigh, 0f, 1f);

            Vector3 hit = listener + dir * d1;
            Vector3 onward = Vector3.Reflect(dir, n1);
            if (!Cast(hit + onward * 0.01f, onward, ref surroundings, out _, out _, out float keep2, out _)) continue;
            float energy = keep1 * keep2;
            returned += energy;
            returnedFrom += dir * energy;
        }

        Vector3 centroid = ReturnCentroid(returnedFrom, returned, out float anisotropy);
        // S = 4π·mean(d²/cosθ) over the hits, so a room with an opening reports the surface it has.
        float surface = hits > 0 ? (float)(4.0 * Math.PI * areaSum / hits) : 0f;
        return new Survey(
            Math.Clamp(returned / Rays, 0f, 1f),
            1f - (float)hits / Rays,
            hits > 0 ? pathSum / hits : ReverberantRangeMetres,
            aLow / Rays, aMid / Rays, aHigh / Rays,
            centroid, anisotropy, surface);
    }

    private readonly record struct RayHit(bool Hit, float Distance, Vector3 Normal, float Keep, MaterialProperties Props);
    [ThreadStatic] private static RayHit[]? _casts;

    /// <summary>A ray has to go this far before the question "did it leave the room?" is asked: a
    /// surface within three metres of you is your own.</summary>
    private const float BoundaryMinMetres = 3f;

    /// <summary>How much more open the middle of a ray's path must be than where you stand for the ray to
    /// have left: it separates a closed box (0-2 % open), a street (about a third) and a field (a half).</summary>
    private const float BoundaryJump = 0.15f;

    /// <summary>Rays per openness probe: coarse, asked at up to a couple of hundred points a survey.</summary>
    private const int OpennessRays = 14;

    /// <summary>A listener this open is not inside anything small, and the boundary is not looked for.</summary>
    private const float AlreadyOutside = 0.35f;

    /// <summary>
    /// Openness probes are cached in cells half a metre high: the map does not move. The probe is cast
    /// from the point that asked, never the cell's centre: a cell centred three metres up probed a
    /// 2.5 m garage from above its roof, and eleven per cent of a sealed garage read as having left it.
    /// </summary>
    private const float OpennessCell = 0.5f;

    /// <summary>...and two metres across: openness changes fast with height and slowly across the
    /// ground, and half-metre cells every way missed the cache almost every probe (a survey five times
    /// as costly).</summary>
    private const float OpennessCellAcross = 2f;

    [ThreadStatic] private static Dictionary<(int, int, int), float>? _openness;
    [ThreadStatic] private static object? _opennessScene;

    /// <summary>The fraction of directions from a point that meet nothing.</summary>
    private static float Openness<S>(Vector3 at, object scene, ref S surroundings) where S : ISurroundings
    {
        if (!ReferenceEquals(_opennessScene, scene) || _openness == null)
        {
            _openness = new Dictionary<(int, int, int), float>();
            _opennessScene = scene;
        }
        var key = ((int)MathF.Floor(at.X / OpennessCellAcross), (int)MathF.Floor(at.Y / OpennessCell), (int)MathF.Floor(at.Z / OpennessCellAcross));
        if (_openness.TryGetValue(key, out float cached)) return cached;
        int open = 0;
        for (int k = 0; k < OpennessRays; k++)
            if (!Cast(at, SphereDirection(k, OpennessRays), ref surroundings, out _, out _, out _, out _)) open++;
        float value = open / (float)OpennessRays;
        _openness[key] = value;
        return value;
    }

    /// <summary>
    /// How long the tail lasts per band, seconds, by Eyring with the measured mean free path for V/S:
    /// <c>RT60 = 0.161 · (MFP/4) / -ln(1 - a)</c>. Eyring, not Sabine, because Sabine goes badly wrong
    /// once a face of the room is missing, the normal case here; the sky's absorption is what turns a
    /// concrete box into a courtyard's third of a second.
    /// </summary>
    public static (float Low, float Mid, float High) DecaySeconds(in Survey survey)
    {
        float mfp = MathF.Max(0.5f, survey.MeanFreePathMetres);
        return (Band(survey.AbsorptionLow), Band(survey.AbsorptionMid), Band(survey.AbsorptionHigh));

        float Band(float a)
        {
            float eyring = -MathF.Log(1f - Math.Clamp(a, 0.001f, 0.999f));
            return 0.161f * mfp / (4f * eyring);
        }
    }

    /// <summary>
    /// The reverberant field next to the direct sound of a source <paramref name="distanceMetres"/> away,
    /// as a power ratio: the room equation, (r / r_c)² with r_c = sqrt(R / 16π) and R = S·ā/(1−ā), with
    /// e/(1−e) standing for (1−ā)/ā and S ≈ 13.5·MFP² for a cube:
    /// <c>16π r² e / (S (1−e)) ≈ 3.7 · (r / MFP)² · e / (1−e)</c>. A constant send in its place (0.35 for
    /// every source) put a footstep's reverberation 12 dB over the step, onto the limiter: "footsteps
    /// loud, pop pop pop, piling up".
    /// </summary>
    public static float ReverberantToDirectPower(float enclosure, float meanFreePathMetres, float distanceMetres)
        => ReverberantToDirectPower(enclosure, meanFreePathMetres,
                                    CubeSurfaceOverMfpSquared * meanFreePathMetres * meanFreePathMetres,
                                    distanceMetres);

    /// <summary>
    /// The same ratio with the surface area measured (Look) rather than a cube's. The cube is hopeless
    /// for anything flat or long: a car park 21 by 28 m and 2.5 m high (MFP 4.3 m) would have 246 m²
    /// and has 1,390, so its field read 9 dB too loud and every footstep sat on the limiter
    /// (`--enclosure map=city at=-20,1.6,30` read a 298 % send where the room equation says 170 %).
    /// </summary>
    public static float ReverberantToDirectPower(float enclosure, float meanFreePathMetres,
                                                 float surfaceAreaSquareMetres, float distanceMetres)
    {
        float e = Math.Clamp(enclosure, 0f, 0.999f);
        if (e <= 1e-6f) return 0f;
        float mfp = MathF.Max(0.5f, meanFreePathMetres);
        float s = surfaceAreaSquareMetres > 1f
            ? surfaceAreaSquareMetres
            : CubeSurfaceOverMfpSquared * mfp * mfp;    // nothing measured: a cube
        // A source further off than the room is wide is not in it as the diffuse field assumes.
        float r = MathF.Min(MathF.Max(0.1f, distanceMetres), 3f * mfp);
        return 16f * MathF.PI * r * r * e / (s * (1f - e));
    }

    /// <summary>A cube's surface per square metre of its mean free path: S = 6L², MFP = 2L/3, so
    /// S = 13.5·MFP². The fallback when nothing measured the surface; five times wrong on anything flat
    /// or long.</summary>
    private const float CubeSurfaceOverMfpSquared = 13.5f;

    /// <summary>
    /// The steady-state level of the reverberant field, dB: every generation of returns, a geometric
    /// series <c>e + e² + ... = e / (1 - e)</c>, which is why one bounce understates a room (worth 58 at
    /// 98 % enclosure, under one at 47 %). AudioLab --sim-reverbfield prints the ladder from an open
    /// field to a sealed box.
    /// </summary>
    public static float ReverberantGainDb(float enclosure)
    {
        float e = Math.Clamp(enclosure, 0f, 0.999f);
        if (e <= 1e-6f) return -80f;
        return 10f * MathF.Log10(e / (1f - e));
    }

    /// <summary>Evenly spread directions on the sphere by the golden angle, the same for the same index.</summary>
    public static Vector3 SphereDirection(int index, int count)
    {
        // Equal bands of height are equal areas on a sphere.
        float y = 1f - 2f * (index + 0.5f) / count;
        float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        float theta = index * 2.399963f;   // golden angle, radians
        return new Vector3(r * MathF.Cos(theta), y, r * MathF.Sin(theta));
    }
}
