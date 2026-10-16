using System.Numerics;

namespace OpenFPS.Common;

/// <summary>What kind of glass it is, which decides almost everything about what happens when you
/// shoot it: a player can learn to tell a shop front from a car window by ear.</summary>
public enum GlassType
{
    /// <summary>Ordinary plate glass: old windows, picture frames, cabinets. Cracks from the impact
    /// point and comes down in large, irregular shards.</summary>
    Annealed,
    /// <summary>Toughened glass: car side windows, shop fronts, balustrades. Held in compression, so the
    /// whole pane fails the instant it is breached, into small blunt cubes; it cannot be punctured.</summary>
    Tempered,
    /// <summary>Two sheets on a plastic interlayer: windscreens, security glazing. Takes the hole and
    /// keeps the pane: a dull crunch and no fall at all.</summary>
    Laminated,
}

/// <summary>One sound the breaking of a pane produces.</summary>
public enum GlassEventKind
{
    /// <summary>A round going through without destroying the pane.</summary>
    Puncture,
    /// <summary>The pane failing, at the window.</summary>
    Shatter,
    /// <summary>Fragments leaving the frame: a shower at the window, moving downward.</summary>
    Shard,
    /// <summary>A fragment reaching the ground, at the foot of the wall.</summary>
    Landing,
}

/// <summary>One sound, when it happens and where. <paramref name="Pitch"/> varies per fragment so the
/// recordings do not repeat.</summary>
public readonly record struct GlassEvent(
    GlassEventKind Kind,
    float DelaySeconds,
    Vector3 Position,
    float Volume,
    float Pitch);

/// <summary>A pane, as the acoustics need to know it.</summary>
/// <param name="Centre">Centre of the pane in world space.</param>
/// <param name="Size">Width and height of the opening, metres.</param>
/// <param name="Normal">Outward normal: which way the fragments go when it fails.</param>
/// <param name="HeightAboveGround">Height of the bottom edge above the ground it will fall to, metres:
/// what the delay before the landing encodes.</param>
public readonly record struct GlassPane(
    Vector3 Centre,
    Vector2 Size,
    Vector3 Normal,
    GlassType Type,
    float HeightAboveGround);

/// <summary>
/// Shooting out a window, as a sequence of sounds with times and places. A window five floors up makes
/// two sounds from two places: the break at the window, then, sqrt(2h/g) later, the glass at the foot of
/// the wall. The gap tells a player who cannot see the building which floor it was, as the crack-to-report
/// gap does in <see cref="Ballistics"/>. Deterministic given a seed, so server and clients agree.
/// </summary>
public static class GlassBreak
{
    public const int MaxEventsPerBreak = 32;

    /// <summary>How long a fragment takes to fall <paramref name="height"/> metres: sqrt(2h/g), with the
    /// map's gravity.</summary>
    public static float FallSeconds(float height, float gravity = Earth.StandardGravity)
    {
        if (height <= 0f || gravity <= 0.01f) return 0f;
        return MathF.Sqrt(2f * height / gravity);
    }

    /// <summary>The inverse: how high the window was, from the gap a listener heard.</summary>
    public static float HeightFromFallDelay(float seconds, float gravity = Earth.StandardGravity)
        => seconds <= 0f ? 0f : 0.5f * gravity * seconds * seconds;

    /// <summary>Whether a hit destroys the pane or goes through it. Tempered always fails, laminated
    /// never; annealed shatters to buckshot or a slow heavy round, and a fast light one drills it.</summary>
    public static bool Shatters(GlassType type, WeaponDefinition weapon)
    {
        switch (type)
        {
            case GlassType.Tempered: return true;
            case GlassType.Laminated: return false;
            default:
                if (weapon.PelletsPerShot > 1) return true;
                return weapon.MuzzleVelocity < 500f;
        }
    }

    /// <summary>The whole sequence for one round striking one pane at <paramref name="impact"/>, world
    /// space. Returns how many events were written.</summary>
    public static int Resolve(GlassPane pane, Vector3 impact, WeaponDefinition weapon, int seed,
                              Span<GlassEvent> events, float gravity = Earth.StandardGravity)
    {
        int n = 0;
        var rng = new Random(seed);

        if (!Shatters(pane.Type, weapon))
        {
            // A hole, and the pane stays up: the silence where glass was expected is the information.
            events[n++] = new GlassEvent(GlassEventKind.Puncture, 0f, impact,
                                         pane.Type == GlassType.Laminated ? 0.8f : 1f,
                                         Pitch(rng, 1.15f, 0.12f));
            return n;
        }

        events[n++] = new GlassEvent(GlassEventKind.Shatter, 0f, pane.Centre, 1f, Pitch(rng, 1f, 0.08f));

        // Fragments leaving the frame over the first fraction of a second, spread over the opening.
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

        // The arrival, which carries the height: at the foot of the wall, thrown a little outward, a
        // different direction from the break.
        float fall = FallSeconds(pane.HeightAboveGround, gravity);
        if (fall <= 0.01f) return n;

        Vector3 ground = new(pane.Centre.X, pane.Centre.Y - pane.HeightAboveGround, pane.Centre.Z);
        ground += pane.Normal * 0.8f;

        // A scatter, longer from higher up: the fragments left over a couple of hundred milliseconds.
        int landings = Math.Min(events.Length - n, pane.Type == GlassType.Tempered ? 6 : 4);
        float spread = 0.18f + fall * 0.35f;
        for (int i = 0; i < landings; i++)
        {
            float t = fall + (float)rng.NextDouble() * spread;
            Vector3 at = ground + Offset(rng, 1.2f, 0f) + pane.Normal * (float)(rng.NextDouble() * 1.4f);
            // The big pieces arrive first and loudest.
            float vol = (i == 0 ? 0.9f : 0.4f + (float)rng.NextDouble() * 0.3f);
            events[n++] = new GlassEvent(GlassEventKind.Landing, t, at, vol, Pitch(rng, 1f, 0.3f));
        }

        return n;
    }

    private static float Pitch(Random rng, float centre, float spread)
        => centre * (1f - spread * 0.5f + (float)rng.NextDouble() * spread);

    /// <summary>A random offset within a rectangle; horizontal only when <paramref name="halfHeight"/> is
    /// zero.</summary>
    private static Vector3 Offset(Random rng, float halfWidth, float halfHeight)
        => new((float)(rng.NextDouble() * 2 - 1) * halfWidth,
               (float)(rng.NextDouble() * 2 - 1) * halfHeight,
               (float)(rng.NextDouble() * 2 - 1) * halfWidth);

    /// <summary>The folder a glass event plays from.</summary>
    public static string SoundFolderFor(GlassEventKind kind) => kind switch
    {
        GlassEventKind.Puncture => "GLASS/TINKLE",     // a short bright tick; the pool has plenty
        GlassEventKind.Shatter => "GLASS/SHATTER",
        GlassEventKind.Shard => "GLASS/TINKLE",
        GlassEventKind.Landing => "GLASS/TINKLE",
        _ => "",
    };
}
