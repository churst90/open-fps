using OpenFPS.Acoustics;
using OpenFPS.Models;

namespace OpenFPS.Client.Core;

/// <summary>What a scope can pick out: somebody, or something with an engine.</summary>
public enum SightKind { Person, Player, Vehicle, Aircraft, Train }

/// <summary>
/// What the scope's categories call a body: the engine's table (<see cref="ModelKindTable"/>) and the
/// body's kind say it. The game's scope and the sight aids ask the same function, so one policy answers
/// both (stage 9, docs/ENGINE_PACKAGES.md).
/// </summary>
public static class SightKinds
{
    /// <summary>What a body is, to a scope, or null for anything it does not pick out.</summary>
    public static SightKind? Classify(in AcousticBody body)
    {
        var model = ModelKindTable.BodyOf(body.SoundId);
        if (model == ModelBody.Rolls) return SightKind.Vehicle;
        if (model == ModelBody.Flies) return SightKind.Aircraft;
        if (model == ModelBody.Rails) return SightKind.Train;
        if (body.Kind == BodyKind.Player) return SightKind.Player;
        // A walker is an NPC with no model in it. A machine is not picked out, the push mower included,
        // though its body walks (docs/SPECIAL_CASES_AUDIT.md, P2: the open choice).
        if (body.Kind == BodyKind.Npc && model == null) return SightKind.Person;
        return null;
    }

    /// <summary>The words for what something is: "person", a player's name, a vehicle's name.</summary>
    public static string NameOf(in AcousticBody body, SightKind kind)
    {
        string name = body.Name ?? "";
        return kind switch
        {
            // A person with a name of their own is called by it, as a player is (IdentityComponent.Named).
            SightKind.Person => body.Named && !string.IsNullOrWhiteSpace(name) ? name.Trim() : "person",
            SightKind.Player => string.IsNullOrWhiteSpace(name) ? "a player" : name,
            SightKind.Aircraft => string.IsNullOrWhiteSpace(name) ? "aircraft" : name,
            SightKind.Train => "train",
            _ => string.IsNullOrWhiteSpace(name) ? "vehicle" : name,
        };
    }
}
