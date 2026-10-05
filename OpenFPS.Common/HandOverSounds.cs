using System;
using System.Numerics;
using static OpenFPS.Common.DesignedSoundKit;

namespace OpenFPS.Common;

/// <summary>
/// What a thing sounds like as it is put into somebody's hands, played at the receiver when they are
/// given it ("give:akm_rifle"). Each is the thing's own: a gun has its action worked once, as anybody
/// handed a gun checks it (<see cref="WeaponHandling.ActionKey"/>); a sword is drawn a hand's width
/// from its scabbard and let back; the teleporter gives its ready tone; a crowbar or a torch is a hand
/// closing on it and a knock of steel or a switch's click; anything else a soft brush of the hand.
/// </summary>
public static class HandOverSounds
{
    public const string Prefix = "give:";

    public static string Key(string prefabId) => Prefix + prefabId;

    public static bool TryParseKey(string? key, out string prefabId)
    {
        prefabId = "";
        if (string.IsNullOrEmpty(key) || !key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        prefabId = key[Prefix.Length..];
        return prefabId.Length > 0;
    }

    /// <summary>What kind of hand-over a prefab gets.</summary>
    public enum Kind { Gun, Sword, Teleporter, Crowbar, Torch, Soft }

    /// <summary>The kind, from the prefab id, and for a gun the weapon whose action it is. A prefab
    /// names its weapon first (akm_rifle, glock_pistol, m700_rifle); the admin gun is checked like an
    /// AR-15, whose receiver it borrows.</summary>
    public static Kind KindOf(string prefabId, out WeaponDefinition? weapon)
    {
        weapon = null;
        string p = (prefabId ?? "").Trim().ToLowerInvariant();
        if (p == AdminGun.PrefabId) { weapon = WeaponRegistry.Ar15; return Kind.Gun; }
        foreach (var w in WeaponRegistry.All)
            if (p == w.Id || p.StartsWith(w.Id + "_", StringComparison.Ordinal)) { weapon = w; return Kind.Gun; }
        if (p.Contains("teleport")) return Kind.Teleporter;
        if (p.Contains("sword") || p.Contains("blade") || p.Contains("sabre") || p.Contains("saber")) return Kind.Sword;
        if (p.Contains("crowbar") || p.Contains("wrench") || p.Contains("pipe")) return Kind.Crowbar;
        if (p.Contains("torch") || p.Contains("flashlight") || p.Contains("lamp")) return Kind.Torch;
        if (p.Contains("rifle")) { weapon = WeaponRegistry.Ar15; return Kind.Gun; }
        if (p.Contains("pistol") || p.Contains("handgun")) { weapon = WeaponRegistry.Glock; return Kind.Gun; }
        if (p.Contains("shotgun")) { weapon = WeaponRegistry.Shotgun; return Kind.Gun; }
        return Kind.Soft;
    }

    /// <summary>Peak dB SPL at a metre.</summary>
    public static float LevelDb(string prefabId) => KindOf(prefabId, out var w) switch
    {
        Kind.Gun => WeaponHandling.LevelDb(new HandlingSpec(w!.Id, false, 0, false, IsAction: true)),
        Kind.Sword => 64f,
        Kind.Teleporter => TeleporterSounds.LevelDb(TeleporterSounds.Ready),
        Kind.Crowbar => 66f,
        Kind.Torch => 58f,
        _ => 50f,
    };

    public static float Seconds(string prefabId) => KindOf(prefabId, out var w) switch
    {
        Kind.Gun => WeaponHandling.Seconds(new HandlingSpec(w!.Id, false, 0, false, IsAction: true)) + 0.25f,
        Kind.Sword => 0.75f,
        Kind.Teleporter => TeleporterSounds.Seconds(TeleporterSounds.Ready),
        _ => 0.45f,
    };

    /// <summary>The hand-over at the receiver's hands, following them.</summary>
    public static TransientSound Sound(string prefabId, Vector3 feet) => new()
    {
        Character = SoundCharacter.Knock,
        Position = feet + new Vector3(0f, 1.2f, 0.3f),
        OnBody = true,
        BodyOffset = new Vector3(0f, 1.2f, 0.3f),
        LevelDb = LevelDb(prefabId),
        SynthKey = Key(prefabId),
        DecaySeconds = Seconds(prefabId) + 0.1f,
        Noisiness = 1f,
    };

    public static float[] Render(string prefabId, int sr, int seed)
    {
        switch (KindOf(prefabId, out var w))
        {
            case Kind.Gun: return WeaponHandling.Render(new HandlingSpec(w!.Id, false, 0, false, IsAction: true), sr, seed);
            case Kind.Teleporter: return TeleporterSounds.RenderReady(sr);
            case Kind.Sword: return RenderSword(sr, seed);
            case Kind.Crowbar: return RenderCrowbar(sr, seed);
            case Kind.Torch: return RenderTorch(sr, seed);
            default: return RenderSoft(sr, seed);
        }
    }

    /// <summary>
    /// A sword drawn a hand's width from its scabbard and let back: steel drawn through the leather
    /// throat (friction, stick and slip every few milliseconds, bright), the blade singing faintly in
    /// its own modes as it is rubbed, and seated again with a soft knock of the guard on the locket.
    /// </summary>
    public static float[] RenderSword(int sr, int seed)
    {
        var rng = new Random(seed);
        var y = Buffer(0.75f, sr);
        void Scrape(float at, float seconds, float amp)
        {
            var hp = Biquad.HighPass(1800f, 0.7f, sr);
            var lp = Biquad.LowPass(9000f, 0.7f, sr);
            int a = (int)(at * sr), n = (int)(seconds * sr);
            int grain = Math.Max(1, (int)(0.004f * sr));
            float g0 = 0.5f, g1 = 0.5f;
            for (int i = 0; i < n && a + i < y.Length; i++)
            {
                if (i % grain == 0) { g0 = g1; g1 = 0.3f + 0.7f * (float)rng.NextDouble(); }
                float g = g0 + (g1 - g0) * (i % grain) / grain;
                float x = i / (float)n;
                float stroke = MathF.Sin(MathF.PI * MathF.Min(1f, x * 1.1f));
                y[a + i] += amp * stroke * g * lp.Run(hp.Run(White(rng)));
            }
        }
        Scrape(0.02f, 0.22f, 0.5f);
        // The blade's modes, rubbed rather than struck: faint, and dying in a few tenths.
        foreach (var (hz, a, t60) in new[] { (1210f, 0.10f, 0.6f), (3020f, 0.08f, 0.45f), (5480f, 0.05f, 0.3f), (8350f, 0.03f, 0.2f) })
            AddRing(y, sr, 0.05f, hz, a, t60, attackSeconds: 0.08f);
        Scrape(0.38f, 0.14f, 0.35f);
        // Home: the guard against the scabbard's mouth.
        AddClick(y, sr, 0.52f, 0.45f, rng, 1800f, 4f);
        AddRing(y, sr, 0.52f, 1210f, 0.06f, 0.4f, 0.001f);
        return Finish(y, sr, fadeOutMs: 30f);
    }

    /// <summary>A hand closing on a steel bar: a brush, and the bar knocking the palm's ring or the
    /// buckle, a short dull ring of steel.</summary>
    public static float[] RenderCrowbar(int sr, int seed)
    {
        var rng = new Random(seed);
        var y = Buffer(0.45f, sr);
        AddRustle(y, sr, 0.0f, 0.22f, 0.25f, rng);
        AddClick(y, sr, 0.18f, 0.6f, rng, 2500f, 3f);
        AddRing(y, sr, 0.18f, 1130f, 0.18f, 0.12f);
        AddRing(y, sr, 0.18f, 2960f, 0.10f, 0.08f);
        return Finish(y, sr, fadeOutMs: 30f);
    }

    /// <summary>A torch taken in hand: a brush, and its switch clicked on and off.</summary>
    public static float[] RenderTorch(int sr, int seed)
    {
        var rng = new Random(seed);
        var y = Buffer(0.45f, sr);
        AddRustle(y, sr, 0.0f, 0.2f, 0.25f, rng);
        AddClick(y, sr, 0.22f, 0.8f, rng, 4200f, 1.2f);
        AddClick(y, sr, 0.33f, 0.7f, rng, 4600f, 1.0f);
        return Finish(y, sr, fadeOutMs: 20f);
    }

    /// <summary>Anything else: the hand and sleeve brushing it, quiet.</summary>
    public static float[] RenderSoft(int sr, int seed)
    {
        var rng = new Random(seed);
        var y = Buffer(0.45f, sr);
        AddRustle(y, sr, 0.0f, 0.32f, 0.5f, rng, 400f, 4000f);
        return Finish(y, sr, fadeOutMs: 30f);
    }
}
