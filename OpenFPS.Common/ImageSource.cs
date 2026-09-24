using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>One flat reflecting face: a rectangle in space, with a material.</summary>
public readonly record struct ReflectingSurface(
    Vector3 Centre,
    Vector3 Normal,
    /// <summary>Two orthogonal in-plane axes, each scaled to HALF the face's extent along it. Storing
    /// them scaled means the containment test is two dot products and no normalisation.</summary>
    Vector3 HalfU,
    Vector3 HalfV,
    /// <summary>0 = perfect mirror, 1 = the surface eats everything. Concrete is about 0.02.</summary>
    float Absorption,
    /// <summary>Stable identity for this face, so a reflection keeps the same voice frame to frame
    /// instead of retriggering as a click every time the geometry is rescanned.</summary>
    int SurfaceId,
    /// <summary>How much of what it reflects leaves in every direction instead of as a mirror image.
    /// 0 is a polished slab, 1 is a surface that re-radiates everything diffusely. A grandstand full
    /// of people is near the top of that range; a concrete retaining wall is near the bottom.</summary>
    float Scattering = 0f);

/// <summary>
/// A sound arriving by way of one bounce: where it appears to come from, and when.
/// </summary>
public readonly record struct Reflection(
    /// <summary>Where the reflection appears to originate — the mirrored source. Rendering it HERE
    /// rather than at the bounce point is what makes it come from the right direction, because that
    /// is the direction the wavefront is actually travelling when it reaches the ear.</summary>
    Vector3 ApparentPosition,
    /// <summary>The point on the surface the sound bounced off, for debugging and for drawing.</summary>
    Vector3 BouncePoint,
    /// <summary>Seconds later than the direct sound. This is the whole point.</summary>
    float DelaySeconds,
    /// <summary>Total path length, source -> surface -> listener.</summary>
    float PathLength,
    /// <summary>Linear gain relative to the direct sound, from the extra distance and the surface.</summary>
    float Gain,
    int SurfaceId,
    /// <summary>True if this is the SCATTERED share of a surface's return rather than its mirror
    /// image: it radiates from the surface itself, so it arrives from the wall rather than from a
    /// point behind it, and several of them across the face is what makes a rough surface a wash
    /// instead of a copy.</summary>
    bool IsDiffuse = false,
    /// <summary>How rough what it came off is, 0..1 (see <see cref="ReflectingSurface.Scattering"/>).
    /// A renderer uses it to smear the arrival in time: a mirror hands the sound back intact, brick
    /// hands it back from a patch a few metres across with every part of it a little later than the
    /// next, and a copy that is identical to the direct sound is what phases against it.</summary>
    float Scattering = 0f);

/// <summary>
/// First-order specular reflections, by the image-source method.
///
/// This is what was missing, and the gap it fills is the difference between a street and a reverb
/// preset. A parametric reverb — which is what the outdoor bus is — gives a room a decay time, and a
/// decay time has no direction and no structure: every surface in the world blurs into one wash, and
/// what a listener hears is "somewhere reverberant" rather than "a street with a building down the
/// left side and a gap where the cross street is".
///
/// A first-order image source is the opposite: one bounce off one identified face, arriving at a
/// specific time from a specific direction. Mirror the source through the plane of the face and you
/// have, exactly, where the reflected wavefront appears to come from — that is not an approximation,
/// it is what the geometry does. A shot a hundred and fifty metres up the street bounces off the
/// facade beside the shooter and reaches you a fifth of a second after the direct sound, from over
/// there. THAT is hearing a building.
///
/// Deliberately source-to-listener rather than listener-centric. The engine's existing reflection
/// pass scans for surfaces within forty metres of the LISTENER and bounces off those, which finds the
/// wall you are standing next to and can never find the one the sound came off in the distance.
///
/// Pure, allocation-free and deterministic. The heavy part — deciding which surfaces are worth
/// testing — belongs to the caller, which knows its own spatial index.
/// </summary>
public static class ImageSource
{
    /// <summary>Beyond this a first-order reflection has spread and weakened past the point of being a
    /// distinct arrival; it belongs in the reverb tail instead.</summary>
    public const float MaxPathLength = 400f;

    /// <summary>A reflection quieter than this relative to the direct sound is not worth a voice.</summary>
    public const float MinGain = 0.02f;

    /// <summary>
    /// How far under the direct sound a ONE-SHOT's reflection can be and still be heard as an echo.
    ///
    /// A continuous source is different: an engine's reflection is part of the wash it makes against a
    /// wall and is worth having 34 dB down, which is what <see cref="MinGain"/> allows. A single event
    /// is heard once, and a copy of it far enough under the original is not heard as a second event at
    /// all — it is fused with the first and then masked by whatever happens next. Twenty decibels is
    /// about where that line sits.
    ///
    /// It is a RATIO, and that is what ties it to the loudness of the thing: your own footstep is a
    /// metre away and the wall is fifty, so its echo is forty decibels down and there is no echo —
    /// which is what a footstep outdoors actually does. The same footstep in a corridor with a wall
    /// two metres away is ten decibels down and slaps, which is also what it actually does. Nothing
    /// here knows what a footstep is; it knows how loud the copy is against the original.
    /// </summary>
    public const float EchoAudibleRatio = 0.1f;

    /// <summary>
    /// The frequency the reflector-size test is evaluated at, Hz.
    ///
    /// Whether a surface reflects or is diffracted around depends on its size against the WAVELENGTH,
    /// so strictly this is a per-band question: a garden fence mirrors 4 kHz and is invisible to 100 Hz.
    /// One representative frequency near the middle of a gunshot's energy is a reasonable compromise
    /// until reflections are rendered per band. Lower it and small objects stop reflecting; raise it
    /// and everything becomes a mirror.
    /// </summary>
    public const float FresnelReferenceHz = 700f;

    /// <summary>
    /// A reflection arriving sooner than this after the direct sound does not get its own voice.
    ///
    /// Not an optimisation — a correction. Below roughly this gap the ear does not hear a second
    /// arrival at all: the precedence effect fuses it with the direct sound, and what it changes is
    /// the TIMBRE (comb filtering) rather than adding an echo. The ground is the case that forces the
    /// issue. A shooter and a listener both about a metre and a half up, a hundred and seventy metres
    /// apart, have a ground-bounce path three CENTIMETRES longer than the direct one — a tenth of a
    /// millisecond. Rendered as its own voice at 98% gain, that is not a reflection off anything, it
    /// is the direct sound played twice, six decibels louder, with a comb filter over it. It was also
    /// crowding the genuinely distant bounces out of the voice budget.
    /// </summary>
    public const float MinDelaySeconds = 0.012f;

    /// <summary>
    /// Fills <paramref name="into"/> with the audible first-order reflections, strongest first, and
    /// returns how many were written.
    ///
    /// <paramref name="occluded"/> is an optional visibility test for the two legs of the path. Pass
    /// null to skip it — the reflections will then include some that a building is actually standing
    /// in front of, which is cheap and usually inaudible under the ones that are real.
    /// </summary>
    /// <param name="diffuseTaps">
    /// How many points across each surface also radiate its SCATTERED share, or 0 for mirrors only.
    ///
    /// A real surface does not send everything it reflects along one path. The rougher it is — and a
    /// grandstand full of seats and people is about as rough as a surface gets — the more of what it
    /// returns leaves in every direction at once, from the surface itself rather than from a point
    /// behind it. Sampling that at a few points across the face is what turns a crisp copy into a
    /// wash, because the taps arrive over the spread of path lengths the face covers rather than all
    /// at one instant.
    ///
    /// Off by default because it costs a voice per tap, and because a CONTINUOUS source's scattered
    /// energy is already accounted for elsewhere: the ray-traced RT60 that drives the outdoor reverb
    /// is exactly that field. Nothing accounts for a ONE-SHOT's, which is why the clapping came back
    /// off the grandstand as an exact copy of itself.
    /// </param>
    public static int FirstOrder(ReadOnlySpan<ReflectingSurface> surfaces, Vector3 source,
                                 Vector3 listener, float speedOfSound, Span<Reflection> into,
                                 Func<Vector3, Vector3, bool>? occluded = null, int diffuseTaps = 0)
    {
        if (into.Length == 0) return 0;
        float direct = Vector3.Distance(source, listener);
        int n = 0;

        for (int i = 0; i < surfaces.Length; i++)
        {
            var s = surfaces[i];
            Vector3 nrm = s.Normal;

            // Source and listener must both be on the FRONT of the face. A face they are behind is
            // the inside of a wall, and mirroring through it invents a reflection off a surface that
            // is not facing either of them.
            float dS = Vector3.Dot(source - s.Centre, nrm);
            float dL = Vector3.Dot(listener - s.Centre, nrm);
            if (dS <= 0.01f || dL <= 0.01f) continue;

            // The image source: the source mirrored through the plane. The reflected path from the
            // real source is exactly as long as the straight line from the image, which is the whole
            // trick — and it is also the direction the arriving wavefront came from.
            Vector3 image = source - 2f * dS * nrm;

            // Where the straight line from image to listener crosses the plane is the bounce point.
            // It exists because they are on opposite sides of it now.
            float denom = dL + dS;                       // = |image->listener| projected on the normal
            if (denom <= 1e-4f) continue;
            float t = dS / denom;
            Vector3 hit = image + (listener - image) * t;

            // ...but the MIRROR IMAGE only counts if the bounce lands ON the face rather than off the
            // end of it. This is the test that makes a gap in a row of buildings actually be a gap.
            // A face that fails it can still SCATTER to the listener — that is the difference between
            // a mirror, which only works from the one place it works from, and a rough surface, which
            // answers from wherever you are standing — so the specular test gates only the specular.
            Vector3 rel = hit - s.Centre;
            float u = Vector3.Dot(rel, s.HalfU) / MathF.Max(1e-6f, s.HalfU.LengthSquared());
            float v = Vector3.Dot(rel, s.HalfV) / MathF.Max(1e-6f, s.HalfV.LengthSquared());
            bool specularLandsOnTheFace = MathF.Abs(u) <= 1f && MathF.Abs(v) <= 1f;

            float reflected = 1f - Math.Clamp(s.Absorption, 0f, 1f);
            float scatter = Math.Clamp(s.Scattering, 0f, 1f);

            if (specularLandsOnTheFace)
            {
                float path = Vector3.Distance(image, listener);
                float delay = (path - direct) / MathF.Max(1f, speedOfSound);
                // Spreading loss, what the surface kept, and how much of it there is — and then only
                // the share that leaves as a mirror image. A polished slab keeps nearly all of it
                // here; a stand full of people almost none.
                float gain = (direct / path) * reflected * (1f - scatter)
                           * ApertureFactor(s, source, listener, hit);

                if (path <= MaxPathLength && path > direct && gain >= MinGain && delay >= MinDelaySeconds
                    && (occluded == null || (!occluded(source, hit) && !occluded(hit, listener))))
                    n = Insert(into, n, new Reflection(image, hit, delay, path, gain, s.SurfaceId, false, scatter));
            }

            if (diffuseTaps > 0 && scatter > 0.01f)
                n = Scatter(into, n, s, source, listener, direct, speedOfSound, reflected * scatter,
                            diffuseTaps, occluded);
        }
        return n;
    }

    /// <summary>
    /// The share a surface sends back in every direction, sampled at a few points across it.
    ///
    /// Each tap is the surface radiating from where it is, so what reaches the listener is the
    /// Lambert fraction: the patch collects what falls on it from the source, spreads it over a
    /// hemisphere, and the listener gets the part aimed their way —
    /// <c>A·cosθs·cosθl·direct² / (π·rs²·rl²)</c> as an ENERGY ratio against the direct sound. That
    /// is the "a lot of it is taken by the open space" term, and it is also why a big surface close
    /// to the source still comes back strongly: a wall right behind a crowd catches a large part of
    /// what they make.
    ///
    /// Material coefficients stay in the amplitude convention the rest of this file uses, so only the
    /// geometry goes through the square root.
    /// </summary>
    private static int Scatter(Span<Reflection> into, int n, in ReflectingSurface s,
                               Vector3 source, Vector3 listener, float direct, float speedOfSound,
                               float returned, int taps, Func<Vector3, Vector3, bool>? occluded)
    {
        Vector3 nrm = s.Normal;
        // Spread the taps along the face's longer axis: that is the axis whose spread of path lengths
        // smears the arrival, and the smear is what stops it being a copy.
        bool uLonger = s.HalfU.LengthSquared() >= s.HalfV.LengthSquared();
        Vector3 along = uLonger ? s.HalfU : s.HalfV;
        float area = 4f * s.HalfU.Length() * s.HalfV.Length();
        if (area <= 0.01f) return n;

        for (int t = 0; t < taps; t++)
        {
            // Evenly across the face: two taps sit at a quarter and three quarters of its width.
            float u = taps == 1 ? 0f : -0.9f + 1.8f * t / (taps - 1);
            Vector3 tap = s.Centre + along * u;

            float rS = Vector3.Distance(source, tap), rL = Vector3.Distance(tap, listener);
            if (rS < 0.5f || rL < 0.5f) continue;
            float cosS = Math.Clamp(Vector3.Dot(Vector3.Normalize(source - tap), nrm), 0f, 1f);
            float cosL = Math.Clamp(Vector3.Dot(Vector3.Normalize(listener - tap), nrm), 0f, 1f);
            if (cosS <= 0f || cosL <= 0f) continue;

            float path = rS + rL;
            if (path > MaxPathLength || path <= direct) continue;
            float delay = (path - direct) / MathF.Max(1f, speedOfSound);
            if (delay < MinDelaySeconds) continue;

            float energy = (area / taps) * cosS * cosL * direct * direct
                         / (MathF.PI * rS * rS * rL * rL);
            float gain = MathF.Sqrt(MathF.Max(0f, energy)) * returned;
            if (gain < MinGain) continue;

            if (occluded != null && (occluded(source, tap) || occluded(tap, listener))) continue;

            // It radiates from the surface, so that is where it is heard from — not from an image
            // behind the wall. Identified apart from the specular arrival so a caller keeping one
            // voice per surface does not confuse the two.
            n = Insert(into, n, new Reflection(tap, tap, delay, path, gain,
                                               unchecked(s.SurfaceId * 397 + t + 1), true, 1f));
        }
        return n;
    }

    /// <summary>
    /// The six faces of an axis-aligned box, appended to <paramref name="into"/>.
    ///
    /// Buildings arrive as box colliders, and a box is six rectangles. Only the faces that can be seen
    /// from outside matter, which is all of them for a building standing in a street.
    /// </summary>
    public static int FacesOfBox(Vector3 centre, Vector3 size, float absorption, int baseId,
                                 Span<ReflectingSurface> into, float scattering = 0f)
    {
        if (into.Length < 6) return 0;
        Vector3 h = size * 0.5f;
        var x = new Vector3(h.X, 0, 0);
        var y = new Vector3(0, h.Y, 0);
        var z = new Vector3(0, 0, h.Z);

        into[0] = new ReflectingSurface(centre + x, Vector3.UnitX, y, z, absorption, baseId + 0, scattering);
        into[1] = new ReflectingSurface(centre - x, -Vector3.UnitX, y, z, absorption, baseId + 1, scattering);
        into[2] = new ReflectingSurface(centre + y, Vector3.UnitY, x, z, absorption, baseId + 2, scattering);
        into[3] = new ReflectingSurface(centre - y, -Vector3.UnitY, x, z, absorption, baseId + 3, scattering);
        into[4] = new ReflectingSurface(centre + z, Vector3.UnitZ, x, y, absorption, baseId + 4, scattering);
        into[5] = new ReflectingSurface(centre - z, -Vector3.UnitZ, x, y, absorption, baseId + 5, scattering);
        return 6;
    }

    /// <summary>
    /// The six faces of a box that has been ROTATED, appended to <paramref name="into"/>.
    ///
    /// A wall that follows a curve — the retaining wall of an oval, say — is a run of straight
    /// segments each turned to its own angle, and every one of them is a mirror at that angle. Taking
    /// their axis-aligned bounding boxes instead would point every face along X or Z and send the
    /// reflections off in directions the wall does not face, which on a circular track is the
    /// difference between the sound coming back off the wall you are looking at and coming back off
    /// nothing in particular.
    /// </summary>
    public static int FacesOfBox(Vector3 centre, Vector3 size, Quaternion rotation, float absorption,
                                 int baseId, Span<ReflectingSurface> into, float scattering = 0f)
    {
        if (into.Length < 6) return 0;
        Vector3 h = size * 0.5f;
        var x = Vector3.Transform(new Vector3(h.X, 0, 0), rotation);
        var y = Vector3.Transform(new Vector3(0, h.Y, 0), rotation);
        var z = Vector3.Transform(new Vector3(0, 0, h.Z), rotation);
        Vector3 nx = Norm(x), ny = Norm(y), nz = Norm(z);

        into[0] = new ReflectingSurface(centre + x,  nx, y, z, absorption, baseId + 0, scattering);
        into[1] = new ReflectingSurface(centre - x, -nx, y, z, absorption, baseId + 1, scattering);
        into[2] = new ReflectingSurface(centre + y,  ny, x, z, absorption, baseId + 2, scattering);
        into[3] = new ReflectingSurface(centre - y, -ny, x, z, absorption, baseId + 3, scattering);
        into[4] = new ReflectingSurface(centre + z,  nz, x, y, absorption, baseId + 4, scattering);
        into[5] = new ReflectingSurface(centre - z, -nz, x, y, absorption, baseId + 5, scattering);
        return 6;
    }

    private static Vector3 Norm(Vector3 v)
        => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitY;

    /// <summary>
    /// Second-order reflections: source -> surface A -> surface B -> listener.
    ///
    /// This is the one that makes a street sound like a street. A pair of flat parallel walls offers
    /// exactly ONE first-order bounce each, so a shot down the middle of a road produces two discrete
    /// reflections and then nothing — and with nothing else arriving, the diffuse reverb tail is all
    /// that is left and the whole world collapses into "one big echoey room". The second order is
    /// where a canyon's character actually lives: left wall to right wall to ear, then right to left,
    /// a whole series of them marching out in time. That repeating slap between two facades is the
    /// flutter every real street has and no reverb decay reproduces, because a decay has no direction
    /// and these each arrive from a definite one.
    ///
    /// Ordered pairs, because A-then-B and B-then-A are different paths arriving at different times.
    /// Quadratic in the surface count, so callers hand it the faces worth considering rather than
    /// every face in the map.
    /// </summary>
    public static int SecondOrder(ReadOnlySpan<ReflectingSurface> surfaces, Vector3 source,
                                  Vector3 listener, float speedOfSound, Span<Reflection> into,
                                  Func<Vector3, Vector3, bool>? occluded = null)
    {
        if (into.Length == 0) return 0;
        float direct = Vector3.Distance(source, listener);
        int n = 0;

        for (int a = 0; a < surfaces.Length; a++)
        {
            var A = surfaces[a];
            float dS = Vector3.Dot(source - A.Centre, A.Normal);
            if (dS <= 0.01f) continue;                       // source is behind A
            Vector3 image1 = source - 2f * dS * A.Normal;

            for (int b = 0; b < surfaces.Length; b++)
            {
                if (b == a) continue;
                var B = surfaces[b];

                // The first image has to be in front of B, and the listener too, or the second
                // mirroring is through a face neither of them can see.
                float d1 = Vector3.Dot(image1 - B.Centre, B.Normal);
                float dL = Vector3.Dot(listener - B.Centre, B.Normal);
                if (d1 <= 0.01f || dL <= 0.01f) continue;

                Vector3 image2 = image1 - 2f * d1 * B.Normal;

                // Where the straight line from the second image reaches the listener crosses B.
                if (!CrossesFace(B, image2, listener, out Vector3 p2)) continue;
                // ...and the leg before it, from the first image to that point, must cross A.
                if (!CrossesFace(A, image1, p2, out Vector3 p1)) continue;

                float path = Vector3.Distance(image2, listener);
                if (path > MaxPathLength || path <= direct) continue;

                float delay = (path - direct) / MathF.Max(1f, speedOfSound);
                if (delay < MinDelaySeconds) continue;

                // Two surfaces, so absorption and the size test both apply twice.
                float gain = (direct / path)
                           * (1f - Math.Clamp(A.Absorption, 0f, 1f))
                           * (1f - Math.Clamp(B.Absorption, 0f, 1f))
                           * ApertureFactor(A, source, p2, p1)
                           * ApertureFactor(B, p1, listener, p2);
                if (gain < MinGain) continue;

                if (occluded != null &&
                    (occluded(source, p1) || occluded(p1, p2) || occluded(p2, listener))) continue;

                n = Insert(into, n, new Reflection(image2, p2, delay, path, gain,
                                                   A.SurfaceId * 31 + B.SurfaceId, false,
                                                   MathF.Max(A.Scattering, B.Scattering)));
            }
        }
        return n;
    }

    /// <summary>
    /// How much of a reflection a surface actually returns, given its SIZE.
    ///
    /// The model without this treats a garden fence and a fifty-storey tower as equally reflective at
    /// the same distance, which is plainly wrong and is the thing to fix: a big building twenty metres
    /// away sends back far more than a small one does, because far more of the expanding wavefront
    /// lands on it.
    ///
    /// The physical criterion is the FIRST FRESNEL ZONE — the ellipse on the surface that actually
    /// contributes to the specular reflection, with radius sqrt(lambda * d1 * d2 / (d1 + d2)) at the
    /// bounce point. A reflector much larger than that zone behaves as an infinite plane and returns
    /// the full specular reflection. One smaller than it intercepts only part of the contributing
    /// area, and the rest of the wave simply diffracts past; the return falls roughly as the area
    /// ratio.
    ///
    /// It is frequency dependent through lambda, which is also correct and is why a fence is a mirror
    /// to a whistle and transparent to a lorry. And it grows with distance: the zone at a bounce point
    /// two hundred metres away is many metres across, so only genuinely large things — buildings —
    /// still reflect from there. That is exactly the distance/height/size interaction that matters.
    /// </summary>
    private static float ApertureFactor(in ReflectingSurface s, Vector3 source, Vector3 listener,
                                        Vector3 hit)
    {
        float d1 = Vector3.Distance(source, hit);
        float d2 = Vector3.Distance(hit, listener);
        if (d1 <= 0.01f || d2 <= 0.01f) return 1f;

        float lambda = 343f / MathF.Max(1f, FresnelReferenceHz);
        float zone = MathF.Sqrt(lambda * d1 * d2 / (d1 + d2));
        if (zone <= 1e-4f) return 1f;

        // The face's own half-extents. A face is a rectangle, so the smaller of the two decides
        // whether the zone fits: a tall narrow strip reflects like a narrow strip.
        float halfU = s.HalfU.Length();
        float halfV = s.HalfV.Length();

        // How much of the zone the surface covers, per axis, capped at 1 — past the zone boundary a
        // bigger surface adds nothing, which is why a skyscraper and a merely large wall sound alike.
        float coverU = Math.Clamp(halfU / zone, 0f, 1f);
        float coverV = Math.Clamp(halfV / zone, 0f, 1f);

        // Area ratio, but on amplitude rather than energy, so a surface covering a quarter of the zone
        // returns half the amplitude rather than a sixteenth.
        return MathF.Sqrt(coverU * coverV);
    }

    /// <summary>Where the segment crosses the face's plane, and whether that point is on the face.</summary>
    private static bool CrossesFace(in ReflectingSurface s, Vector3 from, Vector3 to, out Vector3 hit)
    {
        hit = default;
        float dF = Vector3.Dot(from - s.Centre, s.Normal);
        float dT = Vector3.Dot(to - s.Centre, s.Normal);
        if (dF * dT >= 0f) return false;                     // both the same side: no crossing
        float t = dF / (dF - dT);
        hit = from + (to - from) * t;

        Vector3 rel = hit - s.Centre;
        float u = Vector3.Dot(rel, s.HalfU) / MathF.Max(1e-6f, s.HalfU.LengthSquared());
        float v = Vector3.Dot(rel, s.HalfV) / MathF.Max(1e-6f, s.HalfV.LengthSquared());
        return MathF.Abs(u) <= 1f && MathF.Abs(v) <= 1f;
    }

    /// <summary>
    /// Two arrivals this close in time and direction are ONE arrival, and the second is an artefact
    /// of how the wall was built rather than anything a listener could hear as separate.
    ///
    /// A long wall is authored as a run of segments, and segments are given a little overlap so the
    /// run has no gaps in it. At a join both segments' faces contain the same bounce point, so the
    /// same reflection is found twice — two voices spent on one wall, arriving together, six decibels
    /// louder than the wall really is. A curved wall has a join every few metres, so on a circuit
    /// this is not an edge case; it is most of the reflections.
    /// </summary>
    private const float DuplicateMetres = 0.75f;

    /// <summary>Keeps the strongest reflections in gain order, so a caller with six voices spends them
    /// on the six that matter rather than the first six found.</summary>
    private static int Insert(Span<Reflection> into, int count, in Reflection r)
    {
        for (int i = 0; i < count; i++)
            if (Vector3.DistanceSquared(into[i].BouncePoint, r.BouncePoint) < DuplicateMetres * DuplicateMetres
                && MathF.Abs(into[i].PathLength - r.PathLength) < DuplicateMetres)
                return count;

        int at = count < into.Length ? count : into.Length;
        while (at > 0 && into[at - 1].Gain < r.Gain) at--;
        if (at >= into.Length) return count;
        for (int k = Math.Min(count, into.Length - 1); k > at; k--) into[k] = into[k - 1];
        into[at] = r;
        return count < into.Length ? count + 1 : count;
    }
}
