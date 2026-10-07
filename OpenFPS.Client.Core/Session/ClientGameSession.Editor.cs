using System;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core.Session;

/// <summary>
/// The world editor on the client (docs/WORLD_EDITOR.md): F12 asks the server for its menu, and every
/// menu the server sends is shown as a list on the MenuStack, like F5, F6 and F8. The client knows no
/// kind of thing and no field: it shows what it is sent, and sends back what each item says to send.
/// </summary>
public partial class ClientGameSession
{
    /// <summary>The tag every editor list carries, so one sent again replaces itself.</summary>
    internal const string EditorTagPrefix = "editor:";

    private void OpenWorldEditor() => Command("edit", "menu");

    /// <summary>
    /// An editor menu from the server. The first menu replaces whatever lists are open; any other opens
    /// on top of an open editor menu, so Escape comes back; a refresh replaces the same menu where it
    /// stands, silently, and is dropped if that menu is not the one open.
    /// </summary>
    internal void ShowEditorMenu(EditorMenu menu)
    {
        var list = ToList(menu);
        if (menu.Refresh) { _menus.Replace(list); return; }
        bool editorOpen = _menus.Current?.Tag.StartsWith(EditorTagPrefix, StringComparison.Ordinal) == true;
        if (menu.Path == "root" || !editorOpen) _menus.Show(list);
        else _menus.Open(list);
    }

    private ListMenu ToList(EditorMenu menu)
    {
        var items = menu.Items.Select(item => item.Kind switch
        {
            // The answer opens on top, so the lists stay open while it is asked for.
            EditorItemKind.Menu => new MenuItem(item.Label, () => Command("edit", ("menu " + item.Command).Split(' ', StringSplitOptions.RemoveEmptyEntries)), Stays: true),
            EditorItemKind.Action => new MenuItem(item.Label, () => SendTyped(item.Command), Stays: item.Stay),
            // The command line, with the start of the command typed; the lists wait underneath.
            EditorItemKind.Input => new MenuItem(item.Label, () => _shell.OpenCommandConsole(item.Command), Stays: true),
            _ => new MenuItem(item.Label, () => _speech.Speak(item.Label, interrupt: true), Stays: true),
        }).ToList();
        return new ListMenu(menu.Title, items) { Tag = EditorTagPrefix + menu.Path };
    }

    /// <summary>Sends "edit nudge north" as the command /edit nudge north.</summary>
    private void SendTyped(string text)
    {
        var words = text.TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        Command(words[0], words[1..]);
    }

    /// <summary>A model changed in the world editor: into this client's library, and its voices started again.</summary>
    internal void ApplyModelUpdate(ModelUpdate model)
    {
        if (!ModelLibrary.AddJson(model.Kind, model.Id, model.SpecJson))
        {
            Serilog.Log.Warning("ModelUpdate: {Kind} {Id} version {Version} did not read as a model; kept the one we had.", model.Kind, model.Id, model.Version);
            return;
        }
        var snapshot = _world.GetSnapshot();
        var restarted = _audioSystem.ModelChanged(model.Kind, model.Id, snapshot);
        // An engine is heard through what it is in: every vehicle and machine built on it starts again.
        if (model.Kind == ModelLibrary.Kinds.Engine)
            foreach (var (kind, id) in UsersOfEngine(model.Id, snapshot).ToList())
                restarted.AddRange(_audioSystem.ModelChanged(kind, id, snapshot));
        Serilog.Log.Information("ModelUpdate: {Kind} {Id} is version {Version}; {Count} voice(s) started again.",
            model.Kind, model.Id, model.Version, restarted.Count);
    }

    /// <summary>The vehicles and small machines heard here that are built on an engine.</summary>
    private static IEnumerable<(string Kind, string Id)> UsersOfEngine(string engine, WorldSnapshot snapshot)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var (_, entity) in snapshot.Entities)
        {
            if (!OpenFPS.Common.Editing.ModelKinds.TryModelOfSound(entity.Definition?.SoundEmitter.SoundId, out var kind, out var id)) continue;
            if (!seen.Add((kind, id))) continue;
            string built = kind == ModelLibrary.Kinds.Vehicle ? MachineRegistry.EngineKeyFor(id)
                         : kind == ModelLibrary.Kinds.SmallMachine ? ModelLibrary.SmallMachine(id).EngineKey ?? ""
                         : "";
            if (built.Equals(engine, StringComparison.OrdinalIgnoreCase)) yield return (kind, id);
        }
    }

    /// <summary>/editorkeys [on|off]: the world editor's direct keys, saved. Off by default.</summary>
    internal static string EditorKeysCommand(string[] args, Action? save = null)
    {
        string word = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (word is not ("on" or "off"))
            return $"The editor's direct keys are {(EditorKeys.Enabled ? "on" : "off")}. /editorkeys on or off. With an editor list open: {EditorKeys.Said}";
        EditorKeys.Enabled = word == "on";
        (save ?? SaveSettings)();
        return EditorKeys.Enabled
            ? "Editor direct keys on, while an editor list is open: " + EditorKeys.Said + " They are new: say if a screen reader takes any of them."
            : "Editor direct keys off.";
    }

    /// <summary>A map's settings changed while we are on it: its beacon rules, at once.</summary>
    internal void ApplyMapSettings(MapSettingsUpdate update)
    {
        _audioSystem.Beacons.SetMapPolicy(update.BeaconPolicy);
        Serilog.Log.Information("MapSettingsUpdate: {Map} beacon rules {Rules}.", update.MapId, string.Join(", ", update.BeaconPolicy));
    }
}
