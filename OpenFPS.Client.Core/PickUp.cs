using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// Which of the things on the ground E picks up.
///
/// "If two items are on the ground next to each other, like 2 guns on the ground, what determines
/// which one is picked up? ... whatever is tracked gets picked up?" (Cody, 2026-10-04). It used to be
/// whichever was nearest your body, whatever way you faced, and nothing said there was another. Now,
/// in order: the thing you picked out with comma or period, if it is in reach; else the nearest one
/// in front of you (within <see cref="FrontDegrees"/> of the way you face); else, with two or more in
/// reach and none in front, a list to choose from, since a guess between them is a coin toss; else the
/// one there is. With nothing in reach, E is the door and vehicle key it always was.
/// </summary>
public static class PickUp
{
    /// <summary>How far E reaches for something on the ground (the server's figure too).</summary>
    public const float Reach = PhysicsConstants.PickUpReach;

    /// <summary>Either side of straight ahead that counts as in front of you, degrees.</summary>
    public const float FrontDegrees = 45f;

    /// <summary>Something loose within reach: what it is called, how far, and which way.</summary>
    public readonly record struct Loose(int Id, string Name, float Distance, string Direction, bool InFront)
    {
        /// <summary>"AKM, 1 metre, in front", "Glock 17, right here".</summary>
        public string Label => Direction == "right here" ? $"{Name}, right here" : $"{Name}, {Sightline.SpokenDistance(Distance)}, {Direction}";
    }

    /// <summary>What E does about the things on the ground.</summary>
    public readonly record struct Decision(int? Take, IReadOnlyList<Loose>? Choose)
    {
        public bool Nothing => Take == null && Choose == null;
    }

    /// <summary>Every item lying within reach of your feet, nearest first. Something somebody is
    /// carrying is not an item beacon any more (the server takes it off), so it is not here.</summary>
    public static List<Loose> InReach(WorldSnapshot world, Vector3 feet, float yaw, int selfId)
    {
        var found = new List<Loose>();
        foreach (var e in world.Entities.Values)
        {
            if (e.Id == selfId) continue;
            if (!string.Equals(e.Definition.Identity.BeaconCategory, Beacons.Item, StringComparison.OrdinalIgnoreCase)) continue;
            var at = e.Transform.Position;
            if (Vector3.Distance(feet, at) > Reach) continue;
            var flat = new Vector3(at.X - feet.X, 0f, at.Z - feet.Z);
            float across = flat.Length();
            bool underfoot = across < MapTracker.HereMetres;
            string direction = underfoot ? "right here" : DirectionWords.Relative(yaw, flat);
            bool inFront = underfoot || AngleOff(yaw, flat) <= FrontDegrees;
            found.Add(new Loose(e.Id, Sightline.NameOf(e), across, direction, inFront));
        }
        found.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Id.CompareTo(b.Id));
        return found;
    }

    /// <summary>
    /// The choice, from what is in reach and what comma or period last picked out (if anything):
    /// the picked thing, the nearest in front, a list, or the only one.
    /// </summary>
    public static Decision Decide(IReadOnlyList<Loose> inReach, int? selectedId)
    {
        if (inReach.Count == 0) return new Decision(null, null);
        if (selectedId is { } picked)
            foreach (var l in inReach)
                if (l.Id == picked) return new Decision(l.Id, null);
        foreach (var l in inReach)                 // nearest first, so the first in front is the nearest
            if (l.InFront) return new Decision(l.Id, null);
        if (inReach.Count >= 2) return new Decision(null, inReach);
        return new Decision(inReach[0].Id, null);
    }

    /// <summary>How far round from straight ahead a level direction is, degrees, 0 to 180.</summary>
    private static float AngleOff(float yaw, Vector3 flat)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f));
        float cos = Vector3.Dot(Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z)), Vector3.Normalize(flat));
        return MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;
    }
}
