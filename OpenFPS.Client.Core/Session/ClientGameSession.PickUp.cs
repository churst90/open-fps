using System.Collections.Generic;

namespace OpenFPS.Client.Core.Session;

/// <summary>E and the things on the ground: which one it picks up. See <see cref="PickUp"/>.</summary>
public sealed partial class ClientGameSession
{
    /// <summary>The open list, for tests that choose from it.</summary>
    internal MenuStack Menus => _menus;

    /// <summary>
    /// Picks up the thing E means, by its own number so the server takes that one and not whichever it
    /// finds nearest: the one picked out with comma or period, the nearest in front, or, between two or
    /// more with none in front, the one chosen from a list. False when nothing is within reach, so E
    /// goes on to doors and vehicles.
    /// </summary>
    private bool TryPickUp()
    {
        var loose = PickUp.InReach(_world.GetSnapshot(), _state.Position, _state.Yaw, _ownEntityId);
        var choice = PickUp.Decide(loose, _tracker.Selected?.Id);
        if (choice.Take is int id)
        {
            Command("take", "#" + id);
            return true;
        }
        if (choice.Choose is { } options)
        {
            _menus.Show(PickUpMenu(options));
            return true;
        }
        return false;
    }

    /// <summary>"Take, 2 items. AKM, 1 metre, left": each takes that one.</summary>
    private ListMenu PickUpMenu(IReadOnlyList<PickUp.Loose> options)
    {
        var items = new List<MenuItem>();
        foreach (var l in options)
        {
            string id = "#" + l.Id;
            items.Add(new MenuItem(l.Label, () => Command("take", id)));
        }
        return new ListMenu("Take", items);
    }
}
