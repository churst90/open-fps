using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// The first thing a room does to a sound: sends you a copy of it off every surface.
///
/// A reflection is not an effect applied to a signal. It is the SAME sound arriving a second time,
/// from somewhere else, a little later and a little duller — and that is all a first-order image
/// source is. Mirror the source through the plane of a wall and you have a second source standing
/// behind that wall; the path from it to the ear, through the point where it crosses, is exactly the
/// path the reflection took. Nothing about it is a guess: the position is geometry, the delay is the
/// extra distance over the speed of sound, and what is missing from it is what the wall absorbed.
///
/// This exists because the alternative kept being wrong in both directions. A parametric reverb tail
/// has no direction in it at all, so a room answered a sound from everywhere at once and a doorway
/// could not be heard from outside — reported as "shouldn't it just be the natural reflections off the
/// surfaces rather than a blanket reverb". And the ray-tracing generator it replaced jittered every
/// surface normal with a fresh random seed per call, so a wall's reflection moved every frame.
///
/// One model, deterministic, driven by the boxes a map is built from and the materials they are made
/// of. A corridor answers like a corridor because it has walls close on both sides, a field answers
/// with nothing because there is nothing to mirror through, and neither case is written down anywhere.
/// </summary>
public static class EarlyReflections
{
    /// <summary>A surface sound can come off: where it is, how big, how it is turned, what it is made
    /// of. The same shape the acoustic scene and <see cref="Enclosure"/> already use.</summary>
    public readonly record struct Solid(Vector3 Center, Vector3 Size, Quaternion Rotation, string Material);

    /// <summary>
    /// One arrival: where it appears to come from, how far it travelled, and what survived the trip.
    /// </summary>
    /// <param name="ImagePosition">The mirrored source — where the ear should place this arrival.</param>
    /// <param name="PathLength">Total distance travelled, metres: source to surface to ear.</param>
    /// <param name="ExtraDelaySeconds">How much later than the direct sound it arrives.</param>
    /// <param name="SurfaceId">Stable identity of the surface it came off, so a wall's reflection keeps
    /// one voice from frame to frame instead of being torn down and rebuilt.</param>
    public readonly record struct Arrival(Vector3 ImagePosition, Vector3 HitPoint, float PathLength,
                                          float ExtraDelaySeconds, float GainLow, float GainMid,
                                          float GainHigh, float Scattering, int SurfaceId);

    /// <summary>
    /// How far the search looks, metres. A bound on the work, not a statement about audibility.
    ///
    /// What decides whether a copy is worth having is <see cref="MinRelativeAmplitude"/>, and that
    /// prunes by itself: a copy that travelled ten times as far as the direct sound arrives twenty
    /// decibels down and falls out on its own. So this wants to be generous rather than tight — it was
    /// sixty metres, borrowed from <see cref="Enclosure.ReverberantRangeMetres"/>, and that is the
    /// horizon for a REVERBERANT field, which is a different question. A facade a hundred metres down
    /// a street and a grandstand across a racetrack are the clearest thing a listener has to go on out
    /// there, and sixty metres cannot reach either of them.
    /// </summary>
    public const float RangeMetres = 200f;

    /// <summary>
    /// How many arrivals to keep, loudest first.
    ///
    /// A room has six surfaces and a street has two that matter; past a handful the copies are closer
    /// together than the ear resolves and belong in the tail. This is what stops a scene of a hundred
    /// boxes turning into a hundred voices.
    /// </summary>
    public const int MaxArrivals = 4;

    /// <summary>
    /// Quietest arrival worth a voice, as a fraction of the direct sound's amplitude at the same
    /// distance. Below this it is inaudible under the sound it is a copy of.
    /// </summary>
    public const float MinRelativeAmplitude = 0.05f;

    /// <summary>
    /// How late a copy has to be before the ear hears it as a SEPARATE arrival, seconds.
    ///
    /// This is the line between a reflection and an echo, and it is a fact about hearing rather than
    /// about geometry. Inside about fifty milliseconds the ear fuses a copy with the sound it is a copy
    /// of — it does not perceive two events, it perceives one event that is wider, and slightly
    /// coloured, and located where the FIRST arrival came from. Past it, the copy is a second event
    /// with a place of its own, which is what a slapback off a distant wall is.
    ///
    /// It matters here because a fused reflection cannot be rendered as another voice. Two voices are
    /// two independent playbacks: even scheduled to start at the right moment, they are two reads of
    /// the same sound at unrelated positions in it, and for anything sustained — a siren, a machine, a
    /// megaphone repeating an announcement — that is not a reflection, it is a second copy of the
    /// announcement. Reported exactly so: "the megaphone is like repeating echoing, not an
    /// environmental reverb... if I stand by the megaphone, I hear it repeat softer but in the same
    /// place". Every arrival in a ten-metre room is inside 30 ms.
    ///
    /// So a fused arrival is not a voice. It is the room, and it goes to the room — which is measured
    /// from the same surfaces (see <see cref="Enclosure.Look"/>). Only an arrival late enough to be its
    /// own event gets its own voice, and out there it genuinely is one.
    ///
    /// The search itself does not apply this. It reports what the geometry does, every arrival of it,
    /// because that is a fact about the room; how each one is RENDERED is a decision about the ear and
    /// belongs to whatever is rendering. <see cref="IsSeparateEvent"/> is that decision, written once.
    /// </summary>
    public const float FusionSeconds = 0.05f;

    /// <summary>Is this arrival late enough to be heard as an event of its own, and therefore to be
    /// worth a voice? See <see cref="FusionSeconds"/>. Everything else is the room.</summary>
    public static bool IsSeparateEvent(in Arrival a) => a.ExtraDelaySeconds >= FusionSeconds;

    /// <summary>
    /// Every first-order reflection of <paramref name="source"/> that reaches <paramref name="listener"/>,
    /// strongest first.
    ///
    /// <paramref name="into"/> is cleared and filled, so a caller on the audio worker can keep one list
    /// and never allocate.
    /// </summary>
    public static void Find(Vector3 source, Vector3 listener, IReadOnlyList<Solid> solids,
                            List<Arrival> into, float speedOfSound = 343.0f)
    {
        into.Clear();
        if (solids == null || solids.Count == 0) return;

        float direct = Vector3.Distance(source, listener);
        if (direct < 1e-3f) return;

        for (int i = 0; i < solids.Count; i++)
        {
            var s = solids[i];
            if (s.Size.X <= 0f || s.Size.Y <= 0f || s.Size.Z <= 0f) continue;

            // Out of the horizon entirely: its own bounding sphere cannot reach.
            float reach = RangeMetres + s.Size.Length() * 0.5f;
            if (Vector3.DistanceSquared(listener, s.Center) > reach * reach) continue;

            var p = AcousticRegistry.GetProperties(s.Material);
            // What the surface sends back, per band. The registry's absorption is what it TAKES.
            float keepLow = 1f - Math.Clamp(p.AbsorptionLow, 0f, 1f);
            float keepMid = 1f - Math.Clamp(p.AbsorptionMid, 0f, 1f);
            float keepHigh = 1f - Math.Clamp(p.AbsorptionHigh, 0f, 1f);
            if (MathF.Max(keepLow, MathF.Max(keepMid, keepHigh)) < MinRelativeAmplitude) continue;

            // Each of the six faces is a mirror. Only the one facing the listener can send anything
            // back, which the plane test below decides for itself.
            for (int f = 0; f < 6; f++)
            {
                if (!FacePlane(s, f, out Vector3 faceCentre, out Vector3 normal, out Vector3 uAxis,
                               out Vector3 vAxis, out float halfU, out float halfV)) continue;

                float dSource = Vector3.Dot(source - faceCentre, normal);
                float dListener = Vector3.Dot(listener - faceCentre, normal);
                // Both have to be in FRONT of the face. Behind it is inside the solid, and a mirror
                // has no back.
                if (dSource <= 0.01f || dListener <= 0.01f) continue;

                Vector3 image = source - 2f * dSource * normal;

                // Where the straight line from the image to the ear crosses the plane is the point the
                // sound bounced off. If that point is off the edge of the face, this surface is not in
                // the way of this particular reflection and there is nothing to hear.
                float denom = dListener + dSource;
                if (denom < 1e-4f) continue;
                Vector3 hit = image + (listener - image) * (dSource / denom);

                Vector3 local = hit - faceCentre;
                if (MathF.Abs(Vector3.Dot(local, uAxis)) > halfU) continue;
                if (MathF.Abs(Vector3.Dot(local, vAxis)) > halfV) continue;

                float pathLength = Vector3.Distance(source, hit) + Vector3.Distance(hit, listener);
                if (pathLength > RangeMetres) continue;

                // Spherical spreading: the copy travelled further than the direct sound, so it arrives
                // quieter in exactly that proportion. Nothing else is applied here — air absorption and
                // the distance model belong to whatever renders the arrival, which already does both.
                float spread = direct / pathLength;
                float gLow = keepLow * spread, gMid = keepMid * spread, gHigh = keepHigh * spread;
                if (MathF.Max(gLow, MathF.Max(gMid, gHigh)) < MinRelativeAmplitude) continue;

                // Both legs have to be clear of everything else, or this is a reflection off a wall
                // with a building in front of it.
                if (!LegIsClear(source, hit, solids, i)) continue;
                if (!LegIsClear(hit, listener, solids, i)) continue;


                into.Add(new Arrival(
                    image, hit, pathLength,
                    (pathLength - direct) / MathF.Max(1f, speedOfSound),
                    gLow, gMid, gHigh,
                    Math.Clamp(p.Scattering, 0f, 1f),
                    SurfaceId(i, f)));
            }
        }

        // Loudest first, and only as many as a listener can tell apart: a budget cut has to take the
        // arrivals nobody would have heard.
        into.Sort(static (a, b) =>
        {
            float ea = MathF.Max(a.GainLow, MathF.Max(a.GainMid, a.GainHigh));
            float eb = MathF.Max(b.GainLow, MathF.Max(b.GainMid, b.GainHigh));
            return eb.CompareTo(ea);
        });
        if (into.Count > MaxArrivals) into.RemoveRange(MaxArrivals, into.Count - MaxArrivals);

        // ── Then back into surface order, and that is not cosmetic ──────────────────────────────
        //
        // What survives is rendered into a small fixed set of voices, one per slot. If the slots are
        // filled in LOUDNESS order, two arrivals of nearly equal strength swap places the moment the
        // listener shifts a little — and a swap means each voice is suddenly playing the other one's
        // reflection, from somewhere else in the room. Ordering the survivors by the surface they came
        // off makes a slot mean the same wall from one tick to the next, for as long as the same walls
        // are answering, which is the whole of the time it matters.
        into.Sort(static (a, b) => a.SurfaceId.CompareTo(b.SurfaceId));
    }

    /// <summary>A surface's identity, stable for the life of a scene: which box, which face. A wall's
    /// reflection has to keep the same voice as the listener moves, or it restarts every frame.</summary>
    public static int SurfaceId(int solidIndex, int face) => solidIndex * 6 + face;

    /// <summary>
    /// One face of a box, in world space: its centre, its outward normal, and the two axes and half
    /// extents that say where its edges are.
    /// </summary>
    private static bool FacePlane(in Solid s, int face, out Vector3 centre, out Vector3 normal,
                                  out Vector3 uAxis, out Vector3 vAxis, out float halfU, out float halfV)
    {
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

    /// <summary>Is the straight run between two points clear of every solid except the one being
    /// reflected off? Its own face is the thing the sound is touching, so it cannot block itself.</summary>
    private static bool LegIsClear(Vector3 a, Vector3 b, IReadOnlyList<Solid> solids, int skip)
    {
        for (int i = 0; i < solids.Count; i++)
        {
            if (i == skip) continue;
            var s = solids[i];
            if (GeometryUtils.LineIntersectsOBB(a, b, s.Center, s.Size, s.Rotation)) return false;
        }
        return true;
    }
}
