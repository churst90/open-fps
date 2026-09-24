using System;
using System.Collections.Generic;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core;

/// <summary>One thing on a list: what it says, and what choosing it does — act, or open another list.</summary>
public sealed record MenuItem(string Label, Action? Choose = null, Func<ListMenu>? Opens = null);

/// <summary>A titled list of things you can choose.</summary>
public sealed class ListMenu
{
    public string Title { get; }
    public IReadOnlyList<MenuItem> Items { get; }
    public int At { get; set; }

    public ListMenu(string title, IReadOnlyList<MenuItem> items)
    {
        Title = title;
        Items = items;
    }
}

/// <summary>
/// Lists you can arrow through in the game itself — players, maps, friends, and what you can do with
/// each — spoken, with the same interface sounds as the main menu.
///
/// Up and Down move, with a knock at either end. Enter or Right chooses: an action runs and the lists
/// close; a submenu opens on top. Escape, Left or Backspace goes back a level, and out of the last
/// one. A letter jumps to the next item starting with it. While a list is open it has the keyboard:
/// you stand still and nothing else fires.
/// </summary>
public sealed class MenuStack
{
    private readonly Stack<ListMenu> _open = new();
    private readonly ISpeechOutput _speech;
    private readonly UiSounds? _ui;

    public MenuStack(ISpeechOutput speech, UiSounds? ui)
    {
        _speech = speech;
        _ui = ui;
    }

    public bool IsOpen => _open.Count > 0;
    public ListMenu? Current => _open.Count > 0 ? _open.Peek() : null;

    /// <summary>Opens a list as the only one — what an F key does.</summary>
    public void Show(ListMenu menu)
    {
        _open.Clear();
        Push(menu);
    }

    private void Push(ListMenu menu)
    {
        _open.Push(menu);
        menu.At = 0;
        _ui?.Play(UiCue.MenuSelect);
        if (menu.Items.Count == 0) _speech.Speak($"{menu.Title}. Empty.", interrupt: true);
        else _speech.Speak($"{menu.Title}, {menu.Items.Count} item{(menu.Items.Count == 1 ? "" : "s")}. {menu.Items[0].Label}", interrupt: true);
    }

    public void Close()
    {
        if (_open.Count == 0) return;
        _open.Clear();
        _ui?.Play(UiCue.MenuBack);
        _speech.Speak("Closed.", interrupt: true);
    }

    /// <summary>Handles one key while a list is open. True if it was the menu's.</summary>
    public bool HandleKey(GameKey key)
    {
        if (_open.Count == 0) return false;
        var menu = _open.Peek();
        switch (key)
        {
            case GameKey.Up: Move(menu, -1); return true;
            case GameKey.Down: Move(menu, 1); return true;
            case GameKey.Enter:
            case GameKey.Right:
                Choose(menu);
                return true;
            case GameKey.Escape:
            case GameKey.Left:
            case GameKey.Backspace:
                Back();
                return true;
            default:
                if (key >= GameKey.A && key <= GameKey.Z) { Jump(menu, (char)('a' + (key - GameKey.A))); return true; }
                // Everything else is swallowed: an open list is modal. (F keys are let through by the
                // session so one list can be swapped for another.)
                return true;
        }
    }

    private void Move(ListMenu menu, int by)
    {
        if (menu.Items.Count == 0) { _ui?.Play(UiCue.MenuEdge); return; }
        int to = menu.At + by;
        if (to < 0 || to >= menu.Items.Count)
        {
            _ui?.Play(UiCue.MenuEdge);
            _speech.Speak(menu.Items[menu.At].Label, interrupt: true);
            return;
        }
        menu.At = to;
        _ui?.Play(UiCue.MenuMove);
        _speech.Speak(menu.Items[to].Label, interrupt: true);
    }

    private void Jump(ListMenu menu, char letter)
    {
        for (int k = 1; k <= menu.Items.Count; k++)
        {
            int i = (menu.At + k) % menu.Items.Count;
            string label = menu.Items[i].Label;
            if (label.Length > 0 && char.ToLowerInvariant(label[0]) == letter)
            {
                menu.At = i;
                _ui?.Play(UiCue.MenuMove);
                _speech.Speak(label, interrupt: true);
                return;
            }
        }
        _ui?.Play(UiCue.MenuEdge);
    }

    private void Choose(ListMenu menu)
    {
        if (menu.Items.Count == 0) return;
        var item = menu.Items[menu.At];
        if (item.Opens != null) { Push(item.Opens()); return; }
        _open.Clear();
        _ui?.Play(UiCue.MenuSelect);
        item.Choose?.Invoke();
    }

    private void Back()
    {
        _open.Pop();
        _ui?.Play(UiCue.MenuBack);
        if (_open.Count == 0) { _speech.Speak("Closed.", interrupt: true); return; }
        var menu = _open.Peek();
        _speech.Speak($"{menu.Title}. {menu.Items[menu.At].Label}", interrupt: true);
    }
}
