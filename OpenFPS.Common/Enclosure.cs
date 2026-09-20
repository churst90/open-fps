using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// How much of the sound leaving a place comes back to it — the one number that says whether there is
/// a reverberant field here at all.
///
/// A decay TIME cannot answer that question, and finding out why cost a session. Steam Audio's
/// parametric estimator fits an exponential to the energy its rays bring home, and the fit knows
/// nothing about how much energy that was: in the open, a handful of rays returning off two hard walls
/// decay slowly, because concrete is nearly lossless, and a slow decay of almost nothing is reported as
/// a long reverb. Measured (AudioLab --sim-reverbfield): a walled yard with NO CEILING fitted 1.0 s
/// where the SAME walls with a roof on fitted 0.6 s. The roofless one read as the more reverberant of
/// the two. On the speedway's front straight that put a 1.1 s tail at -22 dB over a race in open air.
///
/// So the level of a reverberant field is not a function of its decay time, and it never was. It is a
/// function of how enclosed you are, which is a property of the geometry standing around you and can
/// simply be measured: fire rays in every direction, see what each one meets, and add up the energy
/// that would come back. Nothing here knows what a room, a street or a racetrack is — a field scores
/// low because the sky takes half of it and the grass takes most of the rest, a concrete box scores
/// high because concrete returns 98% of what hits it, and a stand of trees lands in between because
/// foliage is absorbent. The same arithmetic on any map.
/// </summary>
public static class Enclosure
{
    /// <summary>
    /// A box of scene geometry: where it is, how big, how it is turned, and what it is made of.
    /// Deliberately the same shape the acoustic scene is built from, so the caller passes what it has.
    /// </summary>
    public readonly record struct Solid(Vector3 Center, Vector3 Size, Quaternion Rotation, string Material);

    /// <summary>
    /// Directions sampled. A Fibonacci sphere, so they are near-uniform without a random number in
    /// sight — the measure has to be the same every tick for the same geometry, or a listener standing
    /// still would hear the room breathe.
    /// </summary>
    public const int Rays = 192;

    /// <summary>
    /// How far a surface can be and still be part of the room rather than an echo off something in the
    /// distance, metres.
    ///
    /// The one constant here, and it is a time in disguise: 60 m is about 350 ms for the round trip,
    /// which is the outer edge of what fuses into a reverberant tail instead of arriving as a separate
    /// slap. Beyond it a wall still reflects — that is what the discrete reflection path is for — but
    /// it is not what makes a space sound enclosed.
    /// </summary>
    public const float ReverberantRangeMetres = 60f;

    /// <summary>
    /// The fraction of emitted energy that is still bouncing around here after it has left and come
    /// back — 0 for an open field, 1 for the inside of a mirror-walled box.
    ///
    /// TWO bounces, and the second one is the whole point. One bounce cannot tell a plane from a room:
    /// standing on bare concrete, half of every direction ends in concrete, which returns 98% of what
    /// hits it, and a single-bounce measure calls that half-enclosed. It is not — the ground reflects
    /// sound AWAY, once, and that energy never comes back. Measured on the battle spike's concrete
    /// street, bare open ground scored 48% on one bounce, which would have put a plaza most of the way
    /// to a room. Following each ray past its first surface is what separates them: off flat ground it
    /// goes up and is gone, while in a street it crosses to the facade opposite and stays.
    ///
    /// So a ray is worth what survives BOTH surfaces, and only if there is a second one. Nothing here
    /// knows what a room, a street or a racetrack is: a field scores low because the sky takes the sound
    /// and the ground sends the rest of it there, a concrete box scores high because every direction
    /// leads to another wall, and a stand of trees lands between them because foliage absorbs.
    /// </summary>
    /// <summary>
    /// The boxes near enough to matter, gathered once instead of rejected once per ray.
    ///
    /// Every cast below walks the whole solid list and rejects what is out of range by a distance
    /// test. That is the right test and it is cheap, but it is done 192 times for the outward rays
    /// and 192 more for the bounces, so on a map with four thousand boxes on it the survey does one
    /// and a half million distance tests to look at the two hundred boxes that are actually within
    /// reach. Measured: 7.5 ms on the 390-box block, 32 ms on the 4,165-box city — for the same
    /// question asked of the same room.
    ///
    /// So it is asked once. A solid is in reach if its bounding sphere is, which is exactly the test
    /// the casts were doing; the two bounces can travel further than one, so the radius is doubled.
    /// Nothing about the measurement changes — the same boxes are hit in the same order — and the
    /// sphere it is worth keeping is generous rather than tight, because a box wrongly dropped is a
    /// wall that stops existing.
    /// </summary>
    private static List<Solid> Nearby(Vector3 listener, IReadOnlyList<Solid> solids)
    {
        var near = _nearby ??= new List<Solid>(256);
        near.Clear();
        // Two bounces: out to a surface and on to another. A ray can therefore be twice the range
        // from the listener when it strikes, and the second surface is still this room's.
        const float reach = 2f * ReverberantRangeMetres;
        for (int i = 0; i < solids.Count; i++)
        {
            var s = solids[i];
            float r = reach + s.Size.Length() * 0.5f;
            if (Vector3.DistanceSquared(listener, s.Center) <= r * r) near.Add(s);
        }
        return near;
    }

    /// <summary>One scratch list per thread. The survey runs on the acoustic worker and on whatever
    /// thread a spike calls it from, and two of them sharing a list would interleave.</summary>
    [ThreadStatic] private static List<Solid>? _nearby;

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

            // Where it goes next. A mirror bounce off the face it struck — the specular direction is
            // the one a ray actually takes, and it is what makes a surface that faces away from
            // everything (the ground) behave differently from one that faces another surface.
            Vector3 hit = listener + dir * d1;
            Vector3 onward = Vector3.Reflect(dir, n1);
            // Lift off the surface so the box it just hit is not struck again at zero distance.
            if (!Cast(hit + onward * 0.01f, onward, solids, out _, out _, out float keep2)) continue;

            returned += keep1 * keep2;
        }
        return Math.Clamp(returned / Rays, 0f, 1f);
    }

    /// <summary>Nearest surface along a ray within <see cref="ReverberantRangeMetres"/>: how far, which
    /// way it faces, and the fraction of energy it does not absorb.</summary>
    private static bool Cast(Vector3 origin, Vector3 direction, IReadOnlyList<Solid> solids,
                             out float distance, out Vector3 normal, out float keep)
        => Cast(origin, direction, solids, out distance, out normal, out keep, out _);

    private static bool Cast(Vector3 origin, Vector3 direction, IReadOnlyList<Solid> solids,
                             out float distance, out Vector3 normal, out float keep,
                             out MaterialProperties props)
    {
        distance = float.MaxValue; normal = Vector3.Zero; keep = 0f;
        props = AcousticRegistry.GetProperties("Generic");
        string material = "";

        for (int i = 0; i < solids.Count; i++)
        {
            var s = solids[i];
            // Cheap reject first: a box whose bounding sphere is out of range cannot be struck within
            // it, and on a map with a hundred walls most boxes fail here for every ray.
            float reach = ReverberantRangeMetres + s.Size.Length() * 0.5f;
            if (Vector3.DistanceSquared(origin, s.Center) > reach * reach) continue;
            if (!GeometryUtils.RayHitsOBB(origin, direction, ReverberantRangeMetres,
                                          s.Center, s.Size, s.Rotation, out float t, out Vector3 n)) continue;
            if (t >= distance) continue;
            distance = t; normal = n; material = s.Material;
        }
        if (distance == float.MaxValue) return false;   // this direction is open: the energy is gone

        props = AcousticRegistry.GetProperties(material);
        float absorption = Math.Clamp((props.AbsorptionLow + props.AbsorptionMid + props.AbsorptionHigh) / 3f, 0f, 1f);
        keep = 1f - absorption;
        return true;
    }

    /// <summary>
    /// What the surroundings are, as one measurement: how enclosed, how far sound travels between
    /// surfaces, and what those surfaces take out of it per band.
    /// </summary>
    /// <param name="Enclosure">Fraction of emitted energy still here after leaving and coming back.</param>
    /// <param name="OpenFraction">Fraction of directions with nothing in them — the sky, and the open
    /// sides of the world. An opening is a perfect absorber: what goes out of it does not come back.</param>
    /// <param name="MeanFreePathMetres">Mean distance to the first surface, over the directions that
    /// FOUND one. Four times the volume over the surface area, measured rather than assumed — and the
    /// directions that met nothing are excluded, because a direction with no surface in it has no
    /// distance between surfaces to contribute.</param>
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
    /// Which way the returned energy came from, and how much it came from one way.
    ///
    /// The reverberant field is not the same in every direction, and a listener can tell. Stand on
    /// the carpeted half of a hall facing the concrete half: the carpet behind you sends almost
    /// nothing back, the concrete in front sends nearly everything, and the wash is heard IN FRONT.
    /// Reported as exactly that: "if I turn to face the wood/concrete half, I should hear a wash of
    /// reverb coming ONLY from that half of the room, as if the reflections are in front of me."
    ///
    /// <see cref="Survey.ReturnDirection"/> is the energy-weighted mean of the directions the rays
    /// went out in and came back from, in world space, unit length — or zero when nothing came back.
    /// <see cref="Survey.Anisotropy"/> is how concentrated it is: the length of that mean before
    /// normalising, over the total returned. A sealed uniform room scores near 0 (energy from
    /// everywhere cancels); one hard wall in an open field scores near 1. Nothing here knows what a
    /// carpet or a gym is; the materials and the boxes decide, as they decide the level.
    /// </summary>
    public static Vector3 ReturnCentroid(Vector3 weightedSum, float returned, out float anisotropy)
    {
        float len = weightedSum.Length();
        anisotropy = returned > 1e-6f ? Math.Clamp(len / returned, 0f, 1f) : 0f;
        return len > 1e-6f ? weightedSum / len : Vector3.Zero;
    }

    /// <summary>
    /// The same sphere of rays as <see cref="Measure"/>, reporting everything it saw rather than only
    /// the number that comes out of it.
    ///
    /// This is what makes a room's character a consequence of its materials instead of a setting. The
    /// mean free path and the mean absorption are the two things a decay time is made of, and both are
    /// here for the asking once the rays have been cast — including, and this is the part a room model
    /// built from a box list normally misses, the directions that meet NOTHING. An opening absorbs
    /// everything that reaches it, so a room with no ceiling has most of its absorption in the sky, and
    /// that is why it is a courtyard rather than a reverberation chamber.
    /// </summary>
    public static Survey Look(Vector3 listener, IReadOnlyList<Solid> solids)
    {
        if (solids == null || solids.Count == 0)
            return new Survey(0f, 1f, ReverberantRangeMetres, 1f, 1f, 1f);
        solids = Nearby(listener, solids);
        if (solids.Count == 0)
            return new Survey(0f, 1f, ReverberantRangeMetres, 1f, 1f, 1f);

        float returned = 0f;
        Vector3 returnedFrom = Vector3.Zero;
        int hits = 0;
        float pathSum = 0f;
        float aLow = 0f, aMid = 0f, aHigh = 0f;
        // The two integrals that give the room's VOLUME and its SURFACE AREA from one point inside
        // it. For a convex room both are exact: the cone swept by each ray has volume d³dω/3, and the
        // solid angle a patch of wall subtends is dS·cosθ/d², so ∮(d²/cosθ)dω is the whole of S.
        double volSum = 0, areaSum = 0;

        for (int k = 0; k < Rays; k++)
        {
            Vector3 dir = SphereDirection(k, Rays);
            if (!Cast(listener, dir, solids, out float d1, out Vector3 n1, out float keep1, out var props))
            {
                // Open in this direction: an opening absorbs everything that reaches it, and that is the
                // whole of its contribution. It does NOT count toward the mean free path — the mean free
                // path is the distance between SURFACES, and a direction with no surface in it has no
                // such distance. Letting escapes contribute the horizon put the mean free path of a
                // ten-metre room at twenty-one metres, because a third of the sphere was answering
                // "sixty", and a decay time built on that reads two seconds where a courtyard has a
                // third of one.
                aLow += 1f; aMid += 1f; aHigh += 1f;
                continue;
            }

            hits++;
            pathSum += d1;
            volSum += (double)d1 * d1 * d1;
            // Grazing rays make d²/cosθ diverge, and a sphere of 192 directions cannot integrate a
            // divergence. Floored at a twentieth, which numerically lands within a few per cent of
            // 4V/S for every room shape tried (see EnclosureTests).
            areaSum += (double)d1 * d1 / MathF.Max(MathF.Abs(Vector3.Dot(dir, n1)), 0.05f);
            aLow += Math.Clamp(props.AbsorptionLow, 0f, 1f);
            aMid += Math.Clamp(props.AbsorptionMid, 0f, 1f);
            aHigh += Math.Clamp(props.AbsorptionHigh, 0f, 1f);

            Vector3 hit = listener + dir * d1;
            Vector3 onward = Vector3.Reflect(dir, n1);
            if (!Cast(hit + onward * 0.01f, onward, solids, out _, out _, out float keep2, out _)) continue;
            float energy = keep1 * keep2;
            returned += energy;
            returnedFrom += dir * energy;
        }

        Vector3 centroid = ReturnCentroid(returnedFrom, returned, out float anisotropy);
        // S = 4π·mean(d²/cosθ) over the directions that found a surface. The mean is over HITS rather
        // than over the sphere, so a room with an opening in it reports the area of the surface it
        // does have rather than being scaled down by the hole.
        float surface = hits > 0 ? (float)(4.0 * Math.PI * areaSum / hits) : 0f;
        return new Survey(
            Math.Clamp(returned / Rays, 0f, 1f),
            1f - (float)hits / Rays,
            hits > 0 ? pathSum / hits : ReverberantRangeMetres,
            aLow / Rays, aMid / Rays, aHigh / Rays,
            centroid, anisotropy, surface);
    }

    /// <summary>
    /// How long the tail lasts, per band, seconds — Eyring from what the rays measured.
    ///
    /// <c>RT60 = 0.161 · (MFP/4) / -ln(1 - a)</c>, which is the classical formula with the volume over
    /// surface area replaced by the mean free path that was actually measured. Eyring rather than
    /// Sabine because these rooms are absorbent: Sabine assumes a small amount of absorption spread
    /// evenly and goes badly wrong once a face of the room is missing, which is the normal case here.
    ///
    /// The openings do the work. A sealed box of bare concrete really does ring for seconds — that is
    /// a reverberation chamber and the number is not wrong — but take its ceiling off and most of the
    /// absorption becomes sky, and it turns into the third of a second a courtyard actually has. So
    /// the difference between a room and a yard falls out of the same arithmetic, unprompted.
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
    /// The steady-state level of the reverberant field, dB, for a given enclosure.
    ///
    /// Sound does not come back once. What returns off the walls leaves again, and some of THAT comes
    /// back too, and the reverberant field is the sum of every generation of it — a geometric series,
    /// <c>e + e^2 + e^3 + ... = e / (1 - e)</c>. That sum is the whole difference between a wall and a
    /// room, and it is why a one-bounce measure understates a room so badly: at 98% enclosure the
    /// series is worth fifty-eight, at 47% it is worth less than one.
    ///
    /// This is the classical room equation in the form the geometry hands us, and it needs no tuning.
    /// The ladder it produces is printed by AudioLab --sim-reverbfield, which is the instrument to run
    /// if any of this is ever in doubt: it walks the same listener from an open field to a sealed box
    /// and prints what each place measures, beside what the decay time alone would have said.
    /// </summary>
    /// <summary>
    /// How loud the diffuse field is next to the direct sound of a source <paramref name="distanceMetres"/>
    /// away — as a POWER ratio, reverberant over direct. This is the room equation with the room's
    /// two unknowns replaced by what the rays measured.
    ///
    /// Classically the ratio at distance r is (r / r_c)², where the critical distance r_c is
    /// sqrt(R / 16π) and the room constant R is S·ā/(1−ā): S the surface area, ā the mean absorption.
    /// The survey knows ā through what came back — the enclosure e is what a round of returns keeps,
    /// so e/(1−e) stands for (1−ā)/ā — and knows the room's SIZE through the mean free path, which
    /// is 4V/S; for the box a room approximately is, S comes to about 13.5·MFP². Put together:
    ///
    ///   reverberant / direct  =  16π r² e / (S (1−e))  ≈  3.7 · (r / MFP)² · e / (1−e)
    ///
    /// Every term is measured and every consequence is the right one. A source at your own feet in
    /// a roofless hall reads a few decibels under its direct sound; the same step in a sealed concrete
    /// cell reads well over it, which is what a cell does; open ground, where e is nought, reads
    /// nothing however close the source. And the field is INDEPENDENT of how far the source is: what
    /// grows with distance is the ratio, because the direct sound is what falls.
    ///
    /// Before this the send was one constant for every source at every distance (0.35, post-fader),
    /// so the reverberant field of a footstep at 1.6 m and of a car at 60 m were the same fraction
    /// of each — no critical distance anywhere — and the unit's own gain on top of that put a
    /// footstep's reverberation 12 dB OVER the step, onto the master limiter: "footsteps loud, pop pop
    /// pop, piling up". The unit's gain is normalised separately (FmodAudioProvider metering); this is
    /// the law.
    /// </summary>
    public static float ReverberantToDirectPower(float enclosure, float meanFreePathMetres, float distanceMetres)
        => ReverberantToDirectPower(enclosure, meanFreePathMetres,
                                    CubeSurfaceOverMfpSquared * meanFreePathMetres * meanFreePathMetres,
                                    distanceMetres);

    /// <summary>
    /// The same ratio, with the room's surface area MEASURED rather than assumed to be a cube's.
    ///
    /// The cube assumption is the one term above that is not a measurement, and on a city it is the
    /// one that is furthest wrong. S ≈ 13.5·MFP² is exact for a cube and hopeless for anything flat
    /// or long: a car park 21 by 28 metres and 2.5 high has a mean free path of 4.3 m, which a cube
    /// would give 246 m² of surface — and it really has 1,390. A room with five times the surface
    /// absorbs five times as much, so the reverberant field the cube form predicted was nine
    /// decibels too loud, and every footstep in that garage went to the master limiter's ceiling and
    /// stayed there (measured: `--enclosure map=city at=-20,1.6,30` read a 298 % send where the
    /// classical room equation says 170 %).
    ///
    /// Slabs and tubes are what a city is made of — garages, corridors, tunnels, streets — so this is
    /// not a corner case. <see cref="Look"/> measures S from the same sphere of rays that measures
    /// everything else: ∮(d²/cosθ)dω is the surface area of any convex room seen from any point
    /// inside it.
    /// </summary>
    public static float ReverberantToDirectPower(float enclosure, float meanFreePathMetres,
                                                 float surfaceAreaSquareMetres, float distanceMetres)
    {
        float e = Math.Clamp(enclosure, 0f, 0.999f);
        if (e <= 1e-6f) return 0f;
        float mfp = MathF.Max(0.5f, meanFreePathMetres);
        float s = surfaceAreaSquareMetres > 1f
            ? surfaceAreaSquareMetres
            : CubeSurfaceOverMfpSquared * mfp * mfp;    // nothing measured: the old cube
        // A source further off than the room is wide is not IN this room in the sense the diffuse
        // field assumes; its share stops growing there.
        float r = MathF.Min(MathF.Max(0.1f, distanceMetres), 3f * mfp);
        return 16f * MathF.PI * r * r * e / (s * (1f - e));
    }

    /// <summary>16π over the surface area a box has per square metre of its mean free path
    /// (S ≈ 13.5·MFP² for a cube: S = 6L², MFP = 4V/S = 2L/3).</summary>
    private const float BoxSurfaceOverMfpSquared = 16f * MathF.PI / 13.5f;

    /// <summary>The surface a CUBE has per square metre of its mean free path: S = 6L² and
    /// MFP = 2L/3, so S = 13.5·MFP². This is the assumption <see cref="ReverberantToDirectPower"/>
    /// falls back on when nothing measured the real surface — and the assumption that was wrong by a
    /// factor of five on anything flat or long.</summary>
    private const float CubeSurfaceOverMfpSquared = 13.5f;

    public static float ReverberantGainDb(float enclosure)
    {
        float e = Math.Clamp(enclosure, 0f, 0.999f);
        if (e <= 1e-6f) return -80f;
        return 10f * MathF.Log10(e / (1f - e));
    }

    /// <summary>
    /// What <see cref="ReverberantGainDb"/> reads inside a room that is closed on every side and made
    /// of something hard — the top of the scale, and the reference everything else is quieter than.
    ///
    /// 0.98 rather than 1.0 because a real sealed room has a door, a window and some furniture in it;
    /// at 1.0 the series diverges, which is a room with no losses at all and does not exist.
    /// </summary>
    public static readonly float SealedRoomGainDb = ReverberantGainDb(0.98f);

    /// <summary>
    /// Evenly spread directions on the sphere, by the golden angle. Deterministic and stable: the same
    /// index always gives the same direction, so two measurements of the same place agree exactly.
    /// </summary>
    public static Vector3 SphereDirection(int index, int count)
    {
        // y walks the sphere's height uniformly — equal bands of height are equal areas on a sphere —
        // and the golden angle spaces the longitudes so no two rays line up.
        float y = 1f - 2f * (index + 0.5f) / count;
        float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        float theta = index * 2.399963f;   // golden angle, radians
        return new Vector3(r * MathF.Cos(theta), y, r * MathF.Sin(theta));
    }
}
