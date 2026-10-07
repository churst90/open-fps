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
    /// The knock, from the impact model every other collision uses: a body (Skin) against the thing's
    /// own material, the face it met deciding the note and its mass the weight.
    /// </summary>
    public static List<TransientSound> Sound(in EntitySnapshot struck, BodyContact c, Vector3 where, bool running)
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
