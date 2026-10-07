using System.Diagnostics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// Every candidate sound for the admin gun, the fire selector, the teleporter and things handed over,
/// written out for listening and measured.
///
///   --admin-gun [out=DIR]
///
/// The report variants are written as the game renders them (the calibre's report and the layer, under
/// the report's own ceiling) at one gain, so a variant and the plain report beside it compare as they
/// would in the game. Everything else is brought to -3 dBFS peak: each is a designed sound whose level
/// in the game is its declared level, not its file. 00-tour.wav says each one's name and plays it.
/// Every file is measured: length, peak, RMS, samples at full scale, non-finite samples, octave bands.
/// </summary>
public static class AdminGunSpike
{
    private const int Sr = TransientSynth.SampleRate;

    private sealed record Render(string File, string Spoken, string What, float[] Pcm);

    public static int Run(string[] args)
    {
        string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..]
                     ?? LabPaths.InRepo("inbox", "admin-gun-2026-10-05");
        Directory.CreateDirectory(dir);
        var all = new List<Render>();

        // ── Reports ─────────────────────────────────────────────────────────────────────────────
        int n = 1;
        foreach (var calibre in new[] { WeaponRegistry.Ar15, WeaponRegistry.Glock })
        {
            string cal = calibre.Id;
            all.Add(new($"{n++:00}-report-{cal}-plain.wav", $"The {calibre.DisplayName} report on its own, for comparison.",
                        $"the {calibre.DisplayName}'s own synthesised report, nothing added: the reference", AdminGunSynth.Report(calibre, 0, 1)));
            for (int v = 1; v <= AdminGun.ReportVariants; v++)
                all.Add(new($"{n++:00}-report-{cal}-v{v}.wav", $"Admin gun report variant {v}, {Word(v)}, with the {calibre.DisplayName}.",
                            $"admin gun, variant {v}, {calibre.DisplayName} calibre: {AdminGun.DescribeReport(v)}", AdminGunSynth.Report(calibre, v, 1)));
        }
        // Three shots in a row with the default, as somebody hearing a burst of them would.
        all.Add(new($"{n++:00}-report-ar15-v1-three-shots.wav", "Variant one, three shots.", "admin gun, variant 1, three shots half a second apart",
                    Sequence(Enumerable.Range(1, 3).Select(s => (AdminGunSynth.Report(WeaponRegistry.Ar15, 1, s), 0.5f)).ToList(), 1f)));

        // ── At the target ───────────────────────────────────────────────────────────────────────
        foreach (var m in new[] { AdminGunMode.Vaporize, AdminGunMode.Freeze, AdminGunMode.Inspect })
            all.Add(new($"{n++:00}-hit-{AdminGun.Spoken(m)}.wav", $"{AdminGun.Spoken(m)}, at the target.", HitWhat(m), Level(AdminGun.RenderHit(m, Sr, 1))));

        // ── The mode switch and the selectors ───────────────────────────────────────────────────
        all.Add(new($"{n++:00}-admin-mode-switch.wav", "Admin gun mode switches: kill, vaporize, freeze, inspect.",
                    "X on the admin gun: a click and a note per mode, kill 440 Hz, vaporize 660, freeze 990, inspect 1485",
                    Level(Sequence(AdminGun.Modes.Select(m => (AdminGun.RenderMode(m, Sr, 1), 0.6f)).ToList(), 0f))));
        foreach (var w in WeaponRegistry.All.Where(FireSelector.Has).OrderBy(w => w.Id))
            all.Add(new($"{n++:00}-selector-{w.Id}.wav", $"{w.DisplayName} selector.", $"X on the {w.DisplayName}: its {(FireSelector.HasAuto(w) ? "fire selector" : "safety")} moved one detent",
                        Level(WeaponHandling.Render(new HandlingSpec(w.Id, false, 0, false, IsSelector: true), Sr, 1))));

        // ── The teleporter ──────────────────────────────────────────────────────────────────────
        foreach (var kind in new[] { TeleporterSounds.Charge, TeleporterSounds.Leave, TeleporterSounds.Arrive, TeleporterSounds.Ready })
            all.Add(new($"{n++:00}-teleporter-{kind}.wav", $"Teleporter, {kind}.", TeleWhat(kind), Level(TeleporterSounds.Render(kind, Sr, 1))));
        // In order, at their declared levels against each other: charge, leave; then the arrival and,
        // a second later, the ready tone. (Heard by different people in the game; together here.)
        all.Add(new($"{n++:00}-teleporter-sequence.wav", "Teleporter, the whole trip.", "charge, leave, then arrive and the ready tone a second later, at their levels against each other",
                    Level(Sequence(new List<(float[], float)>
                    {
                        (Db(TeleporterSounds.Render(TeleporterSounds.Charge, Sr, 1), TeleporterSounds.LevelDb(TeleporterSounds.Charge)), 0f),
                        (Db(TeleporterSounds.Render(TeleporterSounds.Leave, Sr, 1), TeleporterSounds.LevelDb(TeleporterSounds.Leave)), 0.6f),
                        (Db(TeleporterSounds.Render(TeleporterSounds.Arrive, Sr, 1), TeleporterSounds.LevelDb(TeleporterSounds.Arrive)), 1.0f),
                        (Db(TeleporterSounds.Render(TeleporterSounds.Ready, Sr, 1), TeleporterSounds.LevelDb(TeleporterSounds.Ready)), 0f),
                    }, 0f))));

        // ── Handed over ─────────────────────────────────────────────────────────────────────────
        foreach (var prefab in new[] { "akm_rifle", "glock_pistol", "m700_rifle", AdminGun.PrefabId, "sword", "teleporter", "crowbar", "torch", "box" })
            all.Add(new($"{n++:00}-give-{prefab}.wav", $"Given {prefab.Replace('_', ' ')}.", GiveWhat(prefab), Level(HandOverSounds.Render(prefab, Sr, 1))));

        // ── Write, measure, tour ────────────────────────────────────────────────────────────────
        Console.WriteLine($"Writing to {dir}\n");
        Console.WriteLine($"  {"file",-36} {"secs",5} {"peak",6} {"rms",6} {"clip",4} {"nan",3}  octave bands 63..16k dB re loudest");
        var table = new List<string>();
        int bad = 0;
        foreach (var r in all)
        {
            File.WriteAllBytes(Path.Combine(dir, r.File), WeaponSynth.ToWav16(r.Pcm, Sr));
            var m = Measure(r.Pcm);
            if (m.NonFinite > 0 || m.Clipped > 0) bad++;
            string line = $"{r.File,-36} {r.Pcm.Length / (float)Sr,5:F2} {m.PeakDb,6:F1} {m.RmsDb,6:F1} {m.Clipped,4} {m.NonFinite,3}  {string.Join(" ", m.Bands.Select(b => b < -60 ? "  ." : $"{b,3:F0}"))}";
            Console.WriteLine("  " + line);
            table.Add(line);
        }

        var tour = new List<float>();
        tour.AddRange(Speak("Admin gun, teleporter and hand over sounds. Fifth of October."));
        tour.AddRange(new float[(int)(0.6f * Sr)]);
        foreach (var r in all)
        {
            tour.AddRange(Speak(r.Spoken));
            tour.AddRange(new float[(int)(0.35f * Sr)]);
            tour.AddRange(r.Pcm);
            tour.AddRange(new float[(int)(0.9f * Sr)]);
        }
        File.WriteAllBytes(Path.Combine(dir, "00-tour.wav"), WeaponSynth.ToWav16(tour.ToArray(), Sr));

        WriteReadme(dir, all, table);
        Console.WriteLine($"\n{all.Count} files and 00-tour.wav ({tour.Count / (float)Sr:F0} s). {(bad == 0 ? "No file clips or holds a non-finite sample." : $"{bad} file(s) clip or hold non-finite samples.")}");
        return bad == 0 ? 0 : 1;
    }

    private static string Word(int v) => v switch { 1 => "ring", 2 => "drop", 3 => "zap", 4 => "flam", 5 => "ring and drop", _ => "plain" };

    private static string HitWhat(AdminGunMode m) => m switch
    {
        AdminGunMode.Vaporize => "where a vaporize round lands: a rising sizzle, then air rushing into where the thing was, ending in a low thump",
        AdminGunMode.Freeze => $"on whatever is frozen: a stun gun's spark crackle, {AdminGun.StunPulsesPerSecond:F0} cracks a second over a 100 Hz buzz and a faint oscillator whine",
        _ => "where an inspect round lands: a soft scanning tone rising an octave with a flutter",
    };

    private static string TeleWhat(string kind) => kind switch
    {
        TeleporterSounds.Charge => "charging: a flash charger's whine rising from 2 to 10 kHz over two seconds, over a faint 120 Hz buzz",
        TeleporterSounds.Leave => "leaving: 75 litres of air rushing into where the body was, a faint rush and a low thump when it meets",
        TeleporterSounds.Arrive => "arriving: the same air shoved aside, a sharp pop and a short rush away",
        _ => "ready: two soft notes, E6 and A6",
    };

    private static string GiveWhat(string prefab) => HandOverSounds.KindOf(prefab, out var w) switch
    {
        HandOverSounds.Kind.Gun => $"given a gun ({prefab}): the {w!.DisplayName}'s action worked once, as a gun is checked when it is handed over",
        HandOverSounds.Kind.Sword => "given a sword: drawn a hand's width from its scabbard, the blade singing faintly, and let back with a knock",
        HandOverSounds.Kind.Teleporter => "given the teleporter: its ready tone",
        HandOverSounds.Kind.Crowbar => "given a crowbar: a hand closing on it and a dull knock of steel",
        HandOverSounds.Kind.Torch => "given a torch: a hand closing on it and its switch clicked on and off",
        _ => $"given anything else ({prefab}): a soft brush of the hand",
    };

    /// <summary>Brought to -3 dBFS peak.</summary>
    private static float[] Level(float[] x)
    {
        float peak = x.Length == 0 ? 0f : x.Max(v => MathF.Abs(v));
        if (peak <= 1e-9f) return x;
        float g = 0.708f / peak;
        return x.Select(v => v * g).ToArray();
    }

    /// <summary>At its declared level against a 100 dB full scale, for a sequence of several.</summary>
    private static float[] Db(float[] x, float levelDb)
    {
        float g = MathF.Pow(10f, (levelDb - 100f) / 20f);
        return x.Select(v => v * g).ToArray();
    }

    /// <summary>Sounds one after another, each followed by its gap.</summary>
    private static float[] Sequence(List<(float[] Pcm, float Gap)> parts, float _)
    {
        var y = new List<float>();
        foreach (var (pcm, gap) in parts)
        {
            y.AddRange(pcm);
            y.AddRange(new float[(int)(gap * Sr)]);
        }
        return y.ToArray();
    }

    private sealed record Measured(float PeakDb, float RmsDb, int Clipped, int NonFinite, float[] Bands);

    private static readonly float[] Centres = { 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    private static Measured Measure(float[] x)
    {
        int nonFinite = 0, clipped = 0;
        double sum = 0;
        float peak = 0f;
        foreach (float v in x)
        {
            if (!float.IsFinite(v)) { nonFinite++; continue; }
            peak = MathF.Max(peak, MathF.Abs(v));
            if (MathF.Abs(v) >= 0.999f) clipped++;
            sum += (double)v * v;
        }
        float rms = (float)Math.Sqrt(sum / Math.Max(1, x.Length));
        var bands = new float[Centres.Length];
        for (int b = 0; b < Centres.Length; b++)
        {
            // An octave band-pass run twice, as the handling spec was measured.
            double w = 2 * Math.PI * Centres[b] / Sr, alpha = Math.Sin(w) / (2 * 1.41), a0 = 1 + alpha;
            double b0 = alpha / a0, b2 = -alpha / a0, a1 = -2 * Math.Cos(w) / a0, a2 = (1 - alpha) / a0;
            double e = 0;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0, u1 = 0, u2 = 0, z1 = 0, z2 = 0;
            foreach (float v0 in x)
            {
                double v = float.IsFinite(v0) ? v0 : 0;
                double y0 = b0 * v + b2 * x2 - a1 * y1 - a2 * y2; x2 = x1; x1 = v; y2 = y1; y1 = y0;
                double z0 = b0 * y0 + b2 * u2 - a1 * z1 - a2 * z2; u2 = u1; u1 = y0; z2 = z1; z1 = z0;
                e += z0 * z0;
            }
            bands[b] = (float)(10 * Math.Log10(e + 1e-20));
        }
        float top = bands.Max();
        for (int b = 0; b < bands.Length; b++) bands[b] -= top;
        return new Measured(20f * MathF.Log10(peak + 1e-12f), 20f * MathF.Log10(rms + 1e-12f), clipped, nonFinite, bands);
    }

    /// <summary>espeak-ng's voice at the mixer's rate, at about -12 dBFS; silence if it is not there.</summary>
    private static float[] Speak(string text)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"admingun-say-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo("espeak-ng") { UseShellExecute = false, RedirectStandardError = true };
            psi.ArgumentList.Add("-s"); psi.ArgumentList.Add("170");
            psi.ArgumentList.Add("-w"); psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add(text);
            using (var p = Process.Start(psi)!) p.WaitForExit();
            var bytes = File.ReadAllBytes(tmp);
            int rate = BitConverter.ToInt32(bytes, 24);
            var x = WeaponSynth.ReadWav16Mono(bytes);
            var y = new float[(int)((long)x.Length * Sr / Math.Max(1, rate))];
            for (int i = 0; i < y.Length; i++)
            {
                double at = i * (double)rate / Sr;
                int k = (int)at;
                float f = (float)(at - k);
                float a = x[Math.Min(k, x.Length - 1)], b = x[Math.Min(k + 1, x.Length - 1)];
                y[i] = a + (b - a) * f;
            }
            float peak = y.Length == 0 ? 0f : y.Max(v => MathF.Abs(v));
            if (peak > 0f) for (int i = 0; i < y.Length; i++) y[i] *= 0.25f / peak;
            return y;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (no speech for \"{text}\": {ex.Message})");
            return new float[(int)(0.3f * Sr)];
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static void WriteReadme(string dir, List<Render> all, List<string> table)
    {
        var lines = new List<string>
        {
            "Admin gun, fire selector, teleporter and hand-over sounds, 2026-10-05",
            "",
            "All synthesised; no recordings. Rendered by the AudioLab: --admin-gun.",
            "00-tour.wav says each file's name and plays it, in this order.",
            "",
            "The report variants are at one gain, as the game renders them, so each can be",
            "compared with the plain report beside it. Everything else is at -3 dBFS peak;",
            "in the game each plays at its own level (given below each name where it matters).",
            "",
            "The game plays report variant 1 until you pick one. /admingun report N switches it",
            "in the game for trying them out.",
            "",
            "Files:",
            "",
        };
        foreach (var r in all) lines.Add($"{r.File}: {r.What}.");
        lines.Add("");
        lines.Add("Levels in the game, dB SPL peak at a metre: the report is the calibre's own (159-167);");
        lines.Add($"vaporize {AdminGun.HitLevelDb(AdminGunMode.Vaporize):F0}, freeze {AdminGun.HitLevelDb(AdminGunMode.Freeze):F0}, inspect {AdminGun.HitLevelDb(AdminGunMode.Inspect):F0}, mode switch {AdminGun.ModeLevelDb:F0};");
        lines.Add($"teleporter charge {TeleporterSounds.LevelDb(TeleporterSounds.Charge):F0}, leave {TeleporterSounds.LevelDb(TeleporterSounds.Leave):F0}, arrive {TeleporterSounds.LevelDb(TeleporterSounds.Arrive):F0}, ready {TeleporterSounds.LevelDb(TeleporterSounds.Ready):F0}.");
        lines.Add("");
        lines.Add("Measured (seconds, peak dBFS, RMS dBFS, samples at full scale, non-finite samples,");
        lines.Add("octave bands 63 Hz to 16 kHz in dB against the loudest band, '.' below -60):");
        lines.Add("");
        lines.AddRange(table);
        File.WriteAllLines(Path.Combine(dir, "README.txt"), lines);
    }
}
