using System.Linq;
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
                                          float GainHigh, float Scattering, int SurfaceId, int Order = 1);

    /// <summary>
    /// How many surfaces a copy may have come off on its way: one is the classic first-order image;
    /// two and three are the copies of copies.
    ///
    /// Indoors they do not matter as separate events — a second bounce in a ten-metre room arrives
    /// inside the fusion window and is the room's tail, which the reverb already is. Outdoors they are
    /// the sound of the place: two facades across a street hand a clap back and forth, and each
    /// crossing arrives a street's width later than the last. That flutter is second- and third-order
    /// and nothing else in the model makes it.
    /// </summary>
    public const int MaxOrder = 3;

    /// <summary>
    /// How many surfaces the higher orders are built from: the loudest first-order mirrors.
    ///
    /// The count of images grows as K for first order, K² for second and K³ for third, and a city
    /// has thousands of faces in range. The copies of copies that are loud enough to hear come off
    /// the surfaces that already reflect the sound loudly once — a weak mirror's third-order copy has
    /// lost three surfaces' worth — so the dozen strongest carry them, and the geometry prunes most of
    /// the 1,700 chains before any line of sight is cast.
    /// </summary>
    public const int HigherOrderSurfaces = 8;

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
    /// <param name="maxOrder">How many surfaces a copy may come off: 1 (the default) for first-order
    /// only; up to <see cref="MaxOrder"/>. A caller asks for more only where the copies of copies are
    /// SPARSE — out in the open, between facades — because in a room they are dense, they are the
    /// room's tail, and the reverb already is that.</param>
    /// <param name="separateFirst">Rank arrivals the ear hears as separate events ahead of fused ones
    /// when the budget cuts. A renderer that only voices separate events wants this; one that renders
    /// the near surfaces (your own footsteps off the ceiling a metre overhead) does not.</param>
    /// <param name="flutter">Also follow the sound back and forth between facing facades, past
    /// <see cref="MaxOrder"/> (see <see cref="FindFlutter"/>), and keep up to
    /// <see cref="MaxFlutterArrivals"/> arrivals rather than <see cref="MaxArrivals"/>. For one-off
    /// sounds out of doors.</param>
    public static void Find(Vector3 source, Vector3 listener, IReadOnlyList<Solid> solids,
                            List<Arrival> into, float speedOfSound = 343.0f,
                            int maxOrder = 1, bool separateFirst = false, bool flutter = false)
    {
        into.Clear();
        if (solids == null || solids.Count == 0) return;

        float direct = Vector3.Distance(source, listener);
        if (direct < 1e-3f) return;
        (_mirrors ??= new List<(int, int, float)>()).Clear();

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
                (_mirrors ??= new List<(int, int, float)>()).Add((i, f, gMid));
            }
        }

        if (Math.Min(maxOrder, MaxOrder) >= 2)
            FindHigherOrders(source, listener, direct, solids, into, speedOfSound, Math.Min(maxOrder, MaxOrder));
        if (flutter)
            FindFlutter(source, listener, direct, solids, into, speedOfSound);

        // Only as many as a listener can tell apart, and the ones they CAN tell apart first.
        //
        // The cap used to keep the loudest four — and the loudest are the ground and the nearest wall,
        // a few milliseconds behind the direct sound, inside the fusion window: the room, which the
        // renderer drops, because a fused copy is not a voice. So the cap spent its slots on arrivals
        // that were never going to play, and the far facade's slapback and the flutter between two
        // facades — the echoes you actually hear as echoes — were cut to make room for them. Separate
        // events first, then loudest; a budget cut has to take what nobody would have heard.
        into.Sort((a, b) =>
        {
            bool sa = separateFirst && IsSeparateEvent(a), sb = separateFirst && IsSeparateEvent(b);
            if (sa != sb) return sb.CompareTo(sa);
            float ea = MathF.Max(a.GainLow, MathF.Max(a.GainMid, a.GainHigh));
            float eb = MathF.Max(b.GainLow, MathF.Max(b.GainMid, b.GainHigh));
            return eb.CompareTo(ea);
        });
        int keep = flutter ? MaxFlutterArrivals : MaxArrivals;
        if (into.Count > keep) into.RemoveRange(keep, into.Count - keep);

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

    /// <summary>One mirror a higher-order copy can come off: a face, and what it keeps per band.</summary>
    private readonly record struct Mirror(int Solid, int Face, Vector3 Centre, Vector3 Normal,
                                          Vector3 U, Vector3 V, float HalfU, float HalfV,
                                          float KeepLow, float KeepMid, float KeepHigh, float Scattering);

    [ThreadStatic] private static List<(Mirror M, float Score)>? _mirrorScratch;
    [ThreadStatic] private static Vector3[]? _images, _hits;
    [ThreadStatic] private static int[]? _chain;
    [ThreadStatic] private static List<(int Solid, int Face, float Gain)>? _mirrors;

    /// <summary>
    /// The copies of copies: every chain of two or three surfaces, drawn from the nearest dozen, that
    /// sends a sound from the source to the ear. The image-source method, applied again: mirror the
    /// source through the first face, mirror THAT through the second, and so on; the ear hears the
    /// last image, and the path is found by walking back from the ear through each face in turn. Any
    /// step whose crossing point falls off its face, or whose leg is blocked, and the chain is not a
    /// path.
    /// </summary>
    private static void FindHigherOrders(Vector3 source, Vector3 listener, float direct,
                                         IReadOnlyList<Solid> solids, List<Arrival> into, float speedOfSound,
                                         int maxOrder)
    {
        // The mirrors are the surfaces that already sent this sound to this ear once: every face that
        // gave a valid first-order copy. That is the whole of "a surface that can take part", and it
        // is found, not guessed — scoring faces by size and distance picked the ground and the floor
        // slabs INSIDE the towers on Main Street (huge, near, and behind the facade), and not one
        // chain survived. A face that cannot reflect the sound to you directly is either hidden or
        // facing away, and a chain through it is very nearly always one or the other too.
        var cand = _mirrorScratch ??= new List<(Mirror, float)>(64);
        cand.Clear();
        foreach (var (si, f, gain) in _mirrors ?? new List<(int, int, float)>())
        {
            var s = solids[si];
            if (!FacePlane(s, f, out var c, out var n, out var u, out var v, out float hu, out float hv)) continue;
            var p = AcousticRegistry.GetProperties(s.Material);
            cand.Add((new Mirror(si, f, c, n, u, v, hu, hv,
                                 1f - Math.Clamp(p.AbsorptionLow, 0f, 1f), 1f - Math.Clamp(p.AbsorptionMid, 0f, 1f),
                                 1f - Math.Clamp(p.AbsorptionHigh, 0f, 1f), Math.Clamp(p.Scattering, 0f, 1f)), -gain));
        }
        if (cand.Count < 2) return;
        cand.Sort(static (a, b) => a.Score.CompareTo(b.Score));
        int k = Math.Min(HigherOrderSurfaces, cand.Count);

        var images = _images ??= new Vector3[MaxOrder + 1];
        var hits = _hits ??= new Vector3[MaxOrder + 1];
        var chain = _chain ??= new int[MaxOrder];

        void Try(int order)
        {
            // The images, forward from the source.
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
            if (pathLength > RangeMetres || pathLength <= direct) return;
            float spread = direct / pathLength;
            if (MathF.Max(keepL, MathF.Max(keepM, keepH)) * spread < MinRelativeAmplitude) return;

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
                if (!LegIsClear(prev, next, solids, skipA, skipB)) return;
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
    /// The sound handed back and forth across a street: the flutter.
    ///
    /// Reported: "I heard gun shots but didn't really hear them reflect off walls, no wash like they
    /// would in a real city" — and, asked what the wash IS: "a bunch of different cracks off every
    /// surface all at slightly different times." It is. Two rows of facades across a street return a
    /// shot to each other a crossing at a time, each copy a street's width of travel later and a
    /// little weaker and more smeared than the last, until it is a roll rather than a train. The chain
    /// search above stops at three surfaces and at the eight strongest single faces, and a street is
    /// not eight faces — it is two PLANES, each made of every building along one side. A copy that
    /// crosses the street a dozen times lands on a dozen different buildings.
    ///
    /// So this finds the planes: every vertical face in range with both ends in front of it, grouped
    /// by where it lies. Pairs of planes that face each other are the streets. For each, the images
    /// alternate from one side to the other; the path back from the ear has to land on SOME building
    /// in each plane at every crossing — a gap between buildings is where the sound leaves the street,
    /// and a chain through it is not a path — and every leg has to be clear. What each crossing keeps
    /// is the building it actually hit. Each crossing also scatters: the copy is made a little more
    /// diffuse per bounce, which is what turns a train of cracks into a wash.
    /// </summary>
    private static void FindFlutter(Vector3 source, Vector3 listener, float direct,
                                    IReadOnlyList<Solid> solids, List<Arrival> into, float speedOfSound)
    {
        // Every vertical face with both ends in front of it, by the way it faces.
        var byNormal = _planes ??= new Dictionary<long, List<(Mirror M, float Offset)>>();
        foreach (var l in byNormal.Values) l.Clear();
        for (int i = 0; i < solids.Count; i++)
        {
            var s = solids[i];
            if (s.Size.X <= 0f || s.Size.Y <= 0f || s.Size.Z <= 0f) continue;
            // A wall of the street the sound is in lies within half the widest street of the line
            // from the source to the ear.
            float reach = 60f + s.Size.Length() * 0.5f;
            if (DistanceSquaredToSegment(s.Center, source, listener) > reach * reach) continue;
            for (int f = 0; f < 6; f++)
            {
                if (f == 2 || f == 3) continue;                               // tops and bottoms
                if (!FacePlane(s, f, out var c, out var n, out var u, out var v, out float hu, out float hv)) continue;
                if (MathF.Abs(n.Y) > 0.2f) continue;                          // walls, not floors
                if (Vector3.Dot(source - c, n) <= 0.01f || Vector3.Dot(listener - c, n) <= 0.01f) continue;
                long key = ((long)MathF.Round(n.X * 20f) & 0xFF) | (((long)MathF.Round(n.Z * 20f) & 0xFF) << 8);
                if (!byNormal.TryGetValue(key, out var list)) byNormal[key] = list = new List<(Mirror, float)>();
                var p = AcousticRegistry.GetProperties(s.Material);
                list.Add((new Mirror(i, f, c, n, u, v, hu, hv,
                                     1f - Math.Clamp(p.AbsorptionLow, 0f, 1f), 1f - Math.Clamp(p.AbsorptionMid, 0f, 1f),
                                     1f - Math.Clamp(p.AbsorptionHigh, 0f, 1f), Math.Clamp(p.Scattering, 0f, 1f)),
                          Vector3.Dot(listener - c, n)));
            }
        }

        // A WALL is every face facing that way within a metre and a half of the nearest: a street's
        // facade is storeys stacked on each other, shopfront glass set back from the brick, a door
        // in its reveal. Asked to be exactly coplanar it fell apart into dozens of little walls and a
        // copy at head height landed on "nothing" at every shop window. For each direction, the wall
        // that bounds the listener is the NEAREST one in front of them; the next street over is not
        // the street they are standing in.
        var walls = _planeList ??= new List<List<(Mirror M, float Offset)>>();
        walls.Clear();
        foreach (var l in byNormal.Values)
        {
            if (l.Count == 0) continue;
            l.Sort(static (x, y) => x.Offset.CompareTo(y.Offset));
            // Nearest first, in clusters a metre and a half deep; the first cluster with a wall's worth
            // of face in it is the wall. A railing, a shelter's back panel or a bollard is nearer than
            // the buildings and is not what the street is made of.
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

        // Every leg of every chain runs between the two walls and between the source and the ear, so
        // only what stands in that stretch of street can block one. Found once here: testing each leg
        // against every solid within range cost 30 ms a shot on Main Street, 160 at worst.
        var legSolids = _legSolids ??= new List<Solid>();
        var localIndex = _legIndex ??= new List<int>();
        for (int pi = 0; pi < Math.Min(2, pairList.Count); pi++)
        {
            int a = pairList[pi].A, b = pairList[pi].B;
            var na = groups[a][0].M.Normal;
            float width = pairList[pi].Width;
            // Only what stands IN this street — between its two walls, along the stretch between the
            // source and the ear — can block a crossing. The buildings behind the walls cannot, and
            // testing every storey of both facades for every leg was most of the cost.
            legSolids.Clear(); localIndex.Clear();
            {
                var wa = groups[a][0].M; var wb = groups[b][0].M;
                for (int i = 0; i < solids.Count; i++)
                {
                    var sd = solids[i];
                    if (Vector3.Dot(sd.Center - wa.Centre, wa.Normal) <= 0.3f) continue;
                    if (Vector3.Dot(sd.Center - wb.Centre, wb.Normal) <= 0.3f) continue;
                    float r = sd.Size.Length() * 0.5f + width;
                    if (DistanceSquaredToSegment(sd.Center, source, listener) > r * r) continue;
                    legSolids.Add(sd); localIndex.Add(i);
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
                    if (path > FlutterRangeMetres) break;
                    float spread = direct / path;
                    if (spread < MinRelativeAmplitude) break;

                    // Back from the ear, crossing by crossing: which building did it hit each time?
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
                    if (MathF.Max(keepL, MathF.Max(keepM, keepH)) * spread < MinRelativeAmplitude) break;

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

                    // Every crossing scatters some of what is left: a mirror at the first, a wash by
                    // the tenth. What stays coherent is what the surfaces did not scatter.
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
    [ThreadStatic] private static List<Solid>? _legSolids;
    [ThreadStatic] private static List<int>? _legIndex;

    private static float DistanceSquaredToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / MathF.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
        return Vector3.DistanceSquared(p, a + ab * t);
    }

    /// <summary><see cref="LegIsClear"/> over a pre-filtered list, skipping by the original index.</summary>
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
    private static bool LegIsClear(Vector3 a, Vector3 b, IReadOnlyList<Solid> solids, int skip, int skip2 = -1)
    {
        for (int i = 0; i < solids.Count; i++)
        {
            if (i == skip || i == skip2) continue;
            var s = solids[i];
            if (GeometryUtils.LineIntersectsOBB(a, b, s.Center, s.Size, s.Rotation)) return false;
        }
        return true;
    }
}
