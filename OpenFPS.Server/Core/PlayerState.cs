using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// What the server keeps about a player between visits, beside what they carry: where they were on
/// each map, how hurt they were, and their stats. Stored as JSON in the Users table (PlayerState), so a
/// stat or a field added later needs no change to the table: a new key is read as missing by an older
/// record and kept by a newer one. Anything a newer server wrote that this one does not know is kept
/// too (<see cref="Unknown"/>), so stepping a server back a version does not wipe it.
/// </summary>
public sealed class PlayerState
{
    /// <summary>The map they were on when they last left the world. They land there next time.</summary>
    public string? Map { get; set; }

    /// <summary>Where they were on each map they have left, by map id (lower case). Kept per map, so
    /// travelling to a map and back puts them where they were on it.</summary>
    public Dictionary<string, SavedPlace> Places { get; set; } = new();

    /// <summary>Health when they left, and its most. Null for whole (or for somebody who left dead,
    /// who comes back at the spawn and up whole once the wait is over).</summary>
    public int? Health { get; set; }
    public int? MaxHealth { get; set; }

    /// <summary>For somebody who left dead, when they may get up again; null otherwise. Coming back
    /// before then, they are still dead until it.</summary>
    public DateTime? DeadUntilUtc { get; set; }

    /// <summary>
    /// Numbers that belong to the player and not the body: experience, level, kills, deaths, headshots,
    /// longest shot, lives. None is counted yet. A stat is a name and a number, so adding one is a line
    /// where it is counted and nothing here.
    /// </summary>
    public Dictionary<string, double> Stats { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    public SavedPlace? PlaceOn(string mapId)
        => Places.TryGetValue(mapId.ToLowerInvariant(), out var place) ? place : null;

    public void SetPlace(string mapId, SavedPlace? place)
    {
        if (place == null) Places.Remove(mapId.ToLowerInvariant());
        else Places[mapId.ToLowerInvariant()] = place;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A stored record, or a new one for none or for one that cannot be read (said in the log,
    /// and not fatal: a broken record costs a player their place, not their login).</summary>
    public static PlayerState Parse(string? json, string username = "")
    {
        if (string.IsNullOrWhiteSpace(json)) return new PlayerState();
        try { return JsonSerializer.Deserialize<PlayerState>(json, Json) ?? new PlayerState(); }
        catch (JsonException ex)
        {
            Log.Warning(ex, "The saved state for {User} could not be read; starting them afresh.", username);
            return new PlayerState();
        }
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>A place on a map: engine coordinates of the feet, and which way they faced (radians, the
/// body's own yaw).</summary>
public sealed class SavedPlace
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
}

/// <summary>
/// What a player had on them when they left the world: each thing by the prefab it is made from, where
/// it was (a hand, both hands, the back), and for a gun what was in it; and the spare rounds by
/// ammunition id. The things themselves are destroyed when this is stored and made again from their
/// prefabs when it is taken, so a thing is in the store or in the world and never both.
/// </summary>
public sealed class Belongings
{
    public List<SavedItem> Items { get; set; } = new();
    public Dictionary<string, int> Spares { get; set; } = new();

    [JsonIgnore] public bool IsEmpty => Items.Count == 0 && Spares.Count == 0;

    public string ToJson() => JsonSerializer.Serialize(this, PlayerState.Json);

    public static Belongings? Parse(string? json, string username = "")
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Belongings>(json, PlayerState.Json); }
        catch (JsonException ex)
        {
            Log.Error(ex, "What {User} was carrying could not be read back: {Json}", username, json);
            return null;
        }
    }
}

/// <summary>One carried thing.</summary>
public sealed class SavedItem
{
    /// <summary>The prefab it is made from (IdentityComponent.PrefabId).</summary>
    public string Prefab { get; set; } = "";

    /// <summary>"right", "left", "both" or "back".</summary>
    public string Place { get; set; } = SavedItem.Back;

    /// <summary>For anything with rounds in it (AmmoComponent): what was in it, what it holds, and any
    /// spare rounds still lying with it. Null for a thing that has none.</summary>
    public int? Rounds { get; set; }
    public int? Capacity { get; set; }
    public int? SpareRounds { get; set; }

    public const string Right = "right", Left = "left", BothHands = "both", Back = "back";
}
