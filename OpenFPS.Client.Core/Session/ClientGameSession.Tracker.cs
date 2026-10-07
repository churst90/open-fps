using System.Numerics;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core.Session;

/// <summary>Comma and period: the things of one kind on the map, nearest first. See <see cref="MapTracker"/>.</summary>
public sealed partial class ClientGameSession
{
    private readonly MapTracker _tracker = new(NavigationAids.Track);

    /// <summary>Writes the category to the settings file. A test puts a no-op here, so pressing keys in a
    /// test never touches the player's own client.json.</summary>
    internal Action SaveTrackedCategory { get; set; } = SaveSettings;

    /// <summary>
    /// Comma and period step through the things in the category; with Shift they change the category,
    /// as Shift and the brackets change the chat ring. By KEY, not by character: Shift+comma is "&lt;"
    /// on a US layout and something else elsewhere, and both heads report the key (see GtkKeyMap).
    /// </summary>
    private void RegisterTrackerBindings()
    {
        _bindings.Bind(InputContext.Gameplay, GameKey.Comma, () => TrackStep(-1));
        _bindings.Bind(InputContext.Gameplay, GameKey.Period, () => TrackStep(1));
        _bindings.Bind(InputContext.Gameplay, GameKey.Comma, KeyModifiers.Shift, () => TrackCategoryStep(-1));
        _bindings.Bind(InputContext.Gameplay, GameKey.Period, KeyModifiers.Shift, () => TrackCategoryStep(1));
    }

    /// <summary>The tracker on the category the settings say, should /track or the file have changed it.</summary>
    private void SyncTrackCategory()
    {
        if (_tracker.Category != NavigationAids.Track) _tracker.SetCategory(NavigationAids.Track);
    }

    private void TrackCategoryStep(int direction)
    {
        SyncTrackCategory();
        string line = _tracker.CycleCategory(direction, _world.GetSnapshot(), _state.Position, _ownEntityId);
        NavigationAids.Track = _tracker.Category;
        SaveTrackedCategory();
        Say(line);
    }

    private void TrackStep(int direction)
    {
        SyncTrackCategory();
        var snapshot = _world.GetSnapshot();
        string line = _tracker.Step(direction, snapshot, _state.Position, _state.Yaw, _ownEntityId);
        Say(line);
        if (_tracker.Selected is { } picked)
        {
            Serilog.Log.Information("[TRACK] {Category} '{Line}' e{Id} at {Pos}", _tracker.Category, line, picked.Id, picked.At);
            _audioSystem.Beacons.Ping(snapshot, picked.Id, _tracker.Category,
                _state.Position + new Vector3(0f, _state.EyeHeight, 0f), _ownEntityId);
        }
    }

    /// <summary>/track, /track doors: which kind of thing comma and period step through.</summary>
    internal static string TrackCommand(string[] args, Action? save = null)
    {
        string all = string.Join(", ", Array.ConvertAll(MapTracker.Ring, c => MapTracker.NameOf(c).ToLowerInvariant()));
        if (args.Length > 0)
        {
            if (MapTracker.Parse(args[0]) is not { } c) return $"{args[0]} is not something to track. Say one of {all}.";
            NavigationAids.Track = c;
            (save ?? SaveSettings)();
        }
        return $"Comma and period step through {MapTracker.NameOf(NavigationAids.Track).ToLowerInvariant()}. "
             + $"Shift with either changes it, or say /track and one of {all}.";
    }
}
