using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>What a scope can pick out: somebody, or something with an engine.</summary>
public enum SightKind { Person, Player, Vehicle, Aircraft, Train }

/// <summary>
/// One thing seen through the scope: what it is, where it is against the crosshair, and how it is
/// moving. <see cref="Right"/> and <see cref="Up"/> are radians from the crosshair to its aim point
/// (a person's chest, a vehicle's middle); <see cref="Feet"/> is where it stands.
/// </summary>
public readonly record struct Sighting(
    int Id, SightKind Kind, string Name, Vector3 Feet, Vector3 AimPoint, Vector3 Velocity,
    float Distance, float Right, float Up, bool OnCrosshair, float HalfWidth);

/// <summary>
/// What is in the scope's view, worked out from the world the client already has.
///
/// In view means three things: inside the scope's round field of view at its power, no further than
/// things can be made out at that power, and not hidden behind anything solid. Glass is seen through.
/// The occlusion is a line of sight from the eye to the aim point; something solid in the way within
/// a body's depth of the target is the target's own outline (a car's panels) and does not hide it.
/// </summary>
public static class ScopeView
{
    /// <summary>A person's chest above their feet: where the crosshair is put on somebody.</summary>
    public const float ChestHeight = 1.2f;

    /// <summary>What an entity is, to a scope, or null for anything it does not pick out.</summary>
    public static SightKind? Classify(in EntitySnapshot e)
    {
        var def = e.Definition;
        string sound = def.SoundEmitter.SoundId ?? "";
        if (sound.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) return SightKind.Vehicle;
        if (sound.StartsWith("aircraft:", StringComparison.OrdinalIgnoreCase)) return SightKind.Aircraft;
        if (sound.StartsWith("rail:", StringComparison.OrdinalIgnoreCase)) return SightKind.Train;
        if (def.Type == EntityType.Player) return SightKind.Player;
        // A walker is an NPC with no machine in it. A riding mower is a machine; somebody pushing a
        // push mower is a person.
        if (def.Type == EntityType.NPC && !sound.StartsWith("machine:", StringComparison.OrdinalIgnoreCase)) return SightKind.Person;
        return null;
    }

    private static bool IsBody(SightKind k) => k is SightKind.Person or SightKind.Player;

    /// <summary>The words for what something is: "person", a player's name, a vehicle's name.</summary>
    public static string NameOf(in EntitySnapshot e, SightKind kind)
    {
        string name = e.Definition.Identity.Name ?? "";
        return kind switch
        {
            // A person with a name of their own is called by it, as a player is (IdentityComponent.Named).
            SightKind.Person => e.Definition.Identity.Named && !string.IsNullOrWhiteSpace(name) ? name.Trim() : "person",
            SightKind.Player => string.IsNullOrWhiteSpace(name) ? "a player" : name,
            SightKind.Aircraft => string.IsNullOrWhiteSpace(name) ? "aircraft" : name,
            SightKind.Train => "train",
            _ => string.IsNullOrWhiteSpace(name) ? "vehicle" : name,
        };
    }

    /// <summary>
    /// Everything in view, left to right. <paramref name="eye"/> and the yaw and pitch are the
    /// crosshair's (with the sway in them).
    /// </summary>
    public static List<Sighting> InView(WorldSnapshot snap, SpatialService spatial, int ownId, Vector3 eye,
                                        float yaw, float pitch, float fovDegrees, float maxRange)
    {
        var seen = new List<Sighting>();
        Vector3 forward = ScopeMath.Forward(yaw, pitch);
        foreach (var e in snap.DynamicEntities)
        {
            if (e.Id == ownId) continue;
            if (Classify(e) is not { } kind) continue;
            bool body = IsBody(kind);
            Vector3 size = e.Definition.Collider.Size;
            Vector3 aim = body ? e.Transform.Position + new Vector3(0f, ChestHeight, 0f) : e.Transform.Position;
            var (r, u, ahead) = ScopeMath.Offset(eye, yaw, pitch, aim);
            if (!ScopeMath.InView(r, u, ahead, fovDegrees)) continue;
            float distance = Vector3.Distance(eye, aim);
            if (distance > maxRange) continue;

            // Hidden? A line of sight to the aim point; anything solid nearer than the target's own
            // depth hides it.
            float depth = body ? 0.4f : 0.5f * MathF.Max(size.X, MathF.Max(size.Y, size.Z)) + 1f;
            Vector3 to = aim - eye;
            int targetId = e.Id;
            if (spatial.CastSight(snap, eye, to, distance, out _, out float blocked, skip: s => s.Id == targetId)
                && blocked < distance - depth) continue;

            bool onCrosshair;
            float halfWidth;
            if (body)
            {
                onCrosshair = GeometryUtils.RayIntersectsCylinder(eye, forward, e.Transform.Position + new Vector3(0f, 0.9f, 0f),
                                                                  ExternalBallistics.BodyRadius, ExternalBallistics.BodyHeight, out _);
                halfWidth = MathF.Atan2(ExternalBallistics.BodyRadius, MathF.Max(0.5f, distance));
            }
            else
            {
                Vector3 box = size.X > 0.5f ? size : new Vector3(2f, 1.5f, 4.5f);
                onCrosshair = GeometryUtils.RayIntersectsOBB(eye, forward, e.Transform.Position, box, e.Transform.Rotation, out _);
                halfWidth = MathF.Atan2(0.5f * MathF.Min(box.X, box.Z), MathF.Max(0.5f, distance));
            }
            seen.Add(new Sighting(e.Id, kind, NameOf(e, kind), e.Transform.Position, aim, e.Velocity,
                                  distance, r, u, onCrosshair, halfWidth));
        }
        seen.Sort((a, b) => a.Right.CompareTo(b.Right));
        return seen;
    }

    /// <summary>The nearest thing the crosshair is on, if any.</summary>
    public static Sighting? OnCrosshair(IReadOnlyList<Sighting> seen)
    {
        Sighting? best = null;
        foreach (var s in seen)
            if (s.OnCrosshair && (best == null || s.Distance < best.Value.Distance)) best = s;
        return best;
    }

    /// <summary>"person, 340 metres, a little left and above, walking right".</summary>
    public static string TargetLine(in Sighting s, float fovDegrees, Vector3 lookForward)
        => $"{s.Name}, {ScopeMath.DistanceWords(s.Distance)}, "
         + $"{ScopeMath.RelativeWords(s.Right, s.Up, fovDegrees, s.HalfWidth)}, "
         + ScopeMath.MovementWords(s.Velocity, lookForward, !IsBody(s.Kind));

    /// <summary>How far the rangefinder reads, and how far the crosshair's surface is looked for.</summary>
    public const float RangefinderMetres = 2000f;

    /// <summary>
    /// Numpad 5: what is under the crosshair. Somebody or something on it first ("person, 340
    /// metres, on a roof 9 metres up, walking left"), else the surface it rests on ("Elm Street roof,
    /// concrete, 230 metres"), else "Nothing within 2000 metres".
    /// </summary>
    public static string Describe(WorldSnapshot snap, SpatialService spatial, IReadOnlyList<Sighting> seen,
                                  Vector3 eye, Vector3 forward, float ownFeetY)
    {
        bool surface = spatial.CastSight(snap, eye, forward, RangefinderMetres, out var hit, out float d, glassBlocks: true);
        if (OnCrosshair(seen) is { } s && (!surface || s.Distance <= d + 1f))
        {
            bool body = IsBody(s.Kind);
            float feetY = body ? s.Feet.Y : s.Feet.Y - 0.5f * MathF.Max(1f, snap.Entities.TryGetValue(s.Id, out var v) ? v.Definition.Collider.Size.Y : 1.5f);
            bool roof = body && StandsOnRoof(snap, spatial, s.Feet);
            return $"{s.Name}, {ScopeMath.DistanceWords(s.Distance)}, {ScopeMath.HeightWords(feetY - ownFeetY, roof)}, "
                 + $"{ScopeMath.MovementWords(s.Velocity, forward, !body)}.";
        }
        if (surface)
        {
            string name = hit.Definition.Identity.Name;
            if (string.IsNullOrWhiteSpace(name)) name = "something";
            string material = hit.Definition.Material.Material;
            string made = string.IsNullOrEmpty(material) || material == "Generic" ? "" : $", {material.ToLowerInvariant()}";
            return $"{name}{made}, {ScopeMath.DistanceWords(d)}.";
        }
        return $"Nothing within {RangefinderMetres:0} metres.";
    }

    /// <summary>Numpad period: the exact distance to what the crosshair is on, to the metre.</summary>
    public static string Range(WorldSnapshot snap, SpatialService spatial, IReadOnlyList<Sighting> seen, Vector3 eye, Vector3 forward)
    {
        bool surface = spatial.CastSight(snap, eye, forward, RangefinderMetres, out _, out float d, glassBlocks: true);
        float? reading = surface ? d : null;
        if (OnCrosshair(seen) is { } s && (reading == null || s.Distance < reading)) reading = s.Distance;
        return reading is { } m ? $"{ScopeMath.RangeWords(m)}." : "No reading.";
    }

    /// <summary>Whether somebody standing at <paramref name="feet"/> is on a roof: the thing under them
    /// is named as one. The city names every roof slab "... roof".</summary>
    public static bool StandsOnRoof(WorldSnapshot snap, SpatialService spatial, Vector3 feet)
        => spatial.CastSight(snap, feet + new Vector3(0f, 0.3f, 0f), -Vector3.UnitY, 1.5f, out var under, out _, glassBlocks: true)
           && (under.Definition.Identity.Name ?? "").Contains("roof", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the guidance tone should do: be silent, pulse at a closeness, or hold its note.</summary>
public readonly record struct Guidance(bool Sounding, bool OnTarget, float Closeness)
{
    public static readonly Guidance Silent = new(false, false, 0f);

    /// <summary>
    /// The tone for what is in view: silent with nothing, the held note when the crosshair is on a
    /// body, and otherwise pulses whose closeness is how near the crosshair is to the nearest thing,
    /// on a log scale from the edge of the view (0) to touching it (1). It knows nothing of drop, wind
    /// or lead; those are the shooter's.
    /// </summary>
    public static Guidance For(IReadOnlyList<Sighting> seen, float fovDegrees)
    {
        if (seen.Count == 0) return Silent;
        if (seen.Any(s => s.OnCrosshair)) return new Guidance(true, true, 1f);
        float half = 0.5f * fovDegrees * MathF.PI / 180f;
        float best = float.MaxValue, edge = 0f;
        foreach (var s in seen)
        {
            float a = MathF.Sqrt(s.Right * s.Right + s.Up * s.Up);
            if (a < best) { best = a; edge = s.HalfWidth; }
        }
        float on = MathF.Max(1e-5f, edge);
        float c = 1f - Math.Clamp(MathF.Log(MathF.Max(best, on) / on) / MathF.Log(MathF.Max(half, on * 1.01f) / on), 0f, 1f);
        return new Guidance(true, false, c);
    }
}

/// <summary>
/// The scope in the player's hands: up or down, its power, its turret, the sway, and which target was
/// last read out. Everything it says is returned as a sentence for the session to speak.
/// </summary>
public sealed class ScopeController
{
    public const string NumLockWarning = "Num Lock is off. The scope keys need it on.";

    public bool Raised { get; private set; }
    public ScopeDefinition? Scope { get; private set; }
    public WeaponDefinition? Weapon { get; private set; }
    private int _power;
    private int _clicks;

    /// <summary>The guidance tone, on unless turned off. Kept when the scope is lowered.</summary>
    public bool ToneOn { get; set; } = true;

    public ScopeSway Sway { get; } = new();
    /// <summary>The sway now, radians right and up.</summary>
    public (float Right, float Up) SwayNow { get; set; }

    /// <summary>The target read out last by 7 or 9, so the next press goes on from it.</summary>
    public int CurrentTargetId { get; private set; } = -1;

    public float Magnification => Scope?.Magnifications[_power] ?? 1f;
    public float FieldOfViewDegrees => Scope?.FieldOfViewDegrees(Magnification) ?? 60f;
    public int Clicks => _clicks;
    public float ElevationMil => Scope == null ? 0f : _clicks * Scope.ClickMil;

    /// <summary>Why the scope cannot come up, or null when it can.</summary>
    public static string? CannotRaise(string heldWeaponId, string heldScopeId, bool riding)
    {
        if (riding) return "Not from a seat.";
        if (string.IsNullOrEmpty(heldWeaponId)) return "You are not holding a rifle.";
        if (string.IsNullOrEmpty(heldScopeId) || !ScopeRegistry.TryGet(heldScopeId, out _))
            return WeaponRegistry.TryGet(heldWeaponId, out var w) ? $"The {w.DisplayName} has no scope." : "That has no scope.";
        return null;
    }

    /// <summary>Puts the scope to the eye. Says the power and the zero, and that Num Lock is off when it is.</summary>
    public string Raise(string weaponId, string scopeId, bool? numLockOn)
    {
        ScopeRegistry.TryGet(scopeId, out var scope);
        WeaponRegistry.TryGet(weaponId, out var weapon);
        if (Scope != scope || Weapon != weapon) { _power = 0; _clicks = 0; }
        Scope = scope;
        Weapon = weapon;
        Raised = true;
        Sway.Reset();
        CurrentTargetId = -1;
        string line = $"Scope up, {PowerWords()}, {ZeroWords()}.";
        return numLockOn == false ? $"{line} {NumLockWarning}" : line;
    }

    public string Lower()
    {
        Raised = false;
        Sway.Reset();
        SwayNow = (0f, 0f);
        return "Scope down.";
    }

    /// <summary>Lowers it without a word: the gun left the hands, or the player sat down.</summary>
    public void Drop() { Raised = false; Sway.Reset(); SwayNow = (0f, 0f); }

    private string PowerWords() => $"{Magnification:0.#} power";

    /// <summary>One stop of the zoom ring in or out.</summary>
    public string Zoom(int direction)
    {
        if (Scope == null) return "No scope.";
        int next = Math.Clamp(_power + Math.Sign(direction), 0, Scope.Magnifications.Length - 1);
        if (next == _power) return direction > 0 ? $"{PowerWords()}, the most it goes." : $"{PowerWords()}, the least it goes.";
        _power = next;
        return $"{PowerWords()}.";
    }

    /// <summary>The nearest stop to a power typed, "/zoom 12".</summary>
    public string ZoomTo(float power)
    {
        if (Scope == null) return "No scope.";
        int best = 0;
        for (int i = 1; i < Scope.Magnifications.Length; i++)
            if (MathF.Abs(Scope.Magnifications[i] - power) < MathF.Abs(Scope.Magnifications[best] - power)) best = i;
        _power = best;
        return $"{PowerWords()}.";
    }

    /// <summary>The turret's clicks the most it goes up.</summary>
    public int MaxClicks => Scope == null ? 0 : (int)MathF.Round(Scope.MaxElevationMil / Scope.ClickMil);

    /// <summary>One click of the elevation turret up (+1) or down (-1), said as the distance it is now
    /// zeroed for.</summary>
    public string Click(int direction)
    {
        if (Scope == null) return "No scope.";
        int next = Math.Clamp(_clicks + Math.Sign(direction), 0, MaxClicks);
        if (next == _clicks) return direction > 0 ? $"The turret is all the way up, {ZeroWords()}." : $"The turret is at its zero, {ZeroWords()}.";
        _clicks = next;
        return ZeroWords();
    }

    /// <summary>The turret set for a distance: the nearest click to the come-up it needs.</summary>
    public string ZeroFor(float metres)
    {
        if (Scope == null || Weapon == null) return "No scope.";
        float mil = ExternalBallistics.ComeUpMil(Weapon, MathF.Max(Scope.BaseZeroMetres, metres), Scope.BaseZeroMetres, Scope.SightHeightMetres);
        _clicks = Math.Clamp((int)MathF.Round(mil / Scope.ClickMil), 0, MaxClicks);
        return $"{Capital(ZeroWords())}, {_clicks} {(_clicks == 1 ? "click" : "clicks")} up.";
    }

    /// <summary>The distance the turret is zeroed for now.</summary>
    public float? ZeroMetres
    {
        get
        {
            if (Scope == null || Weapon == null) return null;
            if (_clicks == 0) return Scope.BaseZeroMetres;
            float angle = ExternalBallistics.ZeroAngle(Weapon, Scope.BaseZeroMetres, Scope.SightHeightMetres) + ElevationMil / 1000f;
            return ExternalBallistics.ZeroDistanceFor(Weapon, angle, Scope.SightHeightMetres);
        }
    }

    public string ZeroWords() => ScopeMath.ZeroWords(ZeroMetres);

    /// <summary>
    /// The next (+1) or previous (-1) thing in view, left to right, from the one read last. The aim
    /// does not move; this only says where things are.
    /// </summary>
    public string NextTarget(IReadOnlyList<Sighting> seen, int direction, Vector3 lookForward)
    {
        if (seen.Count == 0) { CurrentTargetId = -1; return "Nobody in view."; }
        int at = -1;
        for (int i = 0; i < seen.Count; i++) if (seen[i].Id == CurrentTargetId) at = i;
        int next = at < 0 ? (direction > 0 ? 0 : seen.Count - 1) : ((at + Math.Sign(direction)) % seen.Count + seen.Count) % seen.Count;
        var s = seen[next];
        CurrentTargetId = s.Id;
        string count = seen.Count == 1 ? "" : $" {next + 1} of {seen.Count}.";
        return ScopeView.TargetLine(s, FieldOfViewDegrees, lookForward) + "." + count;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
