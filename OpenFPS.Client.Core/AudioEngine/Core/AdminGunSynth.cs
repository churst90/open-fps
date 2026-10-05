using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// The designed sounds by key: the admin gun's report (the calibre's own report from
/// <see cref="WeaponSynth"/> with the admin gun's layer, <see cref="AdminGun.ComposeReport"/>), its mode
/// switch and what it does where the round lands; the teleporter's four sounds; and the sound of a thing
/// handed over. Rendered off the mixer thread like every one-shot.
/// </summary>
public static class AdminGunSynth
{
    public static bool TryRender(string? key, int seed, out float[] pcm)
    {
        pcm = null!;
        if (string.IsNullOrEmpty(key)) return false;
        int sr = TransientSynth.SampleRate;
        if (AdminGun.TryParseReport(key, out string calibre, out int variant) && WeaponRegistry.TryGet(calibre, out var weapon))
        {
            pcm = Report(weapon, variant, seed);
            return true;
        }
        if (AdminGun.TryParseModeKey(key, out var mode)) { pcm = AdminGun.RenderMode(mode, sr, seed); return true; }
        if (AdminGun.TryParseHitKey(key, out var hit)) { pcm = AdminGun.RenderHit(hit, sr, seed); return true; }
        if (TeleporterSounds.TryParseKey(key, out string kind)) { pcm = TeleporterSounds.Render(kind, sr, seed); return true; }
        if (HandOverSounds.TryParseKey(key, out string prefab)) { pcm = HandOverSounds.Render(prefab, sr, seed); return true; }
        return false;
    }

    /// <summary>The admin gun's report with this calibre's ballistics.</summary>
    public static float[] Report(WeaponDefinition calibre, int variant, int seed)
    {
        var profile = WeaponProfile.From(calibre);
        return AdminGun.ComposeReport(s => WeaponSynth.MuzzleBlast(profile, s), variant, TransientSynth.SampleRate, seed);
    }
}
