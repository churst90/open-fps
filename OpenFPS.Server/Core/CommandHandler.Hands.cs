using System.Numerics;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using Arch.Core;

namespace OpenFPS.Server.Core;

/// <summary>
/// Carrying things: take, drop, stow, draw, hands and inv are the whole inventory. A carried thing is
/// the same entity it was on the floor, and the readout is a sentence, what a screen reader takes in.
/// </summary>
public partial class CommandHandler
{
    private HandsService? Hands(Action<IMessage> reply)
    {
        if (_hands == null) Say(reply, "Picking things up is not available on this server.");
        return _hands;
    }

    /// <summary>How near somebody must be to be handed something, metres: arm's length and a step.</summary>
    public const float HandMetres = 2.5f;

    /// <summary>/hand [THING]: gives what you hold to the character beside you who asks for things (Alex).</summary>
    private void HandleHand(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        Entity taker = Entity.Null; string name = ""; float best = HandMetres;
        world.Query(new QueryDescription().WithAll<Transform, OpenFPS.Server.Systems.Pedestrian>().WithNone<DeadComponent>(),
            (Entity e, ref Transform t, ref OpenFPS.Server.Systems.Pedestrian p) =>
            {
                if (string.IsNullOrEmpty(p.Character)) return;
                float d = Vector3.Distance(t.Position, position);
                if (d < best) { best = d; taker = e; name = p.Character; }
            });
        if (taker == Entity.Null) { Say(reply, "There is nobody beside you who would take it."); return; }
        if (!svc.HandOver(session, string.Join(" ", args), out string what, out string why)) { Say(reply, why); return; }
        _server.StreetSpeech.Given(session.CurrentMapId, taker.Id, session.Entity.Id);
        Say(reply, $"You hand {name} the {what}. He takes it.");
    }

    /// <summary>/take [name]: picks up the nearest thing you can reach, or the nearest one called that.</summary>
    private void HandleTake(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        svc.Take(session, string.Join(" ", args), out string message);
        Say(reply, message);
    }

    /// <summary>/drop [name|left|right|all]: puts something down; everyone in earshot hears it land.</summary>
    private void HandleDrop(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        svc.Drop(session, string.Join(" ", args), out string message,
                 (id, label, sounds) => _server.EmitWorldAudio(session.CurrentMapId, id, label, sounds));
        Say(reply, message);
    }

    /// <summary>/stow [name|left|right|all]: slings what you are holding onto your back.</summary>
    private void HandleStow(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        svc.Stow(session, string.Join(" ", args), out string message);
        Say(reply, message);
    }

    /// <summary>/draw [name]: takes something off your back and puts it in your hands.</summary>
    private void HandleDraw(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        svc.Draw(session, string.Join(" ", args), out string message);
        Say(reply, message);
    }

    /// <summary>/hands: what is in your hands.</summary>
    private void HandleHands(UserSession session, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out var world, out _, out _)) return;
        if (!_maps.TryGetMap(session.CurrentMapId, out _, out _, out _, out var lookup)) return;
        var hands = world.Has<HandsComponent>(session.Entity)
            ? world.Get<HandsComponent>(session.Entity) : new HandsComponent();
        Say(reply, HandsService.Carrying(world, lookup, hands));
    }

    /// <summary>
    /// /give [NAME] [ammo] KIND [COUNT]: spare rounds when the words name ammunition ("/give sean ammo
    /// 7.62 90"), a box when no count is given. False when they do not, and /give goes on to items.
    /// </summary>
    private bool TryGiveAmmo(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length == 0) return false;
        var receiver = session;
        if (!CombatService.TryParseAmmo(args, out var ammo, out int count))
        {
            if (args.Length < 2 || OnlineSession(args[0]) is not { } named
                || !CombatService.TryParseAmmo(args[1..], out ammo, out count)) return false;
            receiver = named;
        }
        _combat.GiveAmmo(session, receiver, ammo, count, out string message);
        Say(reply, message);
        return true;
    }

    /// <summary>/inv: everything you have on you, hands first, with what it weighs.</summary>
    private void HandleInventory(UserSession session, Action<IMessage> reply)
    {
        var svc = Hands(reply); if (svc == null) return;
        Say(reply, svc.Readout(session));
    }
}
