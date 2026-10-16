namespace OpenFPS.Common;

/// <summary>
/// The fire selector: what its settings are called and how a step moves between them. The settings
/// themselves are on the weapon (<see cref="WeaponDefinition.Selector"/>), as everything about a gun is.
/// A step past the last setting comes round to the first, so a player never has to know which end the
/// lever is at to reach the one they want.
/// </summary>
public static class FireSelector
{
    public const string NoSelector = "This gun has no selector.";

    public static bool Has(WeaponDefinition w) => w.Selector.Length > 0;

    public static bool HasAuto(WeaponDefinition w) => Array.IndexOf(w.Selector, FireMode.Auto) >= 0;

    /// <summary>Where a gun sits that nobody has moved the lever on: ready to fire one at a time, as
    /// every gun here fired before it had a selector.</summary>
    public static FireMode Default(WeaponDefinition w) => FireMode.Semi;

    /// <summary>The setting one step on (<paramref name="direction"/> 1) or back (-1) from
    /// <paramref name="now"/>; a gun with no selector stays where it is.</summary>
    public static FireMode Step(WeaponDefinition w, FireMode now, int direction)
    {
        var s = w.Selector;
        if (s.Length == 0) return now;
        int i = Array.IndexOf(s, now);
        if (i < 0) i = Math.Max(0, Array.IndexOf(s, Default(w)));
        int n = s.Length;
        int step = direction < 0 ? -1 : 1;
        return s[((i + step) % n + n) % n];
    }

    /// <summary>What the setting is called on this gun: "safe", "semi", "auto", or "fire" for the
    /// second setting of a two-position safety.</summary>
    public static string Spoken(WeaponDefinition w, FireMode m) => m switch
    {
        FireMode.Safe => "safe",
        FireMode.Auto => "auto",
        _ => HasAuto(w) ? "semi" : "fire",
    };

    /// <summary>A setting this gun has, by the word for it, or false.</summary>
    public static bool TryParse(WeaponDefinition w, string? word, out FireMode mode)
    {
        mode = FireMode.Semi;
        switch ((word ?? "").Trim().ToLowerInvariant())
        {
            case "safe": case "safety": mode = FireMode.Safe; break;
            case "semi": case "single": case "fire": mode = FireMode.Semi; break;
            case "auto": case "full": case "automatic": mode = FireMode.Auto; break;
            default: return false;
        }
        return Array.IndexOf(w.Selector, mode) >= 0;
    }
}

/// <summary>
/// A kind of ammunition: what it is called when spoken, what a single one of it is called, and what
/// a box of it holds when somebody is handed one without a count.
/// </summary>
public sealed record AmmoType(string Id, string SpokenName, string Unit, string UnitSingular, int BoxRounds, string[] Aliases);

/// <summary>
/// The ammunition that exists, by the names a player might type for it. A reserve is kept per kind,
/// not per gun: two rifles in the same calibre draw on the same rounds, as they would from one belt.
/// </summary>
public static class Ammunition
{
    public static readonly AmmoType Rifle762 = new("7.62x39", "7.62x39", "rounds", "round", 30, new[] { "7.62x39", "7.62", "762", "7.62x39mm" });
    public static readonly AmmoType Rifle556 = new("5.56", "5.56", "rounds", "round", 30, new[] { "5.56", "556", "5.56x45", "5.56x45mm", ".223", "223" });
    public static readonly AmmoType Pistol9 = new("9mm", "9mm", "rounds", "round", 50, new[] { "9mm", "9x19", "9x19mm", "9" });
    public static readonly AmmoType Acp45 = new(".45", ".45 ACP", "rounds", "round", 50, new[] { ".45", "45", ".45acp", "45acp", ".45 acp" });
    public static readonly AmmoType Magnum357 = new(".357", ".357 Magnum", "rounds", "round", 50, new[] { ".357", "357", ".357magnum", "357magnum" });
    public static readonly AmmoType Gauge12 = new("12 gauge", "12 gauge", "shells", "shell", 25, new[] { "12 gauge", "12gauge", "12ga", "12g", "12", "buckshot", "shells" });

    /// <summary>.308 Winchester match, in boxes of twenty as match ammunition is sold.</summary>
    public static readonly AmmoType Rifle308 = new(".308", ".308", "rounds", "round", 20, new[] { ".308", "308", ".308win", "308win", ".308 win", "7.62x51", "7.62x51mm", "7.62 nato" });

    public static IReadOnlyList<AmmoType> All { get; } = new[] { Rifle762, Rifle556, Pistol9, Acp45, Magnum357, Gauge12, Rifle308 };

    /// <summary>The ammunition a name means, by its id or any of its aliases, ignoring case.</summary>
    public static bool TryFind(string? name, out AmmoType ammo)
    {
        ammo = null!;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.Trim();
        foreach (var a in All)
            if (a.Id.Equals(n, StringComparison.OrdinalIgnoreCase)
                || Array.Exists(a.Aliases, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)))
            { ammo = a; return true; }
        return false;
    }

    /// <summary>The ammunition by id, or null.</summary>
    public static AmmoType? Get(string id) => TryFind(id, out var a) ? a : null;

    /// <summary>"60 rounds of 9mm", "1 shell of 12 gauge".</summary>
    public static string Count(AmmoType ammo, int count)
        => $"{count} {(count == 1 ? ammo.UnitSingular : ammo.Unit)} of {ammo.SpokenName}";
}
