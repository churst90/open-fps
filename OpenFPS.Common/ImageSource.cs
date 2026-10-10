using System.Numerics;

namespace OpenFPS.Common;

/// <summary>One flat reflecting face: a rectangle in space, with a material.</summary>
/// <param name="HalfU">An in-plane axis scaled to half the face's extent along it, so the containment
/// test is two dot products and no normalisation.</param>
/// <param name="HalfV">The other in-plane axis, scaled the same way.</param>
/// <param name="Absorption">0 a perfect mirror, 1 the surface eats everything. Concrete is about 0.02.</param>
/// <param name="SurfaceId">Stable per face, so a reflection keeps its voice from frame to frame instead
/// of retriggering as a click at every rescan.</param>
/// <param name="Scattering">How much of what it reflects leaves in every direction instead of as a mirror
/// image: a polished slab is near 0, a grandstand full of people near 1.</param>
public readonly record struct ReflectingSurface(
    Vector3 Centre,
    Vector3 Normal,
    Vector3 HalfU,
    Vector3 HalfV,
    float Absorption,
    int SurfaceId,
    float Scattering = 0f);

/// <summary>A sound arriving by way of one bounce: where it appears to come from, and when.</summary>
/// <param name="ApparentPosition">The mirrored source. Rendered here, not at the bounce point, because this
/// is the direction the wavefront travels when it reaches the ear.</param>
/// <param name="BouncePoint">The point on the surface the sound bounced off.</param>
/// <param name="DelaySeconds">Seconds after the direct sound.</param>
/// <param name="PathLength">Source to surface to listener, metres.</param>
/// <param name="Gain">Linear gain relative to the direct sound, from the extra distance and the surface.</param>
/// <param name="IsDiffuse">The scattered share of a surface's return rather than its mirror image: it
/// radiates from the wall itself, and several across the face make a rough surface a wash, not a copy.</param>
/// <param name="Scattering">How rough the surface is, 0..1 (<see cref="ReflectingSurface.Scattering"/>),
/// for the renderer to smear the arrival in time: a copy identical to the direct sound phases against it.</param>
public readonly record struct Reflection(
    Vector3 ApparentPosition,
    Vector3 BouncePoint,
    float DelaySeconds,
    float PathLength,
    float Gain,
    int SurfaceId,
    bool IsDiffuse = false,
    float Scattering = 0f);

/// <summary>
/// First- and second-order reflections by the image-source method: one bounce off one identified face,
/// arriving at a definite time from a definite direction, which a reverb decay cannot give. Source to
/// listener rather than round the listener, so the facade a distant shot came off is found as well as
/// the wall beside you. Pure and allocation-free; choosing which surfaces to test is the caller's.
/// </summary>
public static class ImageSource
{
    /// <summary>A reflection arriving more than this much further than the direct sound (400 m, about
    /// 1.2 s behind) is no longer a distinct arrival; it belongs to the tail. Measured against the direct
    /// path, not as a total: as a total, a shot 350 m away could only echo off a wall almost on the line
    /// between (2026-09-28, "not really hearing reflections for gunshots, especially the far away ones").</summary>
    public const float MaxPathLength = 400f;

    /// <summary>
    /// What a surface's mirror copy loses at the bottom and the top, dB per bounce (never positive),
    /// relative to its broadband share. Scattering is quoted at mid-band and rises with frequency (the
    /// ISO 17497 curves climb about as the square root of frequency), so the specular share 1 - s keeps
    /// its bass and loses its top at every bounce (Cody, 2026-09-28: "the reflections get duller the more
    /// it bounces").
    /// </summary>
    public static (float LowDb, float HighDb) SpecularBandLossDb(float scattering, int bounces)
    {
        float s = Math.Clamp(scattering, 0f, 1f);
        if (s <= 0f || bounces <= 0) return (0f, 0f);
        float mid = 1f - s;
        // Band centres 200 Hz and 8 kHz against the quoted 1 kHz: sqrt(0.2) and sqrt(8).
        float sLow = s * 0.447f, sHigh = MathF.Min(1f, s * 2.83f);
        float low = MathF.Min(1f, (1f - sLow) / mid);
        float high = MathF.Max(0.1f, (1f - sHigh) / mid);        // a floor: some top always survives
        return (bounces * 20f * MathF.Log10(low), bounces * 20f * MathF.Log10(high));
    }

    /// <summary>A reflection quieter than this relative to the direct sound is not worth a voice.</summary>
    public const float MinGain = 0.02f;

    /// <summary>
    /// How far under the direct sound a one-shot's reflection can be and still be heard as an echo
    /// (about 20 dB); below it the copy fuses with the original and is masked. A continuous source's
    /// reflection is part of its wash and is kept down to <see cref="MinGain"/>. A ratio, so your own
    /// footstep with a wall fifty metres off (40 dB down) has no echo, and in a corridor with the wall two
    /// metres off (10 dB down) it slaps, as it does in life.
    /// </summary>
    public const float EchoAudibleRatio = 0.1f;

    /// <summary>
    /// The frequency the reflector-size test is evaluated at, Hz, near the middle of a gunshot's energy.
    /// Strictly a per-band question (a garden fence mirrors 4 kHz and is invisible to 100 Hz). Lower it
    /// and small objects stop reflecting; raise it and everything becomes a mirror.
    /// </summary>
    public const float FresnelReferenceHz = 700f;

    /// <summary>
    /// A reflection sooner than this after the direct sound gets no voice of its own: the precedence
    /// effect fuses it, and it colours the timbre (a comb) rather than adding an echo. The ground forced
    /// it: shooter and listener 1.5 m up and 170 m apart have a ground path 3 cm longer than the direct
    /// one, and rendered at 98 % gain it was the direct sound twice, 6 dB louder, under a comb filter.
    /// </summary>
    public const float MinDelaySeconds = 0.012f;

    /// <summary>
    /// Fills <paramref name="into"/> with the audible first-order reflections, strongest first, and
    /// returns how many. <paramref name="occluded"/> tests the path's two legs; null skips it and lets
    /// through some reflections a building stands in front of, cheap and usually inaudible.
    /// </summary>
    /// <param name="diffuseTaps">How many points across each surface also radiate its scattered share, or
    /// 0 for mirrors only. The taps arrive over the spread of path lengths the face covers, which turns a
    /// copy into a wash. Off by default: a voice per tap, and a continuous source's scattered field is the
    /// traced reverb already. Nothing else covers a one-shot's: without it the clapping came back off the
    /// grandstand as an exact copy of itself.</param>
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

            // Both in front of the face: behind it is the inside of a wall.
            float dS = Vector3.Dot(source - s.Centre, nrm);
            float dL = Vector3.Dot(listener - s.Centre, nrm);
            if (dS <= 0.01f || dL <= 0.01f) continue;

            // The reflected path is exactly as long as the straight line from the image, and arrives
            // from its direction.
            Vector3 image = source - 2f * dS * nrm;

            float denom = dL + dS;                       // = |image->listener| projected on the normal
            if (denom <= 1e-4f) continue;
            float t = dS / denom;
            Vector3 hit = image + (listener - image) * t;

            // The mirror image counts only if the bounce lands on the face: that makes a gap in a row
            // of buildings a gap. A face that fails can still scatter to the listener, so this gates
            // only the specular.
            Vector3 rel = hit - s.Centre;
            float u = Vector3.Dot(rel, s.HalfU) / MathF.Max(1e-6f, s.HalfU.LengthSquared());
            float v = Vector3.Dot(rel, s.HalfV) / MathF.Max(1e-6f, s.HalfV.LengthSquared());
            bool specularLandsOnTheFace = MathF.Abs(u) <= 1f && MathF.Abs(v) <= 1f;

            // Amplitudes from energy shares (EarlyReflections.Keep): the surface returns sqrt(1 - a) of
            // the pressure, the mirror sqrt(1 - s) of that and the scattered taps sqrt(s).
            float reflected = EarlyReflections.Keep(s.Absorption);
            float scatter = Math.Clamp(s.Scattering, 0f, 1f);

            if (specularLandsOnTheFace)
            {
                float path = Vector3.Distance(image, listener);
                float delay = (path - direct) / MathF.Max(1f, speedOfSound);
                // Spreading, what the surface kept, and only the share that leaves as a mirror image.
                float gain = (direct / path) * reflected * MathF.Sqrt(1f - scatter)
                           * ApertureFactor(s, source, listener, hit);

                if (path - direct <= MaxPathLength && path > direct && gain >= MinGain && delay >= MinDelaySeconds
                    && (occluded == null || (!occluded(source, hit) && !occluded(hit, listener))))
                    n = Insert(into, n, new Reflection(image, hit, delay, path, gain, s.SurfaceId, false, scatter));
            }

            if (diffuseTaps > 0 && scatter > 0.01f)
                n = Scatter(into, n, s, source, listener, direct, speedOfSound, reflected * MathF.Sqrt(scatter),
                            diffuseTaps, occluded);
        }
        return n;
    }

    /// <summary>
    /// The share a surface sends back in every direction, sampled at a few points across it. Each tap
    /// returns the Lambert fraction, <c>A·cosθs·cosθl·direct² / (π·rs²·rl²)</c> as an energy ratio to the
    /// direct sound, so a big surface close to the source (a wall behind a crowd) still comes back
    /// strongly. Material coefficients stay amplitudes; only the geometry goes through the square root.
    /// </summary>
    private static int Scatter(Span<Reflection> into, int n, in ReflectingSurface s,
                               Vector3 source, Vector3 listener, float direct, float speedOfSound,
                               float returned, int taps, Func<Vector3, Vector3, bool>? occluded)
    {
        Vector3 nrm = s.Normal;
        // Along the longer axis: its spread of path lengths smears the arrival.
        bool uLonger = s.HalfU.LengthSquared() >= s.HalfV.LengthSquared();
        Vector3 along = uLonger ? s.HalfU : s.HalfV;
        float area = 4f * s.HalfU.Length() * s.HalfV.Length();
        if (area <= 0.01f) return n;

        // A face far bigger than the scene (open ground a kilometre across) scatters from round the
        // bounce point, not its far ends: spread across the face, the taps landed hundreds of metres
        // away and the ground returned nothing.
        if (along.Length() > 4f * MathF.Max(direct, 5f))
            return ScatterNearBounce(into, n, s, source, listener, direct, speedOfSound, returned, taps, occluded);

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
            if (path - direct > MaxPathLength || path <= direct) continue;
            float delay = (path - direct) / MathF.Max(1f, speedOfSound);
            if (delay < MinDelaySeconds) continue;

            float energy = (area / taps) * cosS * cosL * direct * direct
                         / (MathF.PI * rS * rS * rL * rL);
            float gain = MathF.Sqrt(MathF.Max(0f, energy)) * returned;
            if (gain < MinGain) continue;

            if (occluded != null && (occluded(source, tap) || occluded(tap, listener))) continue;

            // Heard from the surface itself, with an id apart from the specular arrival's so a caller
            // keeping one voice per surface does not confuse the two.
            n = Insert(into, n, new Reflection(tap, tap, delay, path, gain,
                                               unchecked(s.SurfaceId * 397 + t + 1), true, 1f));
        }
        return n;
    }

    /// <summary>
    /// Scatter from the part of a big face that matters: round the bounce point, at the distances
    /// whose extra path puts the arrival 20 ms, 60 ms, 180 ms... after the direct sound, on alternate
    /// sides. Each tap stands for the ring of the face around its own distance from the bounce point,
    /// and returns that ring's energy by the same Lambert law.
    /// </summary>
    private static int ScatterNearBounce(Span<Reflection> into, int n, in ReflectingSurface s,
                                         Vector3 source, Vector3 listener, float direct, float speedOfSound,
                                         float returned, int taps, Func<Vector3, Vector3, bool>? occluded)
    {
        Vector3 nrm = s.Normal;
        float hS = Vector3.Dot(source - s.Centre, nrm), hL = Vector3.Dot(listener - s.Centre, nrm);
        if (hS <= 0.05f || hL <= 0.05f) return n;
        // The specular point: where the line from the source to the listener's image meets the plane.
        Vector3 image = listener - 2f * hL * nrm;
        Vector3 bounce = source + (image - source) * (hS / (hS + hL));
        // Across the source-listener direction, in the plane: the way the extra path grows fastest
        // and the arrival stays out of the direct sound's line.
        Vector3 along = (listener - source) - Vector3.Dot(listener - source, nrm) * nrm;
        Vector3 side = along.LengthSquared() > 1e-6f ? Vector3.Normalize(Vector3.Cross(nrm, along))
                                                    : Vector3.Normalize(s.HalfU);
        float limitU = s.HalfU.Length(), limitV = s.HalfV.Length();
        Vector3 uDir = Vector3.Normalize(s.HalfU), vDir = Vector3.Normalize(s.HalfV);
        float c = MathF.Max(1f, speedOfSound);

        for (int t = 0; t < taps; t++)
        {
            float wantDelay = 0.020f * MathF.Pow(3f, t);
            float wantPath = direct + wantDelay * c;
            Vector3 dir = t % 2 == 0 ? side : -side;
            // The distance from the bounce point that gives that path: path grows with it, so bisect.
            float lo = 0f, hi = MathF.Max(limitU, limitV);
            float Path(float rho)
            {
                Vector3 q = bounce + dir * rho;
                return Vector3.Distance(source, q) + Vector3.Distance(q, listener);
            }
            if (Path(hi) < wantPath) continue;
            for (int it = 0; it < 40; it++) { float mid = 0.5f * (lo + hi); if (Path(mid) < wantPath) lo = mid; else hi = mid; }
            float r = 0.5f * (lo + hi);
            Vector3 tap = bounce + dir * r;
            // Still on the face?
            Vector3 rel = tap - s.Centre;
            if (MathF.Abs(Vector3.Dot(rel, uDir)) > limitU || MathF.Abs(Vector3.Dot(rel, vDir)) > limitV) continue;

            float rS = Vector3.Distance(source, tap), rL = Vector3.Distance(tap, listener);
            float cosS = Math.Clamp(Vector3.Dot(Vector3.Normalize(source - tap), nrm), 0f, 1f);
            float cosL = Math.Clamp(Vector3.Dot(Vector3.Normalize(listener - tap), nrm), 0f, 1f);
            if (cosS <= 0f || cosL <= 0f) continue;
            float path = rS + rL;
            if (path - direct > MaxPathLength || path <= direct) continue;
            float delay = (path - direct) / c;
            if (delay < MinDelaySeconds) continue;

            // The ring this tap stands for. The delays triple from tap to tap, so the distances grow
            // by about root three; each tap takes the ring from 3^-1/4 to 3^1/4 of its own distance,
            // which tiles the face without counting any of it twice.
            float ring = MathF.PI * (MathF.Sqrt(3f) - 1f / MathF.Sqrt(3f)) * r * r;
            float energy = ring * cosS * cosL * direct * direct / (MathF.PI * rS * rS * rL * rL);
            float gain = MathF.Sqrt(MathF.Max(0f, energy)) * returned;
            if (gain < MinGain) continue;
            if (occluded != null && (occluded(source, tap) || occluded(tap, listener))) continue;

            n = Insert(into, n, new Reflection(tap, tap, delay, path, gain,
                                               unchecked(s.SurfaceId * 397 + t + 1), true, 1f));
        }
        return n;
    }

    /// <summary>The six faces of an axis-aligned box, appended to <paramref name="into"/>.</summary>
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
    /// The six faces of a rotated box, appended to <paramref name="into"/>. A curved wall is a run of
    /// turned segments, each a mirror at its own angle; their axis-aligned bounds would send the
    /// reflections off in directions the wall does not face.
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
    /// The facets of a shape (docs/GEOMETRY.md 3.4) at <paramref name="centre"/>, turned by
    /// <paramref name="rotation"/>, each the rectangle round it: a wedge's slope mirrors as a slope and a
    /// column's narrow sides as narrow sides, not as the faces of the box round them. Appended to
    /// <paramref name="into"/>; returns how many.
    /// </summary>
    public static int FacesOfShape(Geometry.MeshAsset shape, Vector3 centre, Quaternion rotation, float absorption,
                                   int baseId, List<ReflectingSurface> into, float scattering = 0f)
    {
        var facets = shape.Facets.Items;
        for (int i = 0; i < facets.Length; i++)
        {
            var f = facets[i];
            into.Add(new ReflectingSurface(centre + Vector3.Transform(f.RectCentre, rotation), Norm(Vector3.Transform(f.Normal, rotation)),
                                           Vector3.Transform(f.HalfU, rotation), Vector3.Transform(f.HalfV, rotation),
                                           absorption, baseId + i, scattering));
        }
        return facets.Length;
    }

    /// <summary>
    /// Second-order reflections, source to surface A to surface B to listener: the slap between two
    /// facades that every street has (two parallel walls give only one first-order bounce each). Ordered
    /// pairs, since A then B and B then A arrive at different times. Quadratic in the surface count, so
    /// callers pass only the faces worth considering.
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

                // The first image and the listener both in front of B.
                float d1 = Vector3.Dot(image1 - B.Centre, B.Normal);
                float dL = Vector3.Dot(listener - B.Centre, B.Normal);
                if (d1 <= 0.01f || dL <= 0.01f) continue;

                Vector3 image2 = image1 - 2f * d1 * B.Normal;

                if (!CrossesFace(B, image2, listener, out Vector3 p2)) continue;
                if (!CrossesFace(A, image1, p2, out Vector3 p1)) continue;

                float path = Vector3.Distance(image2, listener);
                if (path - direct > MaxPathLength || path <= direct) continue;

                float delay = (path - direct) / MathF.Max(1f, speedOfSound);
                if (delay < MinDelaySeconds) continue;

                float gain = (direct / path)
                           * EarlyReflections.Keep(A.Absorption)
                           * EarlyReflections.Keep(B.Absorption)
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
    /// How much of a reflection a surface returns for its size, by the first Fresnel zone, radius
    /// sqrt(λ d1 d2 / (d1 + d2)) at the bounce point. A face bigger than the zone is an infinite plane;
    /// a smaller one returns roughly its share of the zone's area and the rest diffracts past. The zone
    /// grows with distance, so from 200 m only buildings still reflect.
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

        float halfU = s.HalfU.Length();
        float halfV = s.HalfV.Length();

        // Per axis, capped at 1: past the zone a bigger surface adds nothing.
        float coverU = Math.Clamp(halfU / zone, 0f, 1f);
        float coverV = Math.Clamp(halfV / zone, 0f, 1f);

        // The area ratio as an amplitude: a quarter of the zone returns half, not a sixteenth.
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
    /// Two arrivals this close in bounce point and path are one. Wall segments overlap at their joins,
    /// so both faces find the same reflection: two voices, 6 dB louder than the wall. A curved wall has
    /// a join every few metres, so on a circuit that was most of the reflections.
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
