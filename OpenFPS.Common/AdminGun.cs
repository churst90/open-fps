using System;
using System.Collections.Generic;
using System.Numerics;
using static OpenFPS.Common.DesignedSoundKit;

namespace OpenFPS.Common;

/// <summary>What the admin gun does to what it hits.</summary>
public enum AdminGunMode
{
    /// <summary>Anybody it hits dies, in one shot.</summary>
    Kill,
    /// <summary>What it hits is gone: removed from the world. A player is killed instead.</summary>
    Vaporize,
    /// <summary>What it hits cannot move for <see cref="AdminGun.FreezeSeconds"/>: a player, somebody
    /// walking, a vehicle.</summary>
    Freeze,
    /// <summary>Says what it hit: its name, what kind of thing, whose, its id, its material, where.</summary>
    Inspect,
}

/// <summary>
/// The admin gun: the one thing in the game that is not a real object, so its sounds are DESIGNED, not
/// modelled. Everything a player hears of it is said here: the report (the chosen calibre's own
/// synthesised report with a layer that marks it as this gun, the same in every mode so anyone hearing
/// it knows what was fired), the mode switch, and what happens where the round lands.
///
/// The report is put together in the client (it needs <c>WeaponSynth</c>), through
/// <see cref="ComposeReport"/>; the layers and the effects render here.
/// </summary>
public static class AdminGun
{
    /// <summary>What the item's <c>WeaponId</c> says: not a <see cref="WeaponRegistry"/> weapon, because
    /// it has no ballistics of its own. It fires whatever calibre it is set to.</summary>
    public const string WeaponId = "admingun";

    /// <summary>Its prefab.</summary>
    public const string PrefabId = "admin_gun";

    /// <summary>How long a frozen thing stays frozen, seconds.</summary>
    public const float FreezeSeconds = 10f;

    /// <summary>The calibre it fires until told otherwise.</summary>
    public const string DefaultCalibre = "ar15";

    public static readonly AdminGunMode[] Modes = { AdminGunMode.Kill, AdminGunMode.Vaporize, AdminGunMode.Freeze, AdminGunMode.Inspect };

    public static string Spoken(AdminGunMode m) => m switch
    {
        AdminGunMode.Kill => "kill",
        AdminGunMode.Vaporize => "vaporize",
        AdminGunMode.Freeze => "freeze",
        _ => "inspect",
    };

    public static bool TryParseMode(string? word, out AdminGunMode mode)
    {
        mode = AdminGunMode.Kill;
        switch ((word ?? "").Trim().ToLowerInvariant())
        {
            case "kill": mode = AdminGunMode.Kill; return true;
            case "vaporize": case "vaporise": case "remove": case "delete": mode = AdminGunMode.Vaporize; return true;
            case "freeze": case "stun": mode = AdminGunMode.Freeze; return true;
            case "inspect": case "what": case "identify": mode = AdminGunMode.Inspect; return true;
            default: return false;
        }
    }

    /// <summary>The mode one step on or back, round and round.</summary>
    public static AdminGunMode Step(AdminGunMode now, int direction)
    {
        int i = Array.IndexOf(Modes, now);
        if (i < 0) i = 0;
        int n = Modes.Length;
        return Modes[((i + (direction < 0 ? -1 : 1)) % n + n) % n];
    }

    /// <summary>The calibre one step on or back through the registry, by id, round and round.</summary>
    public static string StepCalibre(string now, int direction)
    {
        var ids = new List<string>();
        foreach (var w in WeaponRegistry.All) ids.Add(w.Id);
        ids.Sort(StringComparer.Ordinal);
        int i = ids.FindIndex(x => x.Equals(now, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = 0;
        int n = ids.Count;
        return ids[((i + (direction < 0 ? -1 : 1)) % n + n) % n];
    }

    // ── The report ────────────────────────────────────────────────────────────────────────────

    /// <summary>The report variant played in the game until Cody picks one.</summary>
    public const int DefaultReport = 1;

    /// <summary>How many report variants there are, numbered from 1.</summary>
    public const int ReportVariants = 5;

    /// <summary>What each report variant is, for the lab's README and for /admingun.</summary>
    public static string DescribeReport(int variant) => variant switch
    {
        1 => "ring: a metal ring-down tuned to an A minor chord, a second and a half long",
        2 => "drop: a sub thump sweeping down from 180 to 40 Hz in a tenth of a second",
        3 => "zap: a fifth of a second of electric crackle and a falling buzz",
        4 => "flam: the report twice, the second 30 ms after the first",
        5 => "ring and drop together",
        _ => "the calibre's own report and nothing more",
    };

    public const string ReportPrefix = "admingun:";

    /// <summary>"admingun:akm:1": the admin gun firing with the AKM's ballistics and report, variant 1.</summary>
    public static string ReportKey(string calibre, int variant) => $"{ReportPrefix}{calibre}:{Math.Clamp(variant, 0, ReportVariants)}";

    public static bool TryParseReport(string? key, out string calibre, out int variant)
    {
        calibre = ""; variant = 0;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(ReportPrefix, StringComparison.Ordinal)) return false;
        var parts = key[ReportPrefix.Length..].Split(':');
        if (parts.Length != 2 || !WeaponRegistry.TryGet(parts[0], out _)) return false;
        if (!int.TryParse(parts[1], out variant) || variant < 0 || variant > ReportVariants) return false;
        calibre = parts[0];
        return true;
    }

    /// <summary>
    /// The admin gun's report: the calibre's own report (from <paramref name="blast"/>, a seed in and a
    /// report out at the level the weapon synthesiser makes every report) with the variant's layer on it.
    /// The report is left exactly as loud as the calibre's, so the declared level means the same. Each
    /// layer is set against the report by ENERGY over the first tenth of a second, which is about what
    /// the ear adds up for a short sound (<see cref="RingDb"/> and the rest), so it sits the same under a
    /// pistol as under a rifle. The whole is held under <see cref="ReportPeakCeiling"/>.
    /// </summary>
    public static float[] ComposeReport(Func<int, float[]> blast, int variant, int sr, int seed)
    {
        var shot = blast(seed);
        float energy = Energy(shot, shot.Length, sr);
        if (energy <= 0f || !float.IsFinite(energy)) energy = 1e-4f;
        var rng = new Random(seed * 7919 + variant);
        float[] y;
        switch (variant)
        {
            case 1: y = Pad(shot, 1.7f, sr); Layer(y, l => AddChordRing(l, sr, 1f), energy, RingDb, sr); break;
            case 2: y = Pad(shot, 0.45f, sr); Layer(y, l => AddDrop(l, sr, 1f), energy, DropDb, sr); break;
            case 3: y = Pad(shot, 0.35f, sr); Layer(y, l => AddZap(l, sr, 1f, rng), energy, ZapDb, sr); break;
            case 4:
            {
                var second = blast(seed + 1);
                y = Pad(shot, 0.03f + second.Length / (float)sr + 0.01f, sr);
                int at = (int)(FlamSeconds * sr);
                for (int i = 0; i < second.Length && at + i < y.Length; i++) y[at + i] += FlamLevel * second[i];
                break;
            }
            case 5:
                y = Pad(shot, 1.7f, sr);
                Layer(y, l => AddChordRing(l, sr, 1f), energy, RingDb, sr);
                Layer(y, l => AddDrop(l, sr, 1f), energy, DropDb, sr);
                break;
            default: y = Pad(shot, 0f, sr); break;
        }
        for (int i = 0; i < y.Length; i++) if (!float.IsFinite(y[i])) y[i] = 0f;
        // A short fade at the end only: the start is the shot's own and must keep its edge.
        int fade = Math.Min(y.Length, (int)(0.01f * sr));
        for (int i = 0; i < fade; i++) y[y.Length - 1 - i] *= i / (float)fade;
        float max = 0f;
        foreach (float v in y) max = MathF.Max(max, MathF.Abs(v));
        if (max > ReportPeakCeiling) { float g = ReportPeakCeiling / max; for (int i = 0; i < y.Length; i++) y[i] *= g; }
        return y;
    }

    /// <summary>The most a composed report may peak: the same ceiling as every report.</summary>
    public const float ReportPeakCeiling = 0.98f;

    /// <summary>Each layer's energy in its first tenth of a second against the report's, dB. The ring
    /// and the zap a little under the shot, so the shot is still what is heard first; the drop above it,
    /// because the ear is 20-30 dB less sensitive at 40-180 Hz than at 1-2 kHz where the shot is.</summary>
    public const float RingDb = -3f, DropDb = 8f, ZapDb = -3f;

    /// <summary>The flam's second report: how late, and how loud against the first.</summary>
    public const float FlamSeconds = 0.030f, FlamLevel = 0.8f;

    /// <summary>A layer rendered at unit level, brought to <paramref name="db"/> against the report's
    /// energy over the first tenth of a second, and added.</summary>
    private static void Layer(float[] y, Action<float[]> render, float reportEnergy, float db, int sr)
    {
        var layer = new float[y.Length];
        render(layer);
        float e = Energy(layer, (int)(0.1f * sr), sr);
        if (e <= 0f || !float.IsFinite(e)) return;
        float g = MathF.Sqrt(reportEnergy * MathF.Pow(10f, db / 10f) / e);
        for (int i = 0; i < y.Length; i++) y[i] += g * layer[i];
    }

    /// <summary>Sum of squares over the sample rate, over the first <paramref name="n"/> samples.</summary>
    private static float Energy(float[] x, int n, int sr)
    {
        double e = 0;
        for (int i = 0; i < Math.Min(n, x.Length); i++) if (float.IsFinite(x[i])) e += (double)x[i] * x[i];
        return (float)(e / sr);
    }

    private static float[] Pad(float[] x, float seconds, int sr)
    {
        var y = new float[Math.Max(x.Length, (int)(seconds * sr))];
        Array.Copy(x, y, x.Length);
        return y;
    }

    /// <summary>The chord the ring is tuned to: A minor, root to the octave's fifth. Each partial a
    /// steel bar's mode, the higher ones dying sooner.</summary>
    private static readonly (float Hz, float Level, float T60)[] Chord =
    {
        (440.00f, 1.00f, 1.6f), (523.25f, 0.80f, 1.4f), (659.26f, 0.75f, 1.2f),
        (880.00f, 0.50f, 0.9f), (1318.51f, 0.35f, 0.6f), (2637.02f, 0.12f, 0.3f),
    };

    /// <summary>The ring: the chord's modes starting with the shot, up in two milliseconds so the shot's
    /// own edge comes first.</summary>
    private static void AddChordRing(float[] y, int sr, float level)
    {
        float sum = 0f;
        foreach (var (_, l, _) in Chord) sum += l;
        foreach (var (hz, l, t60) in Chord)
            AddRing(y, sr, 0.0008f, hz, level * l / sum * 2.2f, t60, attackSeconds: 0.002f);
    }

    /// <summary>The drop: a sine whose pitch falls from 180 Hz to 40 in about a tenth of a second, up in
    /// three milliseconds and gone in about four tenths.</summary>
    private static void AddDrop(float[] y, int sr, float level)
    {
        int n = Math.Min(y.Length, (int)(0.42f * sr));
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr;
            float hz = 40f + 140f * MathF.Exp(-t / 0.035f);
            phase += 2 * Math.PI * hz / sr;
            float env = MathF.Min(1f, t / 0.003f) * MathF.Exp(-t / 0.09f);
            y[i] += level * env * (float)Math.Sin(phase);
        }
    }

    /// <summary>The zap: a spark gap's crackle (sparse clicks, densest at the start) over a buzz whose
    /// pitch falls, a fifth of a second.</summary>
    private static void AddZap(float[] y, int sr, float level, Random rng)
    {
        float len = 0.2f;
        int start = (int)(0.002f * sr), n = Math.Min(y.Length - start, (int)(len * sr));
        var bright = Biquad.HighPass(2500f, 0.7f, sr);
        var buzzLp = Biquad.LowPass(3500f, 0.7f, sr);
        double phase = 0;
        float spark = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr;
            float env = MathF.Min(1f, t / 0.002f) * MathF.Exp(-t / 0.07f);
            // Crackle: a click whenever a spark jumps, most often at first.
            float rate = 900f * MathF.Exp(-t / 0.08f) + 60f;
            if (rng.NextDouble() < rate / sr) spark = 0.6f + 0.4f * (float)rng.NextDouble();
            float crack = bright.Run(spark * White(rng));
            spark *= 0.82f;
            // The buzz: a sawtooth falling from 1400 to 300 Hz, its top taken off.
            float hz = 300f + 1100f * MathF.Exp(-t / 0.05f);
            phase += hz / sr;
            float saw = (float)(2.0 * (phase - Math.Floor(phase)) - 1.0);
            float buzz = buzzLp.Run(saw);
            y[start + i] += level * env * (0.9f * crack + 0.5f * buzz);
        }
    }

    // ── The mode switch ───────────────────────────────────────────────────────────────────────

    public const string ModePrefix = "admingun-mode:";

    public static string ModeKey(AdminGunMode m) => ModePrefix + Spoken(m);

    public static bool TryParseModeKey(string? key, out AdminGunMode mode)
    {
        mode = AdminGunMode.Kill;
        return !string.IsNullOrEmpty(key) && key.StartsWith(ModePrefix, StringComparison.Ordinal)
            && TryParseMode(key[ModePrefix.Length..], out mode);
    }

    /// <summary>The note each mode's switch ends on: a rising fifth apart, so the four can be told
    /// apart before the word is said.</summary>
    public static float ModeHz(AdminGunMode m) => m switch
    {
        AdminGunMode.Kill => 440f,
        AdminGunMode.Vaporize => 660f,
        AdminGunMode.Freeze => 990f,
        _ => 1485f,
    };

    public const float ModeLevelDb = 58f;

    /// <summary>A switch: a small click and the mode's note, a tenth of a second.</summary>
    public static float[] RenderMode(AdminGunMode m, int sr, int seed)
    {
        var rng = new Random(seed);
        var y = Buffer(0.16f, sr);
        AddClick(y, sr, 0.002f, 0.6f, rng, 4000f, 1.2f);
        float hz = ModeHz(m);
        int a = (int)(0.018f * sr), n = (int)(0.11f * sr);
        for (int i = 0; i < n && a + i < y.Length; i++)
        {
            float t = i / (float)sr;
            float env = MathF.Min(1f, t / 0.008f) * MathF.Exp(-t / 0.035f);
            y[a + i] += 0.5f * env * (MathF.Sin(2f * MathF.PI * hz * t) + 0.25f * MathF.Sin(4f * MathF.PI * hz * t));
        }
        return Finish(y, sr);
    }

    // ── At the target ─────────────────────────────────────────────────────────────────────────

    public const string HitPrefix = "admingun-hit:";

    public static string HitKey(AdminGunMode m) => HitPrefix + Spoken(m);

    public static bool TryParseHitKey(string? key, out AdminGunMode mode)
    {
        mode = AdminGunMode.Kill;
        return !string.IsNullOrEmpty(key) && key.StartsWith(HitPrefix, StringComparison.Ordinal)
            && TryParseMode(key[HitPrefix.Length..], out mode) && mode != AdminGunMode.Kill;
    }

    /// <summary>Peak dB SPL at a metre of each effect: a vaporizing as loud as a door slammed hard, a
    /// stun gun's spark as loud as a real one's crack (they are loud, which is half their point), the
    /// scan as quiet as a word spoken.</summary>
    public static float HitLevelDb(AdminGunMode m) => m switch
    {
        AdminGunMode.Vaporize => 96f,
        AdminGunMode.Freeze => 98f,
        _ => 62f,
    };

    public static float HitSeconds(AdminGunMode m) => m switch
    {
        AdminGunMode.Vaporize => 0.95f,
        AdminGunMode.Freeze => 1.6f,
        _ => 0.55f,
    };

    /// <summary>The effect where the round landed, as a world sound.</summary>
    public static TransientSound HitSound(AdminGunMode m, Vector3 at) => new()
    {
        Character = SoundCharacter.Knock,
        Position = at,
        LevelDb = HitLevelDb(m),
        SynthKey = HitKey(m),
        DecaySeconds = HitSeconds(m) + 0.2f,
        Noisiness = 1f,
    };

    public static float[] RenderHit(AdminGunMode m, int sr, int seed) => m switch
    {
        AdminGunMode.Vaporize => RenderVaporize(sr, seed),
        AdminGunMode.Freeze => RenderFreeze(sr, seed),
        _ => RenderInspect(sr, seed),
    };

    /// <summary>
    /// Vaporize: a rising sizzle for most of half a second (noise through a band sweeping from 1.5 to
    /// 9 kHz, broken into grains like fat in a pan), and then the thing is gone and the air rushes into
    /// where it was: a swell of low noise that ends when the air meets in the middle, in a thump.
    /// </summary>
    public static float[] RenderVaporize(int sr, int seed)
    {
        var rng = new Random(seed);
        var y = Buffer(HitSeconds(AdminGunMode.Vaporize), sr);
        float sizzle = 0.45f;
        var band = new Biquad();
        int grain = Math.Max(1, (int)(0.004f * sr));
        float g0 = 0.5f, g1 = 0.5f;
        int n = (int)(sizzle * sr);
        for (int i = 0; i < n && i < y.Length; i++)
        {
            float t = i / (float)sr, x = t / sizzle;
            if (i % 32 == 0) band.SetBandPass(1500f * MathF.Pow(6f, x), 2.2f, sr);
            if (i % grain == 0) { g0 = g1; g1 = 0.25f + 0.75f * (float)rng.NextDouble(); }
            float g = g0 + (g1 - g0) * (i % grain) / grain;
            float env = MathF.Pow(MathF.Min(1f, 0.08f + x), 1.5f) * (x > 0.93f ? (1f - x) / 0.07f : 1f);
            y[i] += 0.55f * env * g * band.Run(White(rng));
        }
        // The rush: air from all round into the hole, low and quickening, cut off when it meets.
        var lp = Biquad.LowPass(1100f, 0.7f, sr);
        var hp = Biquad.HighPass(120f, 0.7f, sr);
        int rushFrom = (int)(0.40f * sr), meet = (int)(0.58f * sr);
        for (int i = rushFrom; i < meet && i < y.Length; i++)
        {
            float x = (i - rushFrom) / (float)(meet - rushFrom);
            y[i] += 0.5f * x * x * lp.Run(hp.Run(White(rng)));
        }
        // The meeting: a pulse and the air sloshing about it, low.
        float at = meet / (float)sr;
        AddRing(y, sr, at, 68f, 0.9f, 0.35f, attackSeconds: 0.002f);
        AddRing(y, sr, at, 141f, 0.35f, 0.2f, attackSeconds: 0.0015f);
        AddClick(y, sr, at, 0.3f, rng, 900f, 3f);
        return Finish(y, sr, fadeOutMs: 30f);
    }

    /// <summary>Real stun guns: a spark gap breaks down 15-20 times a second at tens of kilovolts, each a
    /// sharp crack, over the whine of the oscillator charging it. 17 a second here.</summary>
    public const float StunPulsesPerSecond = 17f;

    /// <summary>
    /// Freeze: a stun gun's crackle, a spark crack <see cref="StunPulsesPerSecond"/> times a second with a
    /// little jitter, over a buzz at 100 Hz and the faint whine of its oscillator.
    /// </summary>
    public static float[] RenderFreeze(int sr, int seed)
    {
        var rng = new Random(seed);
        float len = HitSeconds(AdminGunMode.Freeze);
        var y = Buffer(len, sr);
        // The hum and the whine, under it all.
        var humLp = Biquad.LowPass(1500f, 0.7f, sr);
        for (int i = 0; i < y.Length; i++)
        {
            float t = i / (float)sr;
            float env = MathF.Min(1f, t / 0.02f) * (t > len - 0.25f ? MathF.Max(0f, (len - t) / 0.25f) : 1f);
            float square = MathF.Sign(MathF.Sin(2f * MathF.PI * 100f * t));
            y[i] += env * (0.06f * humLp.Run(square) + 0.012f * MathF.Sin(2f * MathF.PI * 7800f * t));
        }
        // The sparks.
        float period = 1f / StunPulsesPerSecond;
        for (float t = 0.005f; t < len - 0.25f; t += period * (0.9f + 0.2f * (float)rng.NextDouble()))
        {
            float a = 0.7f + 0.3f * (float)rng.NextDouble();
            AddSpark(y, sr, t, a, rng);
        }
        return Finish(y, sr, fadeOutMs: 20f);
    }

    /// <summary>One spark: a channel of air breaking down in microseconds and the plasma ringing out over
    /// a couple of milliseconds; bright, with a body below it from the channel's expansion.</summary>
    private static void AddSpark(float[] y, int sr, float at, float amplitude, Random rng)
    {
        var hp = Biquad.HighPass(1800f, 0.7f, sr);
        int a = (int)(at * sr), n = (int)(0.006f * sr);
        for (int i = 0; i < n && a + i < y.Length; i++)
        {
            float t = i / (float)sr;
            float env = MathF.Exp(-t / 0.0006f);
            y[a + i] += amplitude * (0.9f * env * hp.Run(White(rng)) + 0.35f * MathF.Exp(-t / 0.0012f) * (i == 0 ? 1f : 0.2f * White(rng)));
        }
        AddRing(y, sr, at, 2300f + 400f * (float)rng.NextDouble(), 0.12f * amplitude, 0.012f, 0.0002f);
    }

    /// <summary>
    /// Inspect: a soft scanning tone, a sine and its octave rising from 700 to 1400 Hz with a gentle
    /// flutter at 14 a second, as a scanner passing over the thing. Quiet: it is a question, not a shot.
    /// </summary>
    public static float[] RenderInspect(int sr, int seed)
    {
        float len = HitSeconds(AdminGunMode.Inspect);
        var y = Buffer(len, sr);
        double phase = 0;
        float sweep = 0.38f;
        for (int i = 0; i < y.Length; i++)
        {
            float t = i / (float)sr;
            float x = MathF.Min(1f, t / sweep);
            float hz = 700f * MathF.Pow(2f, x);
            phase += 2 * Math.PI * hz / sr;
            float env = MathF.Min(1f, t / 0.03f) * (t > len - 0.15f ? MathF.Max(0f, (len - t) / 0.15f) : 1f);
            float flutter = 1f - 0.35f * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * 14f * t));
            y[i] += env * flutter * (float)(Math.Sin(phase) + 0.3 * Math.Sin(2 * phase));
        }
        return Finish(y, sr, fadeOutMs: 10f);
    }

    // ── The report as a world sound ───────────────────────────────────────────────────────────

    /// <summary>Whether a synth key is any of the admin gun's: the client routes it here.</summary>
    public static bool IsKey(string? key)
        => !string.IsNullOrEmpty(key) && (key.StartsWith(ReportPrefix, StringComparison.Ordinal)
                                          || key.StartsWith(ModePrefix, StringComparison.Ordinal)
                                          || key.StartsWith(HitPrefix, StringComparison.Ordinal));
}
