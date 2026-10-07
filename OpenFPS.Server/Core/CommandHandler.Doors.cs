using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;
using Arch.Core;

namespace OpenFPS.Server.Core;

/// <summary>Doors: opening, shutting, knocking and listing them.</summary>
public partial class CommandHandler
{
    /// <summary>How far away a door can be and still be reached: the same range as any other interaction.</summary>
    private const float DoorReach = PhysicsConstants.InteractionRange;

    /// <summary>
    /// /knock [name]: three knuckles on the nearest door, heard on either side of it through the door's
    /// own transmission. Nothing is said when it works: the knock is the answer.
    /// </summary>
    private void HandleKnock(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        var from = ReachingFrom(world, session.Entity, position);
        string wanted = args.Length > 0 ? string.Join(" ", args) : "";
        var door = NearestDoor(world, from, wanted, out _, out string name);
        if (door == null)
        {
            var anywhere = NearestDoor(world, from, wanted, out float away, out string itsName, reach: 40f);
            Say(reply, anywhere != null ? $"The nearest {itsName} is {away:F1} metres away. Get closer."
                                        : "There is no door near you to knock on.");
            return;
        }
        var at = world.Get<Transform>(door.Value).Position;
        // On the face you are standing at, at knuckle height.
        var toYou = new Vector3(from.X - at.X, 0f, from.Z - at.Z);
        var face = toYou.LengthSquared() > 1e-6f ? Vector3.Normalize(toYou) * 0.08f : Vector3.Zero;
        _server.EmitWorldAudio(session.CurrentMapId, session.Entity.Id, "knock", new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = new Vector3(at.X, at.Y + 0.3f, at.Z) + face,   // the door's centre is 1.05 m up
                LevelDb = DoorKnock.LevelDb,
                SynthKey = DoorKnock.Key(3),
                DecaySeconds = 0.9f,
                Noisiness = 1f,
            },
        });
    }

    /// <summary>
    /// /open and /close [name]: the door you meant, nearly always the nearest. From a seat it is the door
    /// nearest your seat, so a two-door, a four-door and a bus's middle door need no table.
    /// </summary>
    private void HandleDoor(UserSession session, string[] args, Action<IMessage> reply, bool open)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;

        var from = ReachingFrom(world, session.Entity, position);
        string wanted = args.Length > 0 ? string.Join(" ", args) : "";

        var door = NearestDoor(world, from, wanted, out float distance, out string name);
        if (door == null)
        {
            // Say how far the nearest one is, or this contradicts /doors reporting one at five and a half.
            var anywhere = NearestDoor(world, from, wanted, out float away, out string itsName, reach: 40f);
            Say(reply, anywhere != null
                ? $"The nearest {itsName} is {away:F1} metres away. Get closer."
                : string.IsNullOrEmpty(wanted)
                    ? "There is no door anywhere near you."
                    : $"There is no door called '{wanted}' near you.");
            return;
        }

        var state = world.Get<DoorComponent>(door.Value);
        if (!DoorSystem.OpensByHand(state))
        {
            Say(reply, state.SensorMetres > 0f ? $"The {name} opens by itself when someone comes up to it."
                                               : $"The {name} is moved by its motor, not by hand.");
            return;
        }
        if (!open && DoorSystem.InTheWay(world, door.Value, 0f, session.Entity) is { } blocker)
        {
            Say(reply, DoorSystem.InTheWayLine(blocker, session.Entity, name));
            return;
        }
        if (!DoorSystem.Set(world, door.Value, open, by: from, who: session.Entity))
        {
            Say(reply, state.Openness >= 1f ? $"The {name} is already open."
                     : state.Openness <= 0f ? $"The {name} is already shut."
                     : $"The {name} is already moving.");
            return;
        }
        var moved = world.Get<DoorComponent>(door.Value);
        Say(reply, open
            ? $"{DoorSystem.OpenedPhrase(moved, name)}, {distance:F1} metres away."
              + (DoorSystem.InTheWay(world, door.Value, 1f, session.Entity) != null ? " Someone is in the way of it." : "")
            : $"The {name} {DoorSystem.Verb(moved)} shut, {distance:F1} metres away.");
    }

    /// <summary>/doors: the doors within twenty metres, how far, which way, and whether they are open.</summary>
    private void HandleListDoors(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        var from = ReachingFrom(world, session.Entity, position);

        var found = new List<(string Name, float Distance, float Openness, string Direction)>();
        var rotation = world.Get<Transform>(session.Entity).Rotation;
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent d) =>
        {
            float distance = Vector3.Distance(from, t.Position);
            if (distance > 20f) return;
            var offset = t.Position - from;
            found.Add((DoorName(world, e), distance, d.Openness,
                       offset.LengthSquared() > 0.01f ? GetRelativeDirection(rotation, Vector3.Normalize(offset)) : "right here"));
        });

        if (found.Count == 0) { Say(reply, "No doors within twenty metres."); return; }
        foreach (var d in found.OrderBy(x => x.Distance))
            Say(reply, $"  {d.Name}: {(d.Openness >= 1f ? "open" : d.Openness <= 0f ? "shut" : $"{d.Openness * 100f:F0} per cent open")}, "
                     + $"{d.Direction}, {d.Distance:F1} metres.");
    }

    /// <summary>
    /// Where a player reaches from: their seat if they are in one, otherwise their feet. In a bus the door
    /// beside you and the door nearest the middle are different doors.
    /// </summary>
    private static Vector3 ReachingFrom(World world, Entity player, Vector3 position)
        => world.Has<OccupantComponent>(player) && world.Has<Transform>(player)
            ? world.Get<Transform>(player).Position
            : position;

    private static Entity? NearestDoor(World world, Vector3 from, string named,
                                       out float distance, out string name, float? reach = null)
    {
        Entity? best = null;
        float bestDistance = reach ?? (string.IsNullOrEmpty(named) ? DoorReach : 20f);
        string bestName = "door";
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent _) =>
        {
            string thisName = DoorName(world, e);
            if (!string.IsNullOrEmpty(named)
                && thisName.IndexOf(named, StringComparison.OrdinalIgnoreCase) < 0) return;
            float d = Vector3.Distance(from, t.Position);
            if (d > bestDistance) return;
            bestDistance = d; best = e; bestName = thisName;
        });
        distance = bestDistance; name = bestName;
        return best;
    }

    private static string DoorName(World world, Entity e)
        => world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<NameComponent>(e).Name)
                ? world.Get<NameComponent>(e).Name
                : "door";
}
