using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// Walking into something: once per contact, a knock from where you touched it, and its name (Cody,
/// 2026-10-04: "we need a bump sound and I need to hear what I ran into"). Once per contact means:
///   * a bump needs intent, the attempted motion aimed at the surface by at least
///     <see cref="MinIntent"/> of its speed; brushing along a wall is not running into it.
///   * it is one contact until you are <see cref="RearmMetres"/> clear of where you last touched, so
///     pressing again or sliding along a wall of many panels is the same contact.
///   * within it, only a different entity whose face turns more than <see cref="NewFaceDegrees"/> from
///     every face touched bumps again: a corner is two walls, a wall of panels one.
/// Only faces that stand up: never the ground (<see cref="Sightline.IsGround"/>), a ceiling or a step.
/// </summary>
public sealed class WallBumps
{
    public const float MinIntent = 0.5f;
    public const float RearmMetres = 0.5f;
    public const float NewFaceDegrees = 30f;

    private bool _inContact;
    private Vector3 _lastTouch;
    private readonly List<(int Id, Vector3 Normal)> _touched = new();

    /// <summary>Forget the contact: a teleport, a spawn, getting into a seat.</summary>
    public void Reset()
    {
        _inContact = false;
        _touched.Clear();
    }

    /// <summary>
    /// One fresh movement step. <paramref name="contact"/> is what the body was held off this step,
    /// already filtered of the ground (null for none); <paramref name="feet"/> where the body is now.
    /// True when this step is a new bump.
    /// </summary>
    public bool Update(BodyContact? contact, Vector3 feet)
    {
        if (contact is not { } c)
        {
            if (_inContact && Horizontal(feet - _lastTouch).Length() >= RearmMetres) Reset();
            return false;
        }

        Vector3 normal = Horizontal(c.Normal);
        if (normal.LengthSquared() < 1e-6f) return false;
        normal = Vector3.Normalize(normal);
        bool aimed = c.Intent >= MinIntent;

        bool bump = false;
        if (!_inContact)
        {
            // Brushing past is not a contact at all; only meeting it is.
            if (!aimed) return false;
            bump = true;
            _inContact = true;
        }
        else if (aimed && IsNewFace(c.EntityId, normal))
        {
            bump = true;
        }

        // Any touch keeps the contact alive; only a touch you pressed into marks the thing as met.
        _lastTouch = feet;
        if (aimed && !Touched(c.EntityId)) _touched.Add((c.EntityId, normal));
        return bump;
    }

    /// <summary>
    /// The contact as a bump sees it: the entity it was, or null when it was nothing that counts —
    /// an entity this client no longer holds, a face that does not stand up (a ceiling met on a jump,
    /// the top of something), or the ground (a floor's edge, a kerb, a step you could take).
    /// </summary>
    public static BodyContact? Resolve(BodyContact? contact, WorldSnapshot world, float eyeHeight, out EntitySnapshot struck)
    {
        struck = default;
        if (contact is not { } c) return null;
        bool known = world.Entities.TryGetValue(c.EntityId, out struck);
        if (!known)
            foreach (var d in world.DynamicEntities)
                if (d.Id == c.EntityId) { struck = d; known = true; break; }
        if (!known || MathF.Abs(c.Normal.Y) > 0.5f || Sightline.IsGround(struck, c.Feet.Y, c.Feet.Y + eyeHeight))
            return null;
        // Anything whose top is within a step of the feet is a step, whatever its shape: a stair
        // tread built as a column, a low kerb block. You step up it, you do not walk into it.
        if (Sightline.WorldBounds(struck).Max.Y <= c.Feet.Y + PhysicsConstants.StepHeight + 0.05f) return null;
        return c;
    }

    private bool Touched(int id)
    {
        foreach (var t in _touched) if (t.Id == id) return true;
        return false;
    }

    private bool IsNewFace(int id, Vector3 normal)
    {
        if (Touched(id)) return false;
        float cos = MathF.Cos(NewFaceDegrees * MathF.PI / 180f);
        foreach (var t in _touched)
            if (Vector3.Dot(t.Normal, normal) >= cos) return false;
        return true;
    }

    private static Vector3 Horizontal(Vector3 v) => new(v.X, 0f, v.Z);

    // ── What it sounds like ─────────────────────────────────────────────────────────────────────

    /// <summary>The part of you that meets it first — a hand, a forearm, a shoulder — in kilograms.
    /// What decides the blow is the reduced mass, and against a wall that is this.</summary>
    public const float LeadingMassKg = 4f;
    /// <summary>A walking body's pace into the wall, metres a second; the game's walk is a jog (see
    /// PhysicsConstants.WalkSpeed), and nobody meets a wall at a jog's full speed.</summary>
    public const float WalkingPace = 1.2f;
    public const float RunningPace = 2.0f;

    /// <summary>
    /// Where the body touched it: on the surface, at shoulder height unless the thing is lower than
    /// that, a couple of centimetres proud so the knock is not heard through the very thing it hit.
    /// </summary>
    public static Vector3 TouchPoint(in EntitySnapshot struck, BodyContact c)
    {
        var (min, max) = Sightline.WorldBounds(struck);
        Vector3 n = new(c.Normal.X, 0f, c.Normal.Z);
        n = n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitZ;
        float lo = MathF.Max(c.Feet.Y + 0.2f, min.Y + 0.05f);
        float hi = MathF.Min(c.Feet.Y + 1.4f, max.Y - 0.05f);
        float y = hi >= lo ? hi : 0.5f * (min.Y + max.Y);
        Vector3 p = c.Feet - n * (PhysicsConstants.PlayerRadius - 0.02f);
        return new Vector3(p.X, y, p.Z);
    }

    /// <summary>
    /// The bump: a body striking the thing (docs/MATTER.md 7.3), a palm, the toe of a shoe and the shoulder on
    /// the thing's own modes from its material, shape and size (StruckThings.Describe: a stud wall's board between
    /// its studs, a glass door loose in its latch, a fence's pale on its bolt, a car's panels). A person met is
    /// still the plain impact of two bodies (<see cref="PersonSound"/>).
    /// </summary>
    public static List<TransientSound> Sound(in EntitySnapshot struck, BodyContact c, Vector3 where, bool running, int seed = 0)
    {
        if (Sightline.IsPerson(struck)) return PersonSound(struck, c, where, running);
        var strike = StrikeOf(struck, c, where, running, seed, out _);
        return new List<TransientSound>
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = where,
                // What the client's render of the key puts it at (WorldAudioPlayer.AtOwnLevel); this is a
                // starting figure for anything that reads the level before the render is back.
                LevelDb = BumpLevelDb,
                Hz = 200f, DecaySeconds = 0.5f, Noisiness = 0.6f,
                SynthKey = StruckThings.Key(strike),
            },
        };
    }

    /// <summary>A bump's level before its render is back, dB SPL at a metre: about a walking bump into a
    /// wall as the struck model and its anchor put it (`--struck renders`).</summary>
    public const float BumpLevelDb = 85f;

    /// <summary>
    /// The strike a bump is: the thing as StruckThings.Describe reads its box, the body's blows near where it was
    /// touched (the hand at shoulder height), at the closing speed. Quantised so a wall of one kind is a few
    /// renders, not one per bump: speed to 0.2 m/s, places to tenths of the face.
    /// </summary>
    public static Strike StrikeOf(in EntitySnapshot struck, BodyContact c, Vector3 where, bool running, int seed, out StruckThing thing)
    {
        var def = struck.Definition;
        var local = Vector3.Transform(c.Normal, Quaternion.Inverse(struck.Transform.Rotation));
        thing = StruckThings.Describe(def.Material.Material, def.Collider.Size, local, def.Acoustics.LeafMetres,
                                      def.Acoustics.StudSpacingMetres, def.Moves, isVehicle: struck.Wheels != null, out bool lengthIsUp,
                                      form: def.Collider.Form);
        // Where on the face: across from the box's own centre along the face, up from its bottom.
        var (min, max) = Sightline.WorldBounds(struck);
        float upFrac = max.Y - min.Y > 0.05f ? Math.Clamp((c.Feet.Y + 1.4f - min.Y) / (max.Y - min.Y), 0.05f, 0.95f) : 0.5f;
        Vector3 n = Vector3.Normalize(new Vector3(c.Normal.X, 0f, c.Normal.Z) + new Vector3(1e-6f, 0f, 0f));
        Vector3 along = new(-n.Z, 0f, n.X);
        Vector3 centre = (min + max) * 0.5f;
        float half = MathF.Max(0.05f, MathF.Abs(along.X) * (max.X - min.X) * 0.5f + MathF.Abs(along.Z) * (max.Z - min.Z) * 0.5f);
        float acrossFrac = Math.Clamp(0.5f + Vector3.Dot(where - centre, along) / (2f * half), 0.05f, 0.95f);
        float closing = (running ? RunningPace : WalkingPace) * Math.Clamp(c.Intent, 0f, 1f);
        closing = MathF.Max(ImpactAcoustics.MinimumSpeed + 0.1f, MathF.Round(closing / 0.2f) * 0.2f);
        float Tenth(float v) => MathF.Round(v * 10f) / 10f;
        return new Strike(thing, StruckThings.BodyBump(closing, Tenth(acrossFrac), Tenth(upFrac), lengthIsUp, 0f), seed & 3);
    }

    /// <summary>
    /// Walking into a person: the impact model every collision used before the struck things (ImpactAcoustics), a
    /// body (Skin) against them, weighing what a person weighs.
    /// </summary>
    public static List<TransientSound> PersonSound(in EntitySnapshot struck, BodyContact c, Vector3 where, bool running)
    {
        var def = struck.Definition;
        var body = AcousticRegistry.GetProperties("Skin");
        string mat = string.IsNullOrEmpty(def.Material.Material) ? "Generic" : def.Material.Material;
        var props = AcousticRegistry.GetProperties(mat);

        // The face that was met: the collider's own axis nearest the contact normal is its thickness,
        // the other two are the panel.
        var size = def.Collider.Size;
        var local = Vector3.Transform(c.Normal, Quaternion.Inverse(struck.Transform.Rotation));
        float ax = MathF.Abs(local.X), ay = MathF.Abs(local.Y), az = MathF.Abs(local.Z);
        float width, height, thickness;
        if (ax >= ay && ax >= az) { width = size.Z; height = size.Y; thickness = size.X; }
        else if (az >= ay) { width = size.X; height = size.Y; thickness = size.Z; }
        else { width = size.X; height = size.Z; thickness = size.Y; }

        float density = props.DensityKgM3 > 0f ? props.DensityKgM3 : 500f;
        // A person is not their collision box full of flesh (680 kg): they weigh what a person weighs.
        float massKg = Sightline.IsPerson(struck)
            ? PhysicsConstants.PersonMassKg
            : Math.Clamp(size.X * size.Y * size.Z * density, 1f, 1e6f);
        float closing = (running ? RunningPace : WalkingPace) * Math.Clamp(c.Intent, 0f, 1f);
        closing = MathF.Max(closing, ImpactAcoustics.MinimumSpeed + 0.05f);

        return ImpactAcoustics.Between(body, props, where, closing, LeadingMassKg, massKg,
                                       width, height, thickness, struckIsFixed: !def.Moves);
    }
}
