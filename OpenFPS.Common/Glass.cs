using System;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// What kind of glass it is, which decides almost everything about what happens when you shoot it.
/// These are genuinely different materials, not difficulty settings, and they sound different enough
/// that a player can learn to tell a shop front from a car window by what happens to it.
/// </summary>
public enum GlassType
{
    /// <summary>Ordinary plate glass — old windows, picture frames, cabinets. Brittle and unstressed,
    /// so it cracks from the impact point and comes down in large, irregular, noisy shards.</summary>
    Annealed,
    /// <summary>Toughened glass — car side windows, shop fronts, balustrades. Held in compression, so
    /// the whole pane fails at once the instant it is breached and collapses into small blunt cubes.
    /// It cannot be punctured: there is no such thing as a neat hole in tempered glass.</summary>
    Tempered,
    /// <summary>Two sheets bonded to a plastic interlayer — windscreens, security glazing. It takes the
    /// hole and keeps the pane: a dull crunch and no fall at all, which is a very distinctive absence
    /// and tells a listener something about the building they are shooting at.</summary>
    Laminated,
}

/// <summary>One sound the breaking of a pane produces.</summary>
public enum GlassEventKind
{
    /// <summary>A round going through without destroying the pane. Small, sharp, immediate.</summary>
    Puncture,
    /// <summary>The pane failing. At the window.</summary>
    Shatter,
    /// <summary>Fragments leaving the frame and falling. A shower, at the window, moving downward.</summary>
    Shard,
    /// <summary>A fragment reaching the ground. At the FOOT of the wall, not at the window.</summary>
    Landing,
}

/// <summary>
/// One sound, when it happens and where.
/// <paramref name="Pitch"/> varies per fragment so twenty-four recordings do not read as twenty-four
/// recordings.
/// </summary>
public readonly record struct GlassEvent(
    GlassEventKind Kind,
    float DelaySeconds,
    Vector3 Position,
    float Volume,
    float Pitch);

/// <summary>A pane, as the acoustics need to know it.</summary>
public readonly record struct GlassPane(
    /// <summary>Centre of the pane in world space.</summary>
    Vector3 Centre,
    /// <summary>Width and height of the opening, metres.</summary>
    Vector2 Size,
    /// <summary>Outward normal — which way the fragments go when it fails.</summary>
    Vector3 Normal,
    GlassType Type,
    /// <summary>Height of the pane's bottom edge above the ground it will fall to, metres. THE number
    /// that makes this worth modelling: it is what the delay before the landing encodes.</summary>
    float HeightAboveGround);

/// <summary>
/// Shooting out a window, as a sequence of sounds with times and places.
///
/// The reason this is worth doing properly rather than playing one crash sample: a window shot out
/// five floors up makes TWO sounds separated by most of two seconds, and they come from two different
/// places. The break is up at the window. Then nothing. Then the glass arrives at the pavement, at the
/// foot of the wall, and the gap between them is sqrt(2h/g) — a direct readout of how high up the shot
/// was. A player who cannot see the building can hear which floor someone is on.
///
/// That is the same trick as the crack-to-report gap in <see cref="Ballistics"/>, and it comes from
/// the same place: let the physics be real and the information falls out of it for free. One crash
/// sample at the window throws it away, and a sighted game would never notice.
///
/// Pure and deterministic given a seed, so the server and every client agree about where the glass
/// went and a test can assert on the timing rather than on a description of it.
/// </summary>
public static class GlassBreak
{
    public const int MaxEventsPerBreak = 32;

    /// <summary>How long a fragment takes to fall <paramref name="height"/> metres: sqrt(2h/g).
    /// Gravity is passed in because the world's gravity is a map property and glass should fall at the
    /// same rate as everything else in it, not at a rate of its own.</summary>
    public static float FallSeconds(float height, float gravity = PhysicsConstants.Gravity)
    {
        if (height <= 0f || gravity <= 0.01f) return 0f;
        return MathF.Sqrt(2f * height / gravity);
    }

    /// <summary>The inverse: how high the window was, from the gap a listener heard. This is the sum
    /// the player's ear is doing, written down — for a spoken readout, for tuning, and for checking
    /// that the timing encodes what it claims to.</summary>
    public static float HeightFromFallDelay(float seconds, float gravity = PhysicsConstants.Gravity)
        => seconds <= 0f ? 0f : 0.5f * gravity * seconds * seconds;

    /// <summary>
    /// Whether a hit destroys the pane or merely goes through it.
    ///
    /// Tempered glass always fails completely — it is held in compression and breaching it anywhere
    /// releases the whole sheet, which is why a car window becomes a pile of cubes and never a pane
    /// with a hole in it. Laminated never fails: the interlayer holds the pieces. Annealed depends on
    /// what hit it — a fast small round can punch a surprisingly clean hole in old plate glass, while
    /// buckshot takes the whole thing out.
    /// </summary>
    public static bool Shatters(GlassType type, WeaponDefinition weapon)
    {
        switch (type)
        {
            case GlassType.Tempered: return true;
            case GlassType.Laminated: return false;
            default:
                // Buckshot spreads its energy over nine impacts across a wide area, which is far more
                // destructive to a brittle sheet than one fast round through the middle of it.
                if (weapon.PelletsPerShot > 1) return true;
                // A slow heavy pistol round smashes; a fast light rifle round tends to drill.
                return weapon.MuzzleVelocity < 500f;
        }
    }

    /// <summary>
    /// The whole sequence for one round striking one pane. Returns how many events were written.
    ///
    /// <paramref name="impact"/> is where the round hit, in world space.
    /// </summary>
    public static int Resolve(GlassPane pane, Vector3 impact, WeaponDefinition weapon, int seed,
                              Span<GlassEvent> events, float gravity = PhysicsConstants.Gravity)
    {
        int n = 0;
        var rng = new Random(seed);

        if (!Shatters(pane.Type, weapon))
        {
            // A hole, and the pane stays up. For laminated that is the whole event — and the SILENCE
            // where a listener expected glass to arrive is itself the information.
            events[n++] = new GlassEvent(GlassEventKind.Puncture, 0f, impact,
                                         pane.Type == GlassType.Laminated ? 0.8f : 1f,
                                         Pitch(rng, 1.15f, 0.12f));
            return n;
        }

        // 1. The pane fails, at the window.
        events[n++] = new GlassEvent(GlassEventKind.Shatter, 0f, pane.Centre, 1f, Pitch(rng, 1f, 0.08f));

        // 2. Fragments leaving the frame — a shower over the first fraction of a second, spreading
        //    out and down from the opening rather than all issuing from one point.
        int shards = pane.Type == GlassType.Tempered ? 8 : 5;
        float area = MathF.Max(0.2f, pane.Size.X * pane.Size.Y);
        shards = Math.Min(shards + (int)(area / 1.5f), 12);

        for (int i = 0; i < shards && n < events.Length - 4; i++)
        {
            float t = 0.02f + (float)rng.NextDouble() * 0.28f;
            Vector3 at = pane.Centre
                       + Offset(rng, pane.Size.X * 0.5f, pane.Size.Y * 0.5f)
                       + pane.Normal * (0.2f + (float)rng.NextDouble() * 0.6f);
            events[n++] = new GlassEvent(GlassEventKind.Shard, t, at,
                                         0.35f + (float)rng.NextDouble() * 0.35f,
                                         Pitch(rng, 1.1f, 0.35f));
        }

        // 3. The arrival. This is the part that carries the height.
        //
        //    It lands at the FOOT of the wall, displaced outward by however far the fragments were
        //    thrown — not at the window, and not under the listener. A pile of glass hitting the
        //    pavement forty metres away and five floors down is a completely different direction from
        //    the break that caused it, and hearing the two separately is the whole point.
        float fall = FallSeconds(pane.HeightAboveGround, gravity);
        if (fall <= 0.01f) return n;

        Vector3 ground = new(pane.Centre.X, pane.Centre.Y - pane.HeightAboveGround, pane.Centre.Z);
        ground += pane.Normal * 0.8f;

        // The fragments do not all arrive together: they leave over a couple of hundred milliseconds
        // and from slightly different heights, so the landing is a scatter, longer from higher up.
        int landings = Math.Min(events.Length - n, pane.Type == GlassType.Tempered ? 6 : 4);
        float spread = 0.18f + fall * 0.35f;
        for (int i = 0; i < landings; i++)
        {
            float t = fall + (float)rng.NextDouble() * spread;
            Vector3 at = ground + Offset(rng, 1.2f, 0f) + pane.Normal * (float)(rng.NextDouble() * 1.4f);
            // The first arrivals are the big pieces and the loudest.
            float vol = (i == 0 ? 0.9f : 0.4f + (float)rng.NextDouble() * 0.3f);
            events[n++] = new GlassEvent(GlassEventKind.Landing, t, at, vol, Pitch(rng, 1f, 0.3f));
        }

        return n;
    }

    private static float Pitch(Random rng, float centre, float spread)
        => centre * (1f - spread * 0.5f + (float)rng.NextDouble() * spread);

    /// <summary>A random offset within a rectangle, used to spread fragments over the opening and the
    /// landing over the pavement. Horizontal only when <paramref name="halfHeight"/> is zero.</summary>
    private static Vector3 Offset(Random rng, float halfWidth, float halfHeight)
        => new((float)(rng.NextDouble() * 2 - 1) * halfWidth,
               (float)(rng.NextDouble() * 2 - 1) * halfHeight,
               (float)(rng.NextDouble() * 2 - 1) * halfWidth);

    /// <summary>The folder a glass event plays from. One place, so nothing can drift.</summary>
    public static string SoundFolderFor(GlassEventKind kind) => kind switch
    {
        GlassEventKind.Puncture => "GLASS/TINKLE",     // a short bright tick; the pool has plenty
        GlassEventKind.Shatter => "GLASS/SHATTER",
        GlassEventKind.Shard => "GLASS/TINKLE",
        GlassEventKind.Landing => "GLASS/TINKLE",
        _ => "",
    };
}
